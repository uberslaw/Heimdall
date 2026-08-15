using System.Buffers.Binary;
using System.IO;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Microsoft.Win32.SafeHandles;

namespace Heimdall.DiskUsage.Services;

/// <summary>
/// Opens a local NTFS volume for raw reads (requires Administrator).
/// Parses the boot sector and streams the Master File Table.
/// </summary>
[SupportedOSPlatform("windows")]
internal sealed class NtfsVolume : IDisposable
{
    const uint GenericRead = 0x80000000;
    const uint FileShareRead = 0x00000001;
    const uint FileShareWrite = 0x00000002;
    const uint OpenExisting = 3;
    const uint FileAttributeNormal = 0x00000080;

    readonly FileStream _stream;

    public int BytesPerSector { get; }
    public int BytesPerCluster { get; }
    public int BytesPerFileRecord { get; }
    public long MftStartCluster { get; }

    NtfsVolume(
        FileStream stream,
        int bytesPerSector,
        int sectorsPerCluster,
        int bytesPerFileRecord,
        long mftStartCluster)
    {
        _stream = stream;
        BytesPerSector = bytesPerSector;
        BytesPerCluster = bytesPerSector * sectorsPerCluster;
        BytesPerFileRecord = bytesPerFileRecord;
        MftStartCluster = mftStartCluster;
    }

    public static bool TryOpen(string driveLetter, out NtfsVolume? volume, out string failureReason)
    {
        volume = null;
        failureReason = "";

        if (string.IsNullOrWhiteSpace(driveLetter) || driveLetter.Length < 1)
        {
            failureReason = "Invalid drive letter.";
            return false;
        }

        var letter = char.ToUpperInvariant(driveLetter.Trim()[0]);
        if (letter < 'A' || letter > 'Z')
        {
            failureReason = "Invalid drive letter.";
            return false;
        }

        try
        {
            var root = letter + @":\";
            var di = new DriveInfo(root);
            if (!di.IsReady)
            {
                failureReason = "Drive is not ready.";
                return false;
            }

            if (!string.Equals(di.DriveFormat, "NTFS", StringComparison.OrdinalIgnoreCase))
            {
                failureReason = $"Volume is {di.DriveFormat}, not NTFS.";
                return false;
            }
        }
        catch (Exception ex)
        {
            failureReason = ex.Message;
            return false;
        }

        var volumePath = @"\\.\" + letter + ":";
        var handle = CreateFile(
            volumePath,
            GenericRead,
            FileShareRead | FileShareWrite,
            IntPtr.Zero,
            OpenExisting,
            FileAttributeNormal,
            IntPtr.Zero);

        if (handle.IsInvalid)
        {
            var err = Marshal.GetLastWin32Error();
            failureReason = err is 5 or 32
                ? "Access denied opening volume (run as Administrator)."
                : $"CreateFile failed (Win32 {err}).";
            handle.Dispose();
            return false;
        }

        try
        {
            var stream = new FileStream(handle, FileAccess.Read, bufferSize: 1024 * 1024, isAsync: false);
            var boot = new byte[512];
            if (stream.Read(boot, 0, boot.Length) < 512)
            {
                failureReason = "Could not read boot sector.";
                stream.Dispose();
                return false;
            }

            if (boot[3] != (byte)'N' || boot[4] != (byte)'T' || boot[5] != (byte)'F' || boot[6] != (byte)'S')
            {
                failureReason = "Boot sector is not NTFS.";
                stream.Dispose();
                return false;
            }

            var bytesPerSector = BinaryPrimitives.ReadUInt16LittleEndian(boot.AsSpan(0x0B));
            var sectorsPerCluster = boot[0x0D];
            if (bytesPerSector == 0 || sectorsPerCluster == 0)
            {
                failureReason = "Invalid NTFS BPB.";
                stream.Dispose();
                return false;
            }

            var bytesPerCluster = bytesPerSector * sectorsPerCluster;
            var mftStartCluster = (long)BinaryPrimitives.ReadUInt64LittleEndian(boot.AsSpan(0x30));

            var rawCFR = unchecked((sbyte)boot[0x40]);
            int bytesPerFileRecord = rawCFR < 0
                ? 1 << (-rawCFR)
                : rawCFR * bytesPerCluster;

            if (bytesPerFileRecord < 512 || bytesPerFileRecord > 4096 * 16)
            {
                failureReason = $"Unexpected MFT record size ({bytesPerFileRecord}).";
                stream.Dispose();
                return false;
            }

            volume = new NtfsVolume(stream, bytesPerSector, sectorsPerCluster, bytesPerFileRecord, mftStartCluster);
            return true;
        }
        catch (Exception ex)
        {
            handle.Dispose();
            failureReason = ex.Message;
            return false;
        }
    }

