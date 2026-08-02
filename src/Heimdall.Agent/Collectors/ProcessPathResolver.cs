using System.Management;
using System.Runtime.Versioning;

namespace Heimdall.Agent.Collectors;

/// <summary>Resolve executable paths when Process.MainModule is blocked (services, elevated apps).</summary>
[SupportedOSPlatform("windows")]
internal static class ProcessPathResolver
{
    public static Dictionary<int, string> QueryWmiPaths()
    {
        var map = new Dictionary<int, string>();
        try
        {
            using var searcher = new ManagementObjectSearcher(
                "SELECT ProcessId, ExecutablePath FROM Win32_Process WHERE ExecutablePath IS NOT NULL");
            foreach (ManagementObject obj in searcher.Get())
            {
                try
                {
                    var pid = Convert.ToInt32(obj["ProcessId"], System.Globalization.CultureInfo.InvariantCulture);
                    var path = obj["ExecutablePath"]?.ToString();
                    if (!string.IsNullOrWhiteSpace(path))
                        map[pid] = path;
                }
                catch
                {
                    // skip row
                }
                finally
                {
                    obj.Dispose();
                }
            }
        }
        catch
        {
            // WMI unavailable — caller falls back to MainModule only
        }

        return map;
    }

    public static string? TryGetPath(System.Diagnostics.Process process, IReadOnlyDictionary<int, string>? wmiPaths = null)
    {
        try
        {
            var path = process.MainModule?.FileName;
            if (!string.IsNullOrWhiteSpace(path))
                return path;
        }
        catch
        {
            // access denied — try WMI
        }

        if (wmiPaths is not null && wmiPaths.TryGetValue(process.Id, out var wmiPath) && !string.IsNullOrWhiteSpace(wmiPath))
            return wmiPath;

        return null;
    }
}
