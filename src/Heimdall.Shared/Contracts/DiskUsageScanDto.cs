namespace Heimdall.Shared.Contracts;

/// <summary>API → agent: on-demand disk usage scan (top folders + large files).</summary>
public sealed class DiskUsageScanRequestDto
{
    /// <summary>Opaque id so the API can match the result and clear the pending request.</summary>
    public required string ScanId { get; init; }

    /// <summary>Root to scan, e.g. <c>C:\</c>.</summary>
    public required string RootPath { get; init; }

    /// <summary>When the operator clicked Scan (UTC). Survives navigation; used for UI timers.</summary>
    public DateTimeOffset RequestedUtc { get; init; }

    /// <summary>List files at or above this size (MB). Default 100.</summary>
    public int MinFileMb { get; init; } = 100;

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
