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
    /// <summary>Preferred display / asset serial (hostname-derived when BIOS is generic).</summary>
    public string? HardwareSerialNumber { get; init; }

    public string? HardwareBrand { get; init; }

    public string? HardwareModel { get; init; }

    public string? HardwareCpu { get; init; }

    public double? HardwareRamGb { get; init; }

    public double? HardwareDiskGb { get; init; }

    public string? HardwareGpu { get; init; }

    /// <summary>Raw BIOS / Win32_BIOS serial (may be OEM placeholder).</summary>
    public string? BiosSerial { get; init; }

    /// <summary>Serial parsed from hostname convention (city + DT/LT + serial).</summary>
    public string? AssetSerial { get; init; }

    public string? HostnameCityCode { get; init; }

    /// <summary>DT = desktop, LT = laptop when present in hostname.</summary>
    public string? HostnameChassisHint { get; init; }

    /// <summary>HKLM\SOFTWARE\Microsoft\Cryptography\MachineGuid — changes on OS reimage.</summary>
    public string? MachineGuid { get; init; }

    /// <summary>Win32_ComputerSystemProduct.UUID — hardware; usually survives reimage.</summary>
    public string? SmbiosUuid { get; init; }

    /// <summary>Win32_OperatingSystem.InstallDate / registry — often moves on feature update.</summary>
    public DateTimeOffset? OsInstallDateUtc { get; init; }

    /// <summary>Creation time of %SystemRoot% — often closer to original image.</summary>
    public DateTimeOffset? WindowsFolderCreatedUtc { get; init; }

    /// <summary>Primary IPv4 reported by agent (best-effort).</summary>
    public string? PrimaryIpAddress { get; init; }

    /// <summary>TermService (Remote Desktop Services) status: Running, Stopped, Unknown, etc.</summary>
    public string? TermServiceStatus { get; init; }

    /// <summary>Commands executed since last ingest; API clears matching PendingCommands.</summary>
    public List<string> AcknowledgedCommands { get; init; } = [];

    /// <summary>Per-attempt command results (including failures while command remains pending for retry).</summary>
    public List<CommandExecutionReportDto> CommandExecutionReports { get; init; } = [];
}
