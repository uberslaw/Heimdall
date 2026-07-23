namespace Heimdall.Shared.Contracts;

public sealed class HeartbeatDto
{
    public required string Hostname { get; init; }
    public string? MachineGroup { get; init; }
    public string? OsVersion { get; init; }
    public DateTimeOffset TimestampUtc { get; init; }
    public bool IsInUse { get; init; }
    public int ActiveSessionCount { get; init; }
    public string AgentVersion { get; init; } = "0.1.0";
}
