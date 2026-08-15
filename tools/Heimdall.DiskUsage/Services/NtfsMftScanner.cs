using System.IO;
using System.Runtime.Versioning;
using Heimdall.DiskUsage.Models;

namespace Heimdall.DiskUsage.Services;

/// <summary>
/// Builds a folder-size tree by reading the NTFS Master File Table (WizTree-style).
/// Requires Administrator to open \\.\X:.
/// </summary>
[SupportedOSPlatform("windows")]
public static class NtfsMftScanner
{
    static readonly HashSet<string> SystemTopNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "Windows",
        "Program Files",
        "Program Files (x86)",
    };

    sealed class Entry
    {
        public long Parent;
        public string Name = "";
        public long FileSize;
        public bool IsDirectory;
        public int NameSpace = -1;
    }

    sealed class ExtensionExtra
    {
        public long FileSize;
        public string? Name;
        public long Parent = -1;
        public int NameSpace = -1;
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

    public static bool TryScan(
        string rootPath,
        bool excludeSystemFolders,
        IProgress<DriveScanner.Progress>? progress,
        CancellationToken ct,
        out FolderNode? root,
        out string failureReason)
    {
        root = null;
        failureReason = "";

        string rootFs;
        try
        {
            rootFs = DriveScanner.ToFileSystemPath(rootPath);
        }
        catch (Exception ex)
        {
            failureReason = ex.Message;
            return false;
        }

        if (!Directory.Exists(rootFs))
        {
            failureReason = $"Path not found: {rootFs}";
            return false;
        }

        var letter = rootFs[0];
        if (!NtfsVolume.TryOpen(letter.ToString(), out var volume, out failureReason) || volume is null)
            return false;

        using (volume)
        {
            try
            {
                root = ScanVolume(volume, rootFs, excludeSystemFolders, progress, ct);
                return true;
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                failureReason = ex.Message;
                root = null;
                return false;
            }
        }
    }

    static FolderNode ScanVolume(
        NtfsVolume volume,
        string rootFs,
        bool excludeSystemFolders,
        IProgress<DriveScanner.Progress>? progress,
        CancellationToken ct)
    {
        var rootKey = DriveScanner.ToKey(rootFs);
        var rootDisplay = rootKey.Length == 2 && rootKey[1] == ':' ? rootKey + @"\" : rootKey;

        var root = new FolderNode
        {
            FullPath = rootKey,
            Name = rootDisplay,
            Parent = null
        };

        var entries = new Dictionary<long, Entry>();
        var extensionExtras = new Dictionary<long, ExtensionExtra>();
        long bytesSeen = 0;
        long filesSeen = 0;
        long foldersSeen = 1;
        var lastReport = DateTime.UtcNow;

        void Report(string message, bool force = false)
        {
            if (progress is null) return;
            var now = DateTime.UtcNow;
            if (!force && (now - lastReport).TotalMilliseconds < 350) return;
            lastReport = now;
            progress.Report(new DriveScanner.Progress(message, bytesSeen, filesSeen, foldersSeen));
        }

        Report("Reading NTFS MFT…", force: true);

        var mftProgress = new Progress<(long RecordsRead, long BytesRead)>(p =>
        {
            Report($"Reading MFT… {p.RecordsRead:N0} records ({FolderNode.FormatSize(p.BytesRead)})");
        });

        volume.ForEachMftRecord((index, record) =>
        {
            ct.ThrowIfCancellationRequested();
            if (!MftRecord.TryParseRecord(record, out var parsed))
                return;

            if (parsed.IsExtension)
            {
                // Merge $DATA / $FILE_NAME from extension records onto their base later.
                if (!extensionExtras.TryGetValue(parsed.BaseIndex, out var extra))
                    extensionExtras[parsed.BaseIndex] = extra = new ExtensionExtra();

                if (parsed.HasData && parsed.FileSize > extra.FileSize)
                    extra.FileSize = parsed.FileSize;

                if (!string.IsNullOrEmpty(parsed.Name) && parsed.ParentIndex >= 0
                    && IsBetterName(parsed.NameSpace, extra.NameSpace))
                {
                    extra.NameSpace = parsed.NameSpace;
                    extra.Name = parsed.Name;
                    extra.Parent = parsed.ParentIndex;
                }

                return;
            }

            if (index == MftRecord.RootDirectoryIndex)
            {
                entries[index] = new Entry
                {
                    Parent = parsed.ParentIndex >= 0 ? parsed.ParentIndex : MftRecord.RootDirectoryIndex,
                    Name = rootDisplay,
                    IsDirectory = true
                };
                return;
            }

            entries[index] = new Entry
            {
                Parent = parsed.ParentIndex,
                Name = parsed.Name ?? "",
                FileSize = parsed.IsDirectory ? 0 : parsed.FileSize,
                IsDirectory = parsed.IsDirectory,
                NameSpace = parsed.NameSpace
            };
        }, mftProgress, ct);

        // Apply extension-record attributes onto base entries.
        foreach (var (baseIndex, extra) in extensionExtras)
        {
            ct.ThrowIfCancellationRequested();
            if (!entries.TryGetValue(baseIndex, out var entry))
            {
                if (string.IsNullOrEmpty(extra.Name) || extra.Parent < 0)
                    continue;
                entry = new Entry
                {
                    Parent = extra.Parent,
                    Name = extra.Name,
                    FileSize = extra.FileSize,
                    IsDirectory = false,
                    NameSpace = extra.NameSpace
                };
                entries[baseIndex] = entry;
            }
            else
            {
                if (extra.FileSize > entry.FileSize && !entry.IsDirectory)
                    entry.FileSize = extra.FileSize;
                if (!string.IsNullOrEmpty(extra.Name) && IsBetterName(extra.NameSpace, entry.NameSpace))
                {
                    entry.Name = extra.Name;
                    entry.Parent = extra.Parent;
                    entry.NameSpace = extra.NameSpace;
                }
            }
        }

        // Drop bases that still have no usable name (system metadata without $FILE_NAME).
        var drop = new List<long>();
        foreach (var (index, entry) in entries)
        {
            if (index == MftRecord.RootDirectoryIndex) continue;
            if (string.IsNullOrEmpty(entry.Name) || entry.Parent < 0)
                drop.Add(index);
        }

        foreach (var id in drop)
            entries.Remove(id);

        filesSeen = 0;
        foldersSeen = 1;
        bytesSeen = 0;
        foreach (var (index, entry) in entries)
        {
            if (index == MftRecord.RootDirectoryIndex) continue;
            if (entry.IsDirectory) foldersSeen++;
            else if (entry.FileSize > 0)
            {
                filesSeen++;
                bytesSeen += entry.FileSize;
            }
        }

        if (!entries.ContainsKey(MftRecord.RootDirectoryIndex))
        {
            entries[MftRecord.RootDirectoryIndex] = new Entry
            {
                Parent = MftRecord.RootDirectoryIndex,
                Name = rootDisplay,
                IsDirectory = true
            };
        }

        // Parents of files that were not flagged as directories still need folder nodes.
        foreach (var entry in entries.Values)
        {
            if (entry.IsDirectory) continue;
            if (entries.TryGetValue(entry.Parent, out var parent) && !parent.IsDirectory)
                parent.IsDirectory = true;
        }

        Report($"Building folder tree… ({foldersSeen:N0} folders, {filesSeen:N0} files)", force: true);

        var dirNodes = new Dictionary<long, FolderNode>
        {
            [MftRecord.RootDirectoryIndex] = root
        };

        // parent MFT index → child directory indices
        var childrenOf = new Dictionary<long, List<long>>();
        foreach (var (index, entry) in entries)
        {
            ct.ThrowIfCancellationRequested();
            if (!entry.IsDirectory || index == MftRecord.RootDirectoryIndex)
                continue;

            var parentId = entry.Parent;
            if (parentId == index || parentId < 0 || !entries.ContainsKey(parentId))
                parentId = MftRecord.RootDirectoryIndex;

            if (!childrenOf.TryGetValue(parentId, out var list))
                childrenOf[parentId] = list = new List<long>();
            list.Add(index);
        }

        // BFS from root — O(n), no Path.GetFullPath
        var queue = new Queue<long>();
        queue.Enqueue(MftRecord.RootDirectoryIndex);
        var built = 0;
        while (queue.Count > 0)
        {
            ct.ThrowIfCancellationRequested();
            var parentId = queue.Dequeue();
            if (!childrenOf.TryGetValue(parentId, out var kids))
                continue;

            var parentNode = dirNodes[parentId];
            foreach (var childId in kids)
            {
                if (dirNodes.ContainsKey(childId))
                    continue;
                if (!entries.TryGetValue(childId, out var entry))
                    continue;

                var fullPath = JoinKey(parentNode.FullPath, entry.Name);
                var node = new FolderNode
                {
                    FullPath = fullPath,
                    Name = entry.Name,
                    Parent = parentNode
                };
                dirNodes[childId] = node;
                parentNode.Children.Add(node);
                queue.Enqueue(childId);

                if (++built % 50000 == 0)
                    Report($"Building folder tree… {built:N0} folders linked");
            }
        }

        Report("Rolling up sizes…", force: true);

        var rolled = 0;
        foreach (var (index, entry) in entries)
        {
            ct.ThrowIfCancellationRequested();
            if (entry.IsDirectory || entry.FileSize <= 0) continue;

            if (!dirNodes.TryGetValue(entry.Parent, out var parentNode))
                continue;

            parentNode.OwnFilesBytes += entry.FileSize;
            parentNode.FileCount++;
            for (var n = parentNode; n is not null; n = n.Parent)
                n.SizeBytes += entry.FileSize;

            if (++rolled % 200000 == 0)
                Report($"Rolling up sizes… {rolled:N0} files");
        }

        if (excludeSystemFolders)
            ApplySystemExclude(root);

        SortChildrenRecursive(root);
        Report($"Done (MFT) — {FolderNode.FormatSize(root.SizeBytes)}", force: true);
        return root;
    }

    /// <summary>
    /// Join a ToKey-style parent ("C:" or "C:\Users") with a child name → ToKey path.
    /// Avoids Path.GetFullPath (critical for MFT-scale trees).
    /// </summary>
    internal static string JoinKey(string parentKey, string name) => parentKey + "\\" + name;

    /// <summary>
    /// Remove Windows / Program Files / Program Files (x86) from drive root, but keep
    /// Windows\ccmcache grafted as a direct child named "Windows\ccmcache".
    /// </summary>
    static void ApplySystemExclude(FolderNode root)
    {
        FolderNode? windows = null;
        var remove = new List<FolderNode>();

        foreach (var child in root.Children)
        {
            if (!SystemTopNames.Contains(child.Name))
                continue;
            remove.Add(child);
            if (child.Name.Equals("Windows", StringComparison.OrdinalIgnoreCase))
                windows = child;
        }

        FolderNode? ccmcache = windows?.Children.FirstOrDefault(c =>
            c.Name.Equals("ccmcache", StringComparison.OrdinalIgnoreCase));

        foreach (var r in remove)
        {
            SubtractSizeUp(root, r.SizeBytes);
            root.Children.Remove(r);
        }

        if (ccmcache is null)
            return;

        var grafted = CloneSubtree(
            ccmcache,
            root,
            JoinKey(JoinKey(root.FullPath, "Windows"), "ccmcache"),
            @"Windows\ccmcache");
        root.Children.Add(grafted);
        for (var n = root; n is not null; n = n.Parent)
            n.SizeBytes += grafted.SizeBytes;
    }

    static FolderNode CloneSubtree(FolderNode source, FolderNode newParent, string fullPath, string displayName)
    {
        var node = new FolderNode
        {
            FullPath = fullPath,
            Name = displayName,
            Parent = newParent,
            SizeBytes = source.SizeBytes,
            OwnFilesBytes = source.OwnFilesBytes,
            FileCount = source.FileCount
        };

        foreach (var child in source.Children)
        {
            var childPath = JoinKey(fullPath, child.Name);
            node.Children.Add(CloneSubtree(child, node, childPath, child.Name));
        }

        return node;
    }

    static void SubtractSizeUp(FolderNode from, long bytes)
    {
        for (var n = from; n is not null; n = n.Parent)
            n.SizeBytes = Math.Max(0, n.SizeBytes - bytes);
    }

    static void SortChildrenRecursive(FolderNode node)
    {
        node.Children.Sort((a, b) => b.SizeBytes.CompareTo(a.SizeBytes));
        foreach (var child in node.Children)
            SortChildrenRecursive(child);
    }
}
