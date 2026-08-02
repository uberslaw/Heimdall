using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;

namespace Heimdall.Agent.Collectors;

internal static class NetworkInfoHelper
{
    /// <summary>Best-effort primary IPv4 (first up, non-loopback, non-APIPA interface).</summary>
    public static string? TryGetPrimaryIPv4()
    {
        foreach (var ni in NetworkInterface.GetAllNetworkInterfaces())
        {
            if (ni.OperationalStatus != OperationalStatus.Up)
                continue;
            if (ni.NetworkInterfaceType is NetworkInterfaceType.Loopback or NetworkInterfaceType.Tunnel)
                continue;

            foreach (var addr in ni.GetIPProperties().UnicastAddresses)
            {
                if (addr.Address.AddressFamily != AddressFamily.InterNetwork)
                    continue;
                if (IPAddress.IsLoopback(addr.Address))
                    continue;
                var s = addr.Address.ToString();
                if (s.StartsWith("169.254.", StringComparison.Ordinal))
                    continue;
                return s;
            }
        }

        return null;
    }
}
