using System.Runtime.Versioning;
using Heimdall.Shared.Contracts;

namespace Heimdall.Agent.Collectors;

/// <summary>
/// On-demand disk usage: sizes of first-level folders under a root, plus the largest files
/// above a threshold. Single walk, throttled, skips reparse points / access-denied paths.
/// Fleet profile can exclude system roots from the main walk while still measuring known hotspots.
/// </summary>
[SupportedOSPlatform("windows")]
public static class DiskUsageScanner
{
    private static readonly HashSet<string> SystemFirstLevelNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "Windows",
        "Program Files",
        "Program Files (x86)",
        "ProgramData",
        "$Recycle.Bin",
        "System Volume Information",
        "Recovery",
        "PerfLogs",
        "Boot",
        "Documents and Settings",
        "Config.Msi",
        "MSOCache",
        "Intel",
        "AMD",
        "NVIDIA",
        "Drivers"
    };

    private static readonly HashSet<string> SkipUserProfileNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "Public",
        "Default",
        "Default User",
        "All Users",
        "DefaultAppPool"
    };

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
        var excludeSystem = request.ExcludeSystemFolders;
        var includeHotspots = request.IncludeHotspots;

        var folderBytes = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
        var folderFiles = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var large = new List<DiskUsageFileDto>(maxFiles + 8);
        var hotspotBytes = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
        var hotspotFiles = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var profileBytes = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
        var profileFiles = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

        long bytesScanned = 0;
        var filesSeen = 0;
        var truncated = false;
        string? error = null;
        var walkSteps = 0;
        const string rootFilesKey = ".";
        var lastProgressUtc = DateTimeOffset.MinValue;

        var rootFull = Path.GetFullPath(root).TrimEnd('\\') + @"\";
        var ccmcachePath = Path.Combine(rootFull, "Windows", "ccmcache");
        var projectsPath = Path.Combine(rootFull, "Projects");
        var usersPath = Path.Combine(rootFull, "Users");

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

        void NoteFile(string file, long size)
        {
            filesSeen++;
            bytesScanned += size;

            var bucket = FirstLevelBucket(root, file);
            folderBytes[bucket] = folderBytes.GetValueOrDefault(bucket) + size;
            folderFiles[bucket] = folderFiles.GetValueOrDefault(bucket) + 1;

            if (includeHotspots)
            {
                if (IsUnder(file, ccmcachePath))
                {
                    hotspotBytes[DiskUsageHotspotKeys.CcmCache] =
                        hotspotBytes.GetValueOrDefault(DiskUsageHotspotKeys.CcmCache) + size;
                    hotspotFiles[DiskUsageHotspotKeys.CcmCache] =
                        hotspotFiles.GetValueOrDefault(DiskUsageHotspotKeys.CcmCache) + 1;
                }

                if (IsUnder(file, projectsPath))
                {
                    hotspotBytes[DiskUsageHotspotKeys.Projects] =
                        hotspotBytes.GetValueOrDefault(DiskUsageHotspotKeys.Projects) + size;
                    hotspotFiles[DiskUsageHotspotKeys.Projects] =
                        hotspotFiles.GetValueOrDefault(DiskUsageHotspotKeys.Projects) + 1;
                }

                if (IsUnder(file, usersPath))
                {
                    hotspotBytes[DiskUsageHotspotKeys.Users] =
                        hotspotBytes.GetValueOrDefault(DiskUsageHotspotKeys.Users) + size;
                    hotspotFiles[DiskUsageHotspotKeys.Users] =
                        hotspotFiles.GetValueOrDefault(DiskUsageHotspotKeys.Users) + 1;

                    var profile = UserProfileBucket(usersPath, file);
                    if (profile is not null)
                    {
                        profileBytes[profile] = profileBytes.GetValueOrDefault(profile) + size;
                        profileFiles[profile] = profileFiles.GetValueOrDefault(profile) + 1;
                    }
                }
            }

            if (size >= minBytes)
            {
                large.Add(new DiskUsageFileDto { Path = file, SizeBytes = size });
                if (large.Count > maxFiles * 2)
                    large = large.OrderByDescending(f => f.SizeBytes).Take(maxFiles).ToList();
            }
        }

        bool WalkDirectory(string startDir, bool seedFirstLevelBuckets)
        {
            var stack = new Stack<string>();
            stack.Push(startDir);

            while (stack.Count > 0)
            {
                ct.ThrowIfCancellationRequested();
                if (DateTimeOffset.UtcNow >= deadline)
                {
                    truncated = true;
                    return false;
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
                            return false;
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

                    NoteFile(file, size);
                }

                if (truncated) return false;

                foreach (var sub in SafeEnumerateDirectories(current))
                {
                    if (seedFirstLevelBuckets
                        && string.Equals(current, startDir, StringComparison.OrdinalIgnoreCase)
                        && excludeSystem
                        && IsSystemFirstLevelName(sub))
                    {
                        continue;
                    }

                    stack.Push(sub);
                }
            }

            return true;
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
                if (excludeSystem && IsSystemFirstLevelName(dir))
                    continue;
                folderBytes[dir] = 0;
                folderFiles[dir] = 0;
            }

            // Root files (not in a first-level folder)
            foreach (var file in SafeEnumerateFiles(root))
            {
                long size;
                try { size = new FileInfo(file).Length; }
                catch { continue; }
                NoteFile(file, size);
            }

            // Walk non-system first-level trees (or everything when ExcludeSystemFolders is false)
            foreach (var dir in SafeEnumerateDirectories(root))
            {
                if (excludeSystem && IsSystemFirstLevelName(dir))
                    continue;
                if (!WalkDirectory(dir, seedFirstLevelBuckets: false))
                    break;
            }

            // When system roots were skipped, still measure priority hotspots under Windows / Projects.
            if (includeHotspots && excludeSystem && !truncated)
            {
                if (Directory.Exists(ccmcachePath))
                {
                    EmitProgress(DiskUsageScanStatuses.Running, "Scanning ccmcache hotspot", force: true);
                    WalkDirectory(ccmcachePath, seedFirstLevelBuckets: false);
                }

                // Projects may already be walked as a non-system first-level folder; only force if missing.
                if (!truncated
                    && Directory.Exists(projectsPath)
                    && !folderBytes.ContainsKey(Path.GetFullPath(projectsPath).TrimEnd('\\')))
                {
                    EmitProgress(DiskUsageScanStatuses.Running, "Scanning Projects hotspot", force: true);
                    WalkDirectory(projectsPath, seedFirstLevelBuckets: false);
                }
            }
            else if (includeHotspots && !excludeSystem && !truncated)
            {
                // Full walk already covered hotspots via NoteFile prefix checks.
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
            .Where(kv => !excludeSystem || kv.Key == rootFilesKey || !IsSystemFirstLevelName(kv.Key))
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

        var hotspots = BuildHotspots(
            includeHotspots,
            ccmcachePath,
            projectsPath,
            usersPath,
            hotspotBytes,
            hotspotFiles,
            profileBytes,
            profileFiles);

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
            LargeFiles = largeFiles,
            Hotspots = hotspots
        };
    }

    private static List<DiskUsageHotspotDto> BuildHotspots(
        bool includeHotspots,
        string ccmcachePath,
        string projectsPath,
        string usersPath,
        Dictionary<string, long> hotspotBytes,
        Dictionary<string, int> hotspotFiles,
        Dictionary<string, long> profileBytes,
        Dictionary<string, int> profileFiles)
    {
        if (!includeHotspots)
            return [];

        var list = new List<DiskUsageHotspotDto>();

        void AddNamed(string key, string path)
        {
            var exists = Directory.Exists(path);
            list.Add(new DiskUsageHotspotDto
            {
                Key = key,
                Path = path,
                Exists = exists,
                SizeBytes = hotspotBytes.GetValueOrDefault(key),
                FileCount = hotspotFiles.GetValueOrDefault(key)
            });
        }

        AddNamed(DiskUsageHotspotKeys.CcmCache, ccmcachePath);
        AddNamed(DiskUsageHotspotKeys.Projects, projectsPath);

        if (Directory.Exists(usersPath) || hotspotBytes.ContainsKey(DiskUsageHotspotKeys.Users))
        {
            list.Add(new DiskUsageHotspotDto
            {
                Key = DiskUsageHotspotKeys.Users,
                Path = usersPath,
                Exists = Directory.Exists(usersPath),
                SizeBytes = hotspotBytes.GetValueOrDefault(DiskUsageHotspotKeys.Users),
                FileCount = hotspotFiles.GetValueOrDefault(DiskUsageHotspotKeys.Users)
            });
        }

        foreach (var kv in profileBytes
                     .OrderByDescending(p => p.Value)
                     .Take(8))
        {
            list.Add(new DiskUsageHotspotDto
            {
                Key = DiskUsageHotspotKeys.UserProfile,
                Path = kv.Key,
                Exists = true,
                SizeBytes = kv.Value,
                FileCount = profileFiles.GetValueOrDefault(kv.Key)
            });
        }

        return list;
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

    private static bool IsUnder(string filePath, string directoryPath)
    {
        try
        {
            var file = Path.GetFullPath(filePath);
            var dir = Path.GetFullPath(directoryPath).TrimEnd('\\') + @"\";
            return file.StartsWith(dir, StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }

    private static string? UserProfileBucket(string usersPath, string filePath)
    {
        try
        {
            var file = Path.GetFullPath(filePath);
            var users = Path.GetFullPath(usersPath).TrimEnd('\\') + @"\";
            if (!file.StartsWith(users, StringComparison.OrdinalIgnoreCase))
                return null;

            var rel = file[users.Length..];
            var slash = rel.IndexOfAny(['\\', '/']);
            var name = slash < 0 ? rel : rel[..slash];
            if (string.IsNullOrWhiteSpace(name) || SkipUserProfileNames.Contains(name))
                return null;

            return users + name;
        }
        catch
        {
            return null;
        }
    }

    private static bool IsSystemFirstLevelName(string pathOrName)
    {
        var name = pathOrName;
        try
        {
            name = Path.GetFileName(pathOrName.TrimEnd('\\', '/'));
        }
        catch
        {
            // keep as-is
        }

        return !string.IsNullOrWhiteSpace(name) && SystemFirstLevelNames.Contains(name);
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
