using System.Runtime.Versioning;
using Heimdall.Shared.Contracts;

namespace Heimdall.Agent.Collectors;

/// <summary>
/// On-demand disk usage: sizes of first-level folders under a root, plus the largest files
/// above a threshold. Single walk, throttled, skips reparse points / access-denied paths.
/// </summary>
[SupportedOSPlatform("windows")]
public static class DiskUsageScanner
{
    public static DiskUsageScanResultDto Scan(
        DiskUsageScanRequestDto request,
        Action<DiskUsageScanProgressDto>? onProgress = null,
        CancellationToken ct = default)
    {
        var started = DateTimeOffset.UtcNow;
        var root = NormalizeRoot(request.RootPath);
        var minBytes = Math.Max(1, request.MinFileMb) * 1024L * 1024L;
        var topN = Math.Clamp(request.TopFolderCount, 1, 100);
        var maxFiles = Math.Clamp(request.MaxLargeFiles, 1, 500);
        var maxSeconds = Math.Clamp(request.MaxSeconds, 30, 600);
        var deadline = started.AddSeconds(maxSeconds);

        var folderBytes = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
        var folderFiles = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var large = new List<DiskUsageFileDto>(maxFiles + 8);
        long bytesScanned = 0;
        var filesSeen = 0;
        var truncated = false;
        string? error = null;
        var walkSteps = 0;
        const string rootFilesKey = ".";
        var lastProgressUtc = DateTimeOffset.MinValue;

        void EmitProgress(string status, string? message = null, bool force = false)
        {
            if (onProgress is null) return;
            var now = DateTimeOffset.UtcNow;
            if (!force && (now - lastProgressUtc).TotalSeconds < 5)
                return;
            lastProgressUtc = now;
            onProgress(new DiskUsageScanProgressDto
            {
                ScanId = request.ScanId,
                RootPath = root,
                Status = status,
                UpdatedUtc = now,
                ElapsedSeconds = Math.Round((now - started).TotalSeconds, 1),
                BytesScanned = bytesScanned,
                FilesSeen = filesSeen,
                Message = message
            });
        }

        EmitProgress(DiskUsageScanStatuses.Running, "Scan started", force: true);

        try
        {
            if (!Directory.Exists(root))
            {
                EmitProgress(DiskUsageScanStatuses.Failed, $"Path not found: {root}", force: true);
                return new DiskUsageScanResultDto
                {
                    ScanId = request.ScanId,
                    RootPath = root,
                    CompletedUtc = DateTimeOffset.UtcNow,
                    ElapsedSeconds = (DateTimeOffset.UtcNow - started).TotalSeconds,
                    Error = $"Path not found: {root}"
                };
            }

            folderBytes[rootFilesKey] = 0;
            folderFiles[rootFilesKey] = 0;

            foreach (var dir in SafeEnumerateDirectories(root))
            {
                folderBytes[dir] = 0;
                folderFiles[dir] = 0;
            }

            var stack = new Stack<string>();
            stack.Push(root);

            while (stack.Count > 0)
            {
                ct.ThrowIfCancellationRequested();
                if (DateTimeOffset.UtcNow >= deadline)
                {
                    truncated = true;
                    break;
                }

                var current = stack.Pop();
                foreach (var file in SafeEnumerateFiles(current))
                {
                    ct.ThrowIfCancellationRequested();
                    if (++walkSteps % 64 == 0)
                    {
                        Thread.Sleep(5);
                        EmitProgress(DiskUsageScanStatuses.Running);
                        if (DateTimeOffset.UtcNow >= deadline)
                        {
                            truncated = true;
                            break;
                        }
                    }

                    long size;
                    try
                    {
                        size = new FileInfo(file).Length;
                    }
                    catch
                    {
                        continue;
                    }

                    filesSeen++;
                    bytesScanned += size;

                    var bucket = FirstLevelBucket(root, file);
                    folderBytes[bucket] = folderBytes.GetValueOrDefault(bucket) + size;
                    folderFiles[bucket] = folderFiles.GetValueOrDefault(bucket) + 1;

                    if (size >= minBytes)
                    {
                        large.Add(new DiskUsageFileDto { Path = file, SizeBytes = size });
                        if (large.Count > maxFiles * 2)
                        {
                            large = large.OrderByDescending(f => f.SizeBytes).Take(maxFiles).ToList();
                        }
                    }
                }

                if (truncated) break;

                foreach (var sub in SafeEnumerateDirectories(current))
                    stack.Push(sub);
            }
        }
        catch (OperationCanceledException)
        {
            truncated = true;
            error = "Cancelled";
        }
        catch (Exception ex)
        {
            error = ex.Message;
        }

        var topFolders = folderBytes
            .Where(kv => kv.Key != rootFilesKey || kv.Value > 0)
            .Select(kv => new DiskUsageFolderDto
            {
                Path = kv.Key == rootFilesKey ? root.TrimEnd('\\') + @"\ (files in root)" : kv.Key,
                SizeBytes = kv.Value,
                FileCount = folderFiles.GetValueOrDefault(kv.Key)
            })
            .OrderByDescending(f => f.SizeBytes)
            .Take(topN)
            .ToList();

        var largeFiles = large
            .OrderByDescending(f => f.SizeBytes)
            .Take(maxFiles)
            .ToList();

        var finalStatus = error is null ? DiskUsageScanStatuses.Complete : DiskUsageScanStatuses.Failed;
        EmitProgress(finalStatus, error ?? (truncated ? "Completed (time budget reached)" : "Completed"), force: true);

        return new DiskUsageScanResultDto
        {
            ScanId = request.ScanId,
            RootPath = root,
            CompletedUtc = DateTimeOffset.UtcNow,
            ElapsedSeconds = Math.Round((DateTimeOffset.UtcNow - started).TotalSeconds, 1),
            Truncated = truncated,
            Error = error,
            BytesScanned = bytesScanned,
            FilesSeen = filesSeen,
            TopFolders = topFolders,
            LargeFiles = largeFiles
        };
    }

    private static string NormalizeRoot(string path)
    {
        var p = (path ?? "").Trim();
        if (p.Length == 2 && p[1] == ':')
            p += @"\";
        return Path.GetFullPath(p);
    }

    private static string FirstLevelBucket(string root, string filePath)
    {
        var full = Path.GetFullPath(filePath);
        var rootFull = Path.GetFullPath(root).TrimEnd('\\') + @"\";
        if (!full.StartsWith(rootFull, StringComparison.OrdinalIgnoreCase))
            return ".";

        var rel = full[rootFull.Length..];
        var slash = rel.IndexOfAny(['\\', '/']);
        if (slash < 0)
            return "."; // file in root

        return rootFull + rel[..slash];
    }

    private static IEnumerable<string> SafeEnumerateDirectories(string path)
    {
        IEnumerable<string> raw;
        try
        {
            raw = Directory.EnumerateDirectories(path);
        }
        catch
        {
            yield break;
        }

        foreach (var d in raw)
        {
            if (IsReparsePoint(d))
                continue;
            yield return d;
        }
    }

    private static IEnumerable<string> SafeEnumerateFiles(string path)
    {
        IEnumerable<string> raw;
        try
        {
            raw = Directory.EnumerateFiles(path);
        }
        catch
        {
            yield break;
        }

        foreach (var f in raw)
            yield return f;
    }

    private static bool IsReparsePoint(string path)
    {
        try
        {
            var attrs = File.GetAttributes(path);
            return (attrs & FileAttributes.ReparsePoint) != 0;
        }
        catch
        {
            return true;
        }
    }
}
