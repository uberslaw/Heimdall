namespace Heimdall.Shared.Contracts;

public sealed class IngestBatchDto
{
    public HeartbeatDto? Heartbeat { get; init; }
    public List<SessionEventDto> Sessions { get; init; } = [];
    public List<ProcessRunDto> ProcessRuns { get; init; } = [];
    /// <summary>One-shot inventory of processes seen on the host (for app analysis).</summary>
    public List<DiscoveredProcessDto> DiscoveredProcesses { get; init; } = [];
}

public sealed class DiscoveredProcessDto
{
    public required string ProcessName { get; init; }
    public string? DisplayName { get; init; }
    public string? ExecutablePath { get; init; }
}
