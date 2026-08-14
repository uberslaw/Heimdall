namespace Heimdall.Shared.Contracts;

/// <summary>
/// Well-known <see cref="DiskUsageScanRequestDto.RootPath"/> values.
/// <see cref="AllFixedDrives"/> (or empty / <c>all</c>) means every ready fixed volume.
/// </summary>
public static class DiskUsageScanRoots
{
    public const string AllFixedDrives = "*";

    public static bool IsAllFixedDrives(string? rootPath)
    {
        var p = (rootPath ?? "").Trim();
        return p.Length == 0
               || p == "*"
               || string.Equals(p, "all", StringComparison.OrdinalIgnoreCase)
               || string.Equals(p, "all:", StringComparison.OrdinalIgnoreCase)
               || string.Equals(p, "all:\\", StringComparison.OrdinalIgnoreCase);
    }

    public static string FormatForDisplay(string? rootPath) =>
        IsAllFixedDrives(rootPath) ? "all fixed drives" : (rootPath ?? "").Trim();
}

/// <summary>UI helpers for large-file threshold amount + MB/GB/TB (binary: 1024-based).</summary>
public static class DiskSizeUnits
{
    public const string Mb = "MB";
    public const string Gb = "GB";
    public const string Tb = "TB";

    public static readonly string[] All = [Mb, Gb, Tb];

    /// <summary>Normalize unit token; unknown → MB.</summary>
    public static string Normalize(string? unit)
    {
        var u = (unit ?? "").Trim().ToUpperInvariant();
        return u switch
        {
            "TB" or "T" => Tb,
            "GB" or "G" => Gb,
            _ => Mb
        };
    }

    /// <summary>Convert amount+unit to whole mebibytes for <see cref="DiskUsageScanRequestDto.MinFileMb"/>.</summary>
    public static int ToMinFileMb(double amount, string? unit)
    {
        if (double.IsNaN(amount) || double.IsInfinity(amount) || amount <= 0)
            amount = 1;

        var bytes = Normalize(unit) switch
        {
            Tb => amount * 1024d * 1024d * 1024d * 1024d,
            Gb => amount * 1024d * 1024d * 1024d,
            _ => amount * 1024d * 1024d
        };

        var mb = (long)Math.Round(bytes / (1024d * 1024d), MidpointRounding.AwayFromZero);
        return (int)Math.Clamp(mb, 1, 100L * 1024 * 1024); // 1 MB … 100 TB
    }

    /// <summary>Pick a tidy amount+unit for display from stored MinFileMb.</summary>
    public static (double Amount, string Unit) FromMinFileMb(int minFileMb)
    {
        var mb = Math.Max(1, minFileMb);
        if (mb >= 1024 * 1024 && mb % (1024 * 1024) == 0)
            return (mb / (1024d * 1024d), Tb);
        if (mb >= 1024 && mb % 1024 == 0)
            return (mb / 1024d, Gb);
        return (mb, Mb);
    }

    public static string FormatThreshold(int minFileMb)
    {
        var (amount, unit) = FromMinFileMb(minFileMb);
        var text = Math.Abs(amount - Math.Round(amount)) < 0.001
            ? ((long)Math.Round(amount)).ToString()
            : amount.ToString("0.##");
        return $"{text} {unit}";
    }
}

/// <summary>API → agent: on-demand disk usage scan (top folders + large files).</summary>
public sealed class DiskUsageScanRequestDto
{
    /// <summary>Opaque id so the API can match the result and clear the pending request.</summary>
    public required string ScanId { get; init; }

    /// <summary>
    /// Root to scan, e.g. <c>C:\</c>, or <see cref="DiskUsageScanRoots.AllFixedDrives"/> / empty for every fixed drive.
    /// </summary>
    public required string RootPath { get; init; }

    /// <summary>When the operator clicked Scan (UTC). Survives navigation; used for UI timers.</summary>
    public DateTimeOffset RequestedUtc { get; init; }

