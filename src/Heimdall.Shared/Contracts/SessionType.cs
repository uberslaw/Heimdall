namespace Heimdall.Shared.Contracts;

/// <summary>
/// How the user is attached to <em>this</em> machine's WinStation.
/// <see cref="Rdp"/> means inbound (someone RDPed into this host), not outbound client use.
/// </summary>
public enum SessionType
{
    Local = 0,
    /// <summary>Inbound RDP/ICA into this machine (protocol or RDP- WinStation).</summary>
    Rdp = 1,
    Other = 2
}