    public void ReadExact(long absoluteOffset, Span<byte> destination)
    {
        _stream.Seek(absoluteOffset, SeekOrigin.Begin);
        var total = 0;
        while (total < destination.Length)
        {
            var n = _stream.Read(destination[total..]);
            if (n <= 0)
                throw new EndOfStreamException($"Unexpected EOF at volume offset {absoluteOffset + total}.");
            total += n;
        }
    }

    public void ReadClusters(long startCluster, Span<byte> destination) =>
        ReadExact(startCluster * (long)BytesPerCluster, destination);

    public List<(long StartCluster, long ClusterCount)> ReadMftDataRuns(out long mftDataSizeBytes)
    {
        mftDataSizeBytes = 0;
        var record = new byte[BytesPerFileRecord];
        ReadClusters(MftStartCluster, record);
        MftRecord.ApplyFixups(record, BytesPerSector);

        if (!MftRecord.HasFileSignature(record))
            throw new InvalidDataException("$MFT record signature missing.");

        if (MftRecord.TryGetUnnamedDataInfo(record, out var runs, out var dataSize) && runs.Count > 0)
        {
            mftDataSizeBytes = dataSize;
            return runs;
        }

        var clusters = Math.Max(1, (BytesPerFileRecord + BytesPerCluster - 1) / BytesPerCluster);
        mftDataSizeBytes = BytesPerFileRecord;
        return [(MftStartCluster, clusters)];
    }

    /// <summary>
    /// Streams MFT FILE records. Invokes <paramref name="onRecord"/> for each record (fixups applied in-place on a reusable buffer).
    /// Stops after the used $MFT data size (not the full allocated/sparse extent).
    /// </summary>
    public void ForEachMftRecord(
        Action<long, byte[]> onRecord,
        IProgress<(long RecordsRead, long BytesRead)>? progress,
        CancellationToken ct)
    {
        var runs = ReadMftDataRuns(out var mftDataSizeBytes);
        var recordSize = BytesPerFileRecord;
        var maxRecords = mftDataSizeBytes > 0
            ? Math.Max(1, mftDataSizeBytes / recordSize)
            : long.MaxValue;

        var bufferSize = Math.Min(64 * 1024 * 1024, Math.Max(BytesPerCluster * 256, recordSize * 4096));
        bufferSize -= bufferSize % BytesPerCluster;
        if (bufferSize < BytesPerCluster)
            bufferSize = BytesPerCluster;

        var buffer = new byte[bufferSize];
        var record = new byte[recordSize];
        long recordIndex = 0;
        long bytesReadTotal = 0;
        var lastReport = DateTime.UtcNow;

        foreach (var (startCluster, clusterCount) in runs)
        {
            ct.ThrowIfCancellationRequested();
            if (recordIndex >= maxRecords)
                break;

            long clustersRemaining = clusterCount;
            long cluster = startCluster;

            while (clustersRemaining > 0 && recordIndex < maxRecords)
            {
                ct.ThrowIfCancellationRequested();
                var chunkClusters = Math.Min(clustersRemaining, bufferSize / BytesPerCluster);
                var chunkBytes = (int)(chunkClusters * BytesPerCluster);
                ReadClusters(cluster, buffer.AsSpan(0, chunkBytes));

                var offset = 0;
                while (offset + recordSize <= chunkBytes && recordIndex < maxRecords)
                {
                    Buffer.BlockCopy(buffer, offset, record, 0, recordSize);
                    MftRecord.ApplyFixups(record, BytesPerSector);
                    onRecord(recordIndex, record);
                    recordIndex++;
                    offset += recordSize;
                }

                bytesReadTotal += chunkBytes;
                cluster += chunkClusters;
                clustersRemaining -= chunkClusters;

                var now = DateTime.UtcNow;
                if (progress is not null && (now - lastReport).TotalMilliseconds >= 300)
                {
                    lastReport = now;
                    progress.Report((recordIndex, bytesReadTotal));
                }
            }
        }

        progress?.Report((recordIndex, bytesReadTotal));
    }

