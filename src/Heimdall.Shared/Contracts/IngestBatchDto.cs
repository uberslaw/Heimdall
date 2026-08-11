namespace Heimdall.Shared.Contracts;

public sealed class IngestBatchDto
{
    public HeartbeatDto? Heartbeat { get; init; }
    public List<SessionEventDto> Sessions { get; init; } = [];
    public List<ProcessRunDto> ProcessRuns { get; init; } = [];
    /// <summary>One-shot inventory of processes seen on the host (for app analysis).</summary>
    public List<DiscoveredProcessDto> DiscoveredProcesses { get; init; } = [];
    /// <summary>Optional on-demand disk usage scan result (top folders + large files).</summary>
    public DiskUsageScanResultDto? DiskUsageScan { get; init; }
    /// <summary>Optional mid-scan progress (also accepted via dedicated POST).</summary>
    public DiskUsageScanProgressDto? DiskUsageScanProgress { get; init; }
}

public sealed class DiscoveredProcessDto
{
    public required string ProcessName { get; init; }
    public string? DisplayName { get; init; }
    public string? ExecutablePath { get; init; }
    /// <summary>Win32 FileVersionInfo fields, captured by the agent when the executable path is readable. Null if unavailable.</summary>
    public string? FileVersion { get; init; }
    public string? ProductVersion { get; init; }
    public string? CompanyName { get; init; }
    public string? FileDescription { get; init; }
}
