using System.Management;
using System.Runtime.Versioning;
using Heimdall.Shared;

namespace Heimdall.Agent.Collectors;

/// <summary>
/// Lists local TUFLOW-like processes with command lines and estimates CodeMeter seat claims.
/// </summary>
[SupportedOSPlatform("windows")]
public static class TuflowProcessInspector
{
    public static TuflowLicenseClaimEstimator.AggregateClaim Inspect(IReadOnlyList<string> processNamePatterns)
    {
        var claims = new List<TuflowLicenseClaimEstimator.ProcessClaim>();
        try
        {
            using var searcher = new ManagementObjectSearcher(
                "SELECT ProcessId, Name, CommandLine FROM Win32_Process");
            foreach (ManagementObject mo in searcher.Get())
            {
                try
                {
                    var name = mo["Name"] as string;
                    if (string.IsNullOrWhiteSpace(name))
                        continue;
                    var bare = Path.GetFileNameWithoutExtension(name);
                    if (!Matches(bare, name, processNamePatterns))
                        continue;

                    var pidObj = mo["ProcessId"];
                    var pid = pidObj is null ? 0 : Convert.ToInt32(pidObj);
                    if (pid <= 0)
                        continue;

                    var cmd = mo["CommandLine"] as string;
                    claims.Add(TuflowLicenseClaimEstimator.Classify(pid, bare, cmd));
                }
                finally
                {
                    mo.Dispose();
                }
            }
        }
        catch
        {
            // Best-effort — fleet snapshot still posts without claims.
        }

        return TuflowLicenseClaimEstimator.Aggregate(claims);
    }

    private static bool Matches(string bareName, string fileName, IReadOnlyList<string> patterns)
    {
        foreach (var pattern in patterns)
        {
            if (string.IsNullOrWhiteSpace(pattern))
                continue;
            var p = pattern.Trim();
            if (bareName.Contains(p, StringComparison.OrdinalIgnoreCase))
                return true;
            if (fileName.Contains(p, StringComparison.OrdinalIgnoreCase))
                return true;
        }
        return false;
    }
}
