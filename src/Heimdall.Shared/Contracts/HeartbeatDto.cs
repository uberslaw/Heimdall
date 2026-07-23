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

    // Optional hardware inventory (WMI / Environment). API fills Machine blanks unless HardwareManualOverride.
    public string? HardwareSerialNumber { get; init; }
    public string? HardwareBrand { get; init; }
    public string? HardwareModel { get; init; }
    public string? HardwareCpu { get; init; }
    public double? HardwareRamGb { get; init; }
    public double? HardwareDiskGb { get; init; }
    public string? HardwareGpu { get; init; }
}
