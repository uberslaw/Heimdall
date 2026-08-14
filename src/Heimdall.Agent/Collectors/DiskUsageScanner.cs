using System.Runtime.Versioning;
using Heimdall.Shared.Contracts;

namespace Heimdall.Agent.Collectors;

/// <summary>
/// On-demand disk usage: sizes of first-level folders under a root (or all fixed drives),
/// plus the largest files above a threshold. Single walk per root, throttled, skips reparse
/// points / access-denied paths. Fleet profile can exclude system roots from the main walk
/// while still measuring known hotspots.
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
        var roots = ResolveRoots(request.RootPath);
        var displayRoot = roots.Count <= 1
            ? (roots.Count == 1 ? roots[0] : DiskUsageScanRoots.AllFixedDrives)
            : DiskUsageScanRoots.AllFixedDrives;
        var minBytes = Math.Max(1, request.MinFileMb) * 1024L * 1024L;
        var topN = Math.Clamp(request.TopFolderCount, 1, 100);
        var maxFiles = Math.Clamp(request.MaxLargeFiles, 1, 500);
        var maxSeconds = Math.Clamp(request.MaxSeconds, 30, 600);
        var overallDeadline = started.AddSeconds(maxSeconds);
        var excludeSystem = request.ExcludeSystemFolders;
        var includeHotspots = request.IncludeHotspots;

        var folderBytes = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
        var folderFiles = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var large = new List<DiskUsageFileDto>(maxFiles + 8);
        // Hotspot totals keyed by "key|path" so the same named hotspot on D: vs C: stays distinct.
        var hotspotBytes = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
        var hotspotFiles = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var hotspotMeta = new Dictionary<string, (string Key, string Path)>(StringComparer.OrdinalIgnoreCase);
        var profileBytes = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
        var profileFiles = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

        long bytesScanned = 0;
        var filesSeen = 0;
        var truncated = false;
        string? error = null;
        var walkSteps = 0;
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
                RootPath = displayRoot,
                Status = status,
                UpdatedUtc = now,
                ElapsedSeconds = Math.Round((now - started).TotalSeconds, 1),
                BytesScanned = bytesScanned,
                FilesSeen = filesSeen,
                Message = message
            });
        }

        void NoteFile(string root, string file, long size, string ccmcachePath, string projectsPath, string usersPath)
        {
            filesSeen++;
            bytesScanned += size;

            var bucket = FirstLevelBucket(root, file);
            folderBytes[bucket] = folderBytes.GetValueOrDefault(bucket) + size;
            folderFiles[bucket] = folderFiles.GetValueOrDefault(bucket) + 1;

            if (includeHotspots)
            {
                if (IsUnder(file, ccmcachePath))
                    AddHotspot(DiskUsageHotspotKeys.CcmCache, ccmcachePath, size);
                if (IsUnder(file, projectsPath))
                    AddHotspot(DiskUsageHotspotKeys.Projects, projectsPath, size);
                if (IsUnder(file, usersPath))
                {
                    AddHotspot(DiskUsageHotspotKeys.Users, usersPath, size);
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

        void AddHotspot(string key, string path, long size)
        {
            var id = key + "|" + path;
            hotspotMeta[id] = (key, path);
            hotspotBytes[id] = hotspotBytes.GetValueOrDefault(id) + size;
            hotspotFiles[id] = hotspotFiles.GetValueOrDefault(id) + 1;
        }

        bool WalkDirectory(string startDir, DateTimeOffset deadline, string root, string ccmcachePath, string projectsPath, string usersPath, bool seedFirstLevelBuckets)
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

                    NoteFile(root, file, size, ccmcachePath, projectsPath, usersPath);
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

        void ScanOneRoot(string root, DateTimeOffset deadline)
        {
            var rootFull = Path.GetFullPath(root).TrimEnd('\\') + @"\";
            var rootFilesKey = rootFull + "(files in root)";
            var ccmcachePath = Path.Combine(rootFull, "Windows", "ccmcache");
            var projectsPath = Path.Combine(rootFull, "Projects");
            var usersPath = Path.Combine(rootFull, "Users");

            if (!Directory.Exists(root))
            {
                EmitProgress(DiskUsageScanStatuses.Running, $"Path not found: {root}", force: true);
                // Only fail the whole job for a single explicit root; multi-drive skips missing volumes.
                if (roots.Count == 1)
                    error = $"Path not found: {root}";
                return;
            }

            // Register named hotspot paths even if empty / not walked yet.
            if (includeHotspots)
            {
                EnsureHotspotMeta(DiskUsageHotspotKeys.CcmCache, ccmcachePath);
                EnsureHotspotMeta(DiskUsageHotspotKeys.Projects, projectsPath);
                if (Directory.Exists(usersPath))
                    EnsureHotspotMeta(DiskUsageHotspotKeys.Users, usersPath);
            }

            folderBytes[rootFilesKey] = folderBytes.GetValueOrDefault(rootFilesKey);
            folderFiles[rootFilesKey] = folderFiles.GetValueOrDefault(rootFilesKey);

            foreach (var dir in SafeEnumerateDirectories(root))
            {
                if (excludeSystem && IsSystemFirstLevelName(dir))
                    continue;
                folderBytes.TryAdd(dir, 0);
                folderFiles.TryAdd(dir, 0);
            }

            foreach (var file in SafeEnumerateFiles(root))
            {
                long size;
                try { size = new FileInfo(file).Length; }
                catch { continue; }
                NoteFile(root, file, size, ccmcachePath, projectsPath, usersPath);
            }

            foreach (var dir in SafeEnumerateDirectories(root))
            {
                if (excludeSystem && IsSystemFirstLevelName(dir))
                    continue;
                if (!WalkDirectory(dir, deadline, root, ccmcachePath, projectsPath, usersPath, seedFirstLevelBuckets: false))
                    return;
            }

            if (includeHotspots && excludeSystem && !truncated)
            {
                if (Directory.Exists(ccmcachePath))
                {
                    EmitProgress(DiskUsageScanStatuses.Running, $"Scanning ccmcache on {root}", force: true);
                    if (!WalkDirectory(ccmcachePath, deadline, root, ccmcachePath, projectsPath, usersPath, seedFirstLevelBuckets: false))
                        return;
                }

                if (!truncated
                    && Directory.Exists(projectsPath)
                    && !folderBytes.ContainsKey(Path.GetFullPath(projectsPath).TrimEnd('\\')))
                {
                    EmitProgress(DiskUsageScanStatuses.Running, $"Scanning Projects on {root}", force: true);
                    WalkDirectory(projectsPath, deadline, root, ccmcachePath, projectsPath, usersPath, seedFirstLevelBuckets: false);
                }
            }
        }

        void EnsureHotspotMeta(string key, string path)
        {
            var id = key + "|" + path;
            hotspotMeta.TryAdd(id, (key, path));
            hotspotBytes.TryAdd(id, 0);
            hotspotFiles.TryAdd(id, 0);
        }

        EmitProgress(DiskUsageScanStatuses.Running,
            roots.Count > 1 ? $"Scan started ({roots.Count} fixed drives)" : "Scan started",
            force: true);

        try
        {
            if (roots.Count == 0)
            {
                error = "No fixed drives found to scan";
                EmitProgress(DiskUsageScanStatuses.Failed, error, force: true);
            }
            else
            {
                for (var i = 0; i < roots.Count; i++)
                {
                    ct.ThrowIfCancellationRequested();
                    var now = DateTimeOffset.UtcNow;
                    if (now >= overallDeadline)
                    {
                        truncated = true;
                        break;
                    }

                    var remainingSec = Math.Max(0, (overallDeadline - now).TotalSeconds);
                    var drivesLeft = roots.Count - i;
                    // Fair share of remaining budget; keep a small floor so tiny leftovers still do something.
                    var softSec = Math.Max(5, remainingSec / drivesLeft);
                    var driveDeadline = now.AddSeconds(softSec);
                    if (driveDeadline > overallDeadline)
                        driveDeadline = overallDeadline;

                    var root = roots[i];
                    EmitProgress(DiskUsageScanStatuses.Running,
                        roots.Count > 1 ? $"Scanning {root} ({i + 1}/{roots.Count})" : $"Scanning {root}",
                        force: true);
                    ScanOneRoot(root, driveDeadline);
                    if (truncated && DateTimeOffset.UtcNow >= overallDeadline)
                        break;
                }
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
            .Where(kv => !kv.Key.EndsWith("(files in root)", StringComparison.OrdinalIgnoreCase) || kv.Value > 0)
            .Where(kv =>
            {
                if (!excludeSystem) return true;
                if (kv.Key.EndsWith("(files in root)", StringComparison.OrdinalIgnoreCase)) return true;
                return !IsSystemFirstLevelName(kv.Key);
            })
            .Select(kv => new DiskUsageFolderDto
            {
                Path = kv.Key.EndsWith("(files in root)", StringComparison.OrdinalIgnoreCase)
                    ? kv.Key
                    : kv.Key,
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

        var hotspots = BuildHotspots(includeHotspots, hotspotMeta, hotspotBytes, hotspotFiles, profileBytes, profileFiles);

        var finalStatus = error is null ? DiskUsageScanStatuses.Complete : DiskUsageScanStatuses.Failed;
        EmitProgress(finalStatus, error ?? (truncated ? "Completed (time budget reached)" : "Completed"), force: true);

        return new DiskUsageScanResultDto
        {
            ScanId = request.ScanId,
            RootPath = displayRoot,
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

    /// <summary>Resolve request root to one or more drive roots (trailing slash).</summary>
    public static IReadOnlyList<string> ResolveRoots(string? rootPath)
    {
        if (DiskUsageScanRoots.IsAllFixedDrives(rootPath))
            return GetFixedDriveRoots();

        return [NormalizeRoot(rootPath!)];
    }

    public static IReadOnlyList<string> GetFixedDriveRoots()
    {
        try
        {
            return DriveInfo.GetDrives()
                .Where(d => d.DriveType == DriveType.Fixed && d.IsReady)
                .Select(d =>
                {
                    try { return Path.GetFullPath(d.RootDirectory.FullName); }
                    catch { return d.Name; }
                })
                .Where(r => !string.IsNullOrWhiteSpace(r))
                .Select(r => r.EndsWith('\\') ? r : r + @"\")
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(r => r, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }
        catch
        {
            return [];
        }
    }

    private static List<DiskUsageHotspotDto> BuildHotspots(
        bool includeHotspots,
        Dictionary<string, (string Key, string Path)> hotspotMeta,
        Dictionary<string, long> hotspotBytes,
        Dictionary<string, int> hotspotFiles,
        Dictionary<string, long> profileBytes,
        Dictionary<string, int> profileFiles)
    {
        if (!includeHotspots)
            return [];

        var list = new List<DiskUsageHotspotDto>();

        foreach (var (id, meta) in hotspotMeta
                     .OrderBy(kv => kv.Value.Key, StringComparer.OrdinalIgnoreCase)
                     .ThenBy(kv => kv.Value.Path, StringComparer.OrdinalIgnoreCase))
        {
            // Skip empty Users / Projects placeholders that never existed.
            var exists = Directory.Exists(meta.Path);
            var size = hotspotBytes.GetValueOrDefault(id);
            if (meta.Key is DiskUsageHotspotKeys.Users or DiskUsageHotspotKeys.Projects
                && !exists && size <= 0)
                continue;

            list.Add(new DiskUsageHotspotDto
            {
                Key = meta.Key,
                Path = meta.Path,
                Exists = exists,
                SizeBytes = size,
                FileCount = hotspotFiles.GetValueOrDefault(id)
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
            return rootFull + "(files in root)";

        var rel = full[rootFull.Length..];
        var slash = rel.IndexOfAny(['\\', '/']);
        if (slash < 0)
            return rootFull + "(files in root)";

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
