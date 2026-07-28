namespace Heimdall.Shared.Contracts;

/// <summary>
/// Well-known RDP *client* process names (this machine connecting outbound).
/// Distinct from inbound WinStation / protocol classification on user sessions.
/// </summary>
public static class RdpClientProcesses
{
    public static readonly HashSet<string> Names = new(StringComparer.OrdinalIgnoreCase)
    {
        "mstsc",
        "msrdc",
        "msrdcw"
    };

    public static bool IsRdpClient(string? processName) =>
        !string.IsNullOrWhiteSpace(processName) && Names.Contains(processName.Trim());
}
