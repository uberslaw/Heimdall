namespace Heimdall.Shared.Contracts;

public sealed record ProcessRunDto
{
    public required string RunId { get; init; }
    public required string Hostname { get; init; }
    public required string Username { get; init; }
    public required string ProcessName { get; init; }
    public string? ExecutablePath { get; init; }
    public int ProcessId { get; init; }
    public DateTimeOffset StartedAtUtc { get; init; }
    public DateTimeOffset? EndedAtUtc { get; init; }
    public DateTimeOffset LastSeenAtUtc { get; init; }
    public int SampleCount { get; init; }
    public double? PeakCpuPercent { get; init; }
    /// <summary>Optional; agents may omit until GPU sampling ships.</summary>
    public double? PeakGpuPercent { get; init; }
    /// <summary>Optional cumulative disk read bytes.</summary>
    public long? DiskReadBytes { get; init; }
    /// <summary>Optional cumulative disk write bytes.</summary>
    public long? DiskWriteBytes { get; init; }
}