    public void Dispose() => _stream.Dispose();

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    static extern SafeFileHandle CreateFile(
        string lpFileName,
        uint dwDesiredAccess,
        uint dwShareMode,
        IntPtr lpSecurityAttributes,
        uint dwCreationDisposition,
        uint dwFlagsAndAttributes,
        IntPtr hTemplateFile);
}

/// <summary>Helpers for parsing a single NTFS FILE record (fixups already applied).</summary>
internal static class MftRecord
{
    public const long RootDirectoryIndex = 5;

    public static bool HasFileSignature(ReadOnlySpan<byte> record) =>
        record.Length >= 4
        && record[0] == (byte)'F'
        && record[1] == (byte)'I'
        && record[2] == (byte)'L'
        && record[3] == (byte)'E';

    public static void ApplyFixups(Span<byte> record, int bytesPerSector)
    {
        if (record.Length < 8) return;
        var usaOffset = BinaryPrimitives.ReadUInt16LittleEndian(record[4..]);
        var usaCount = BinaryPrimitives.ReadUInt16LittleEndian(record[6..]);
        if (usaOffset == 0 || usaCount < 2) return;
        if (usaOffset + usaCount * 2 > record.Length) return;

        for (var i = 1; i < usaCount; i++)
        {
            var sectorEnd = i * bytesPerSector - 2;
            if (sectorEnd < 0 || sectorEnd + 2 > record.Length) break;
            var src = usaOffset + i * 2;
            record[sectorEnd] = record[src];
            record[sectorEnd + 1] = record[src + 1];
        }
    }

    public static bool TryGetUnnamedDataInfo(
        ReadOnlySpan<byte> record,
        out List<(long StartCluster, long ClusterCount)> runs,
        out long dataSize)
    {
        runs = [];
        dataSize = 0;
        if (!TryGetAttributeRange(record, out var offset, out var end))
            return false;

        while (offset + 8 <= end)
        {
            var type = BinaryPrimitives.ReadUInt32LittleEndian(record[offset..]);
            if (type == 0xFFFFFFFF) break;
            var length = BinaryPrimitives.ReadInt32LittleEndian(record[(offset + 4)..]);
            if (length < 16 || offset + length > record.Length) break;

            var nonResident = record[offset + 8] != 0;
            var nameLength = record[offset + 9];
            if (type == 0x80 && nameLength == 0 && nonResident && offset + 0x40 <= record.Length)
            {
                dataSize = BinaryPrimitives.ReadInt64LittleEndian(record[(offset + 0x30)..]);
                var runsOff = BinaryPrimitives.ReadUInt16LittleEndian(record[(offset + 0x20)..]);
                if (runsOff > 0 && runsOff < length)
                {
                    runs = DecodeDataRuns(record.Slice(offset + runsOff, length - runsOff));
                    if (runs.Count > 0) return true;
                }
            }

            offset += length;
        }

        return false;
    }