    /// <summary>List files at or above this size (MiB, 1024-based). Default 1024 (1 GiB).</summary>
    public int MinFileMb { get; init; } = 1024;

    /// <summary>How many largest first-level folders to return. Default 25.</summary>
    public int TopFolderCount { get; init; } = 25;

    /// <summary>Max large-file rows to return. Default 100.</summary>
    public int MaxLargeFiles { get; init; } = 100;

    /// <summary>Soft time budget in seconds; agent stops walking and returns partial results. Default 180.</summary>
    public int MaxSeconds { get; init; } = 180;

    /// <summary>
    /// When true, top folders exclude Windows system roots (Windows, Program Files, …).
    /// Those roots are skipped in the main walk; known hotspots are still measured separately.
    /// </summary>
    public bool ExcludeSystemFolders { get; init; }

    /// <summary>When true, measure known hotspots (ccmcache, Projects, user profiles).</summary>
    public bool IncludeHotspots { get; init; } = true;

    /// <summary>Fleet/weekly profile marker for UI (optional).</summary>
    public bool FleetProfile { get; init; }
}

/// <summary>Lightweight agent → API progress while a scan is running.</summary>
public sealed class DiskUsageScanProgressDto
{
    public required string ScanId { get; init; }
    public required string RootPath { get; init; }
    /// <summary>Queued | Running | Complete | Failed</summary>
    public required string Status { get; init; }
    public DateTimeOffset UpdatedUtc { get; init; }
    public double ElapsedSeconds { get; init; }
    public long BytesScanned { get; init; }
    public int FilesSeen { get; init; }
    public string? Message { get; init; }
}

public static class DiskUsageScanStatuses
{
    public const string Queued = "Queued";
    public const string Running = "Running";
    public const string Complete = "Complete";
    public const string Failed = "Failed";
}

/// <summary>Agent → API: disk usage scan result.</summary>
public sealed class DiskUsageScanResultDto
{
    public required string ScanId { get; init; }
    public required string RootPath { get; init; }
    public DateTimeOffset CompletedUtc { get; init; }
    public double ElapsedSeconds { get; init; }
    /// <summary>True when the walker hit MaxSeconds or access limits before finishing.</summary>
    public bool Truncated { get; init; }
    public string? Error { get; init; }
    public long BytesScanned { get; init; }
    public int FilesSeen { get; init; }
    public List<DiskUsageFolderDto> TopFolders { get; init; } = [];
    public List<DiskUsageFileDto> LargeFiles { get; init; } = [];
    /// <summary>Known hotspot sizes (ccmcache, Projects, user profiles). Empty on older agents.</summary>
    public List<DiskUsageHotspotDto> Hotspots { get; init; } = [];
}

public sealed class DiskUsageFolderDto
{
    /// <summary>Full directory path.</summary>
    public required string Path { get; init; }
    public long SizeBytes { get; init; }
    public int FileCount { get; init; }
}

public sealed class DiskUsageFileDto
{
    public required string Path { get; init; }
    public long SizeBytes { get; init; }
}

/// <summary>Prioritized storage hotspot (ccmcache, Projects, Users profile, …).</summary>
public sealed class DiskUsageHotspotDto
{
    /// <summary>Stable key: <c>ccmcache</c>, <c>Projects</c>, <c>Users</c>, or <c>UserProfile</c>.</summary>
    public required string Key { get; init; }

    public required string Path { get; init; }
    public long SizeBytes { get; init; }
    public int FileCount { get; init; }
    public bool Exists { get; init; }
}

/// <summary>Fast poll payload (same cadence idea as TUFLOW pending).</summary>
public sealed class DiskUsagePendingDto
{
    public DiskUsageScanRequestDto? PendingDiskUsageScan { get; init; }
}

/// <summary>Well-known path keys used by the agent scanner and Storage dashboard.</summary>
public static class DiskUsageHotspotKeys
{
    public const string CcmCache = "ccmcache";
    public const string Projects = "Projects";
    public const string Users = "Users";
    public const string UserProfile = "UserProfile";
}
