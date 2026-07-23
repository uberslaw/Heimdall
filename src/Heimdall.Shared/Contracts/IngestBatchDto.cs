namespace Heimdall.Shared.Contracts;

public sealed class IngestBatchDto
{
    public HeartbeatDto? Heartbeat { get; init; }
    public List<SessionEventDto> Sessions { get; init; } = [];
    public List<ProcessRunDto> ProcessRuns { get; init; } = [];
}