    public static bool TryGetUnnamedDataRuns(ReadOnlySpan<byte> record, out List<(long StartCluster, long ClusterCount)> runs) =>
        TryGetUnnamedDataInfo(record, out runs, out _);

    /// <summary>
    /// Parse an in-use FILE record. Extension records (base ≠ 0) return <see cref="ParsedRecord.IsExtension"/> true
    /// with <see cref="ParsedRecord.BaseIndex"/> set so callers can merge $DATA/$FILE_NAME onto the base.
    /// </summary>
    public static bool TryParseRecord(ReadOnlySpan<byte> record, out ParsedRecord parsed)
    {
        parsed = default;
        if (!HasFileSignature(record) || record.Length < 0x28)
            return false;

        var flags = BinaryPrimitives.ReadUInt16LittleEndian(record[0x16..]);
        if ((flags & 0x01) == 0)
            return false;

        var baseRef = (long)(BinaryPrimitives.ReadUInt64LittleEndian(record[0x20..]) & 0x0000FFFFFFFFFFFFUL);
        var isDir = (flags & 0x02) != 0;
        string? bestName = null;
        var bestNs = -1;
        long bestParent = -1;
        long dataSize = 0;
        var hasData = false;

        if (!TryGetAttributeRange(record, out var offset, out var end))
            return false;

        while (offset + 8 <= end)
        {
            var type = BinaryPrimitives.ReadUInt32LittleEndian(record[offset..]);
            if (type == 0xFFFFFFFF) break;
            var length = BinaryPrimitives.ReadInt32LittleEndian(record[(offset + 4)..]);
            if (length < 16 || offset + length > record.Length) break;

            var nonResident = record[offset + 8] != 0;
            var nameLength = record[offset + 9];

            if (type == 0x30 && !nonResident) // FILE_NAME
            {
                var valueLength = BinaryPrimitives.ReadInt32LittleEndian(record[(offset + 0x10)..]);
                var valueOffset = BinaryPrimitives.ReadUInt16LittleEndian(record[(offset + 0x14)..]);
                if (valueLength >= 0x42 && offset + valueOffset + valueLength <= record.Length)
                {
                    var value = record.Slice(offset + valueOffset, valueLength);
                    var parentRef = BinaryPrimitives.ReadUInt64LittleEndian(value);
                    var parent = (long)(parentRef & 0x0000FFFFFFFFFFFFUL);
                    var nameLen = value[0x40];
                    var ns = value[0x41];
                    if (nameLen > 0 && 0x42 + nameLen * 2 <= value.Length)
                    {
                        var name = System.Text.Encoding.Unicode.GetString(value.Slice(0x42, nameLen * 2));
                        if (IsBetterName(ns, bestNs))
                        {
                            bestNs = ns;
                            bestName = name;
                            bestParent = parent;
                        }
                    }
                }
            }
            else if (type == 0x80 && nameLength == 0) // unnamed $DATA
            {
                long size;
                if (!nonResident)
                    size = BinaryPrimitives.ReadInt32LittleEndian(record[(offset + 0x10)..]);
                else if (offset + 0x38 <= record.Length)
                    size = BinaryPrimitives.ReadInt64LittleEndian(record[(offset + 0x30)..]);
                else
                    size = 0;

                if (!hasData || size > dataSize)
                {
                    dataSize = Math.Max(0, size);
                    hasData = true;
                }
            }

            offset += length;
        }

        parsed = new ParsedRecord(
            IsExtension: baseRef != 0,
            BaseIndex: baseRef,
            ParentIndex: bestParent,
            Name: bestName,
            NameSpace: bestNs,
            FileSize: dataSize,
            HasData: hasData,
            IsDirectory: isDir);
        return true;
    }

    public static bool TryParseEntry(ReadOnlySpan<byte> record, out ParsedEntry entry)
    {
        entry = default;
        if (!TryParseRecord(record, out var parsed) || parsed.IsExtension)
            return false;
        if (string.IsNullOrEmpty(parsed.Name) || parsed.ParentIndex < 0)
            return false;
        entry = new ParsedEntry(parsed.ParentIndex, parsed.Name, parsed.IsDirectory ? 0 : parsed.FileSize, parsed.IsDirectory);
        return true;
    }

    static bool TryGetAttributeRange(ReadOnlySpan<byte> record, out int offset, out int end)
    {
        offset = 0;
        end = 0;
        if (record.Length < 0x1C) return false;
        offset = BinaryPrimitives.ReadUInt16LittleEndian(record[0x14..]);
        if (offset < 0x18 || offset >= record.Length) return false;
        var used = BinaryPrimitives.ReadInt32LittleEndian(record[0x18..]);
        end = used > 0 && used <= record.Length ? used : record.Length;
        return true;
    }

    static bool IsBetterName(int ns, int currentBestNs)
    {
        static int Rank(int n) => n switch
        {
            1 or 3 => 3,
            0 => 2,
            2 => 1,
            _ => 0
        };
        return Rank(ns) > Rank(currentBestNs);
    }

    public static List<(long StartCluster, long ClusterCount)> DecodeDataRuns(ReadOnlySpan<byte> runs)
    {
        var list = new List<(long, long)>();
        long currentLcn = 0;
        var i = 0;
        while (i < runs.Length)
        {
            var header = runs[i++];
            if (header == 0) break;

            var lengthSize = header & 0x0F;
            var offsetSize = (header >> 4) & 0x0F;
            if (lengthSize == 0 || i + lengthSize + offsetSize > runs.Length)
                break;

            long clusterCount = ReadLeUnsigned(runs.Slice(i, lengthSize));
            i += lengthSize;

            if (offsetSize > 0)
            {
                currentLcn += ReadLeSigned(runs.Slice(i, offsetSize));
                i += offsetSize;
                if (clusterCount > 0)
                    list.Add((currentLcn, clusterCount));
            }
        }

        return list;
    }

    static long ReadLeUnsigned(ReadOnlySpan<byte> bytes)
    {
        long v = 0;
        for (var i = 0; i < bytes.Length; i++)
            v |= (long)bytes[i] << (8 * i);
        return v;
    }

    static long ReadLeSigned(ReadOnlySpan<byte> bytes)
    {
        long v = ReadLeUnsigned(bytes);
        var bits = bytes.Length * 8;
        if (bits < 64 && (v & (1L << (bits - 1))) != 0)
            v |= -1L << bits;
        return v;
    }

    internal readonly struct ParsedRecord
    {
        public readonly bool IsExtension;
        public readonly long BaseIndex;
        public readonly long ParentIndex;
        public readonly string? Name;
        public readonly int NameSpace;
        public readonly long FileSize;
        public readonly bool HasData;
        public readonly bool IsDirectory;

        public ParsedRecord(
            bool IsExtension,
            long BaseIndex,
            long ParentIndex,
            string? Name,
            int NameSpace,
            long FileSize,
            bool HasData,
            bool IsDirectory)
        {
            this.IsExtension = IsExtension;
            this.BaseIndex = BaseIndex;
            this.ParentIndex = ParentIndex;
            this.Name = Name;
            this.NameSpace = NameSpace;
            this.FileSize = FileSize;
            this.HasData = HasData;
            this.IsDirectory = IsDirectory;
        }
    }

    internal readonly struct ParsedEntry
    {
        public readonly long ParentIndex;
        public readonly string Name;
        public readonly long FileSize;
        public readonly bool IsDirectory;

        public ParsedEntry(long parentIndex, string name, long fileSize, bool isDirectory)
        {
            ParentIndex = parentIndex;
            Name = name;
            FileSize = fileSize;
            IsDirectory = isDirectory;
        }
    }
}
