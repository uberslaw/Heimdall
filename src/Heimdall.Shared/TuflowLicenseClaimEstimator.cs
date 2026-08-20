using System.Text.RegularExpressions;

namespace Heimdall.Shared;

/// <summary>
/// Estimates CodeMeter seat claims from local TUFLOW process command lines (agent-side).
/// Not a license-server truth — CodeMeter checkouts remain authoritative.
/// </summary>
public static partial class TuflowLicenseClaimEstimator
{
    public sealed record ProcessClaim(
        int ProcessId,
        string ProcessName,
        string? CommandLine,
        bool IsHpc,
        int Seats,
        string Evidence);

    public sealed record AggregateClaim(
        int InstanceCount,
        int ClaimedHpcSeats,
        int ClaimedClassicSeats,
        string Detail,
        IReadOnlyList<ProcessClaim> Processes);

    public static AggregateClaim Aggregate(IEnumerable<ProcessClaim> processes)
    {
        var list = processes.ToList();
        var hpc = list.Where(p => p.IsHpc).Sum(p => p.Seats);
        var classic = list.Where(p => !p.IsHpc).Sum(p => p.Seats);
        var detail = list.Count == 0
            ? ""
            : string.Join("; ", list.Select(p => $"{p.ProcessName}#{p.ProcessId}:{p.Evidence}"));
        if (detail.Length > 400)
            detail = detail[..397] + "...";
        return new AggregateClaim(list.Count, hpc, classic, detail, list);
    }

    public static ProcessClaim Classify(int processId, string processName, string? commandLine)
    {
        var name = processName ?? "";
        var cmd = commandLine ?? "";
        var nt = TryParseNt(cmd);
        var nameHpc = NameLooksHpc(name);
        var nameClassic = NameLooksClassic(name);
        var cmdGpu = CmdLooksGpu(cmd);
        var hasNt = nt is > 0;

        // Explicit HPC cues win; Classic binary without -nt/GPU stays Classic.
        var isHpc = nameHpc || cmdGpu || hasNt;
        if (nameClassic && !nameHpc && !cmdGpu && !hasNt)
            isHpc = false;

        int seats;
        string evidence;
        if (isHpc)
        {
            seats = nt is > 0 ? nt.Value : 1;
            evidence = nt is > 0
                ? (cmdGpu ? $"-nt{nt}/GPU" : $"-nt{nt}")
                : (cmdGpu ? "GPU" : "HPC");
        }
        else
        {
            seats = 1;
            evidence = "Classic×1";
        }

        return new ProcessClaim(processId, name, string.IsNullOrWhiteSpace(cmd) ? null : cmd, isHpc, seats, evidence);
    }

    public static int? TryParseNt(string? commandLine)
    {
        if (string.IsNullOrWhiteSpace(commandLine))
            return null;
        var m = NtArgRegex().Match(commandLine);
        if (!m.Success)
            return null;
        if (!int.TryParse(m.Groups[1].Value, out var n) || n < 1)
            return null;
        return Math.Min(n, 512);
    }

    public static bool NameLooksHpc(string processName) =>
        processName.Contains("HPC", StringComparison.OrdinalIgnoreCase)
        || processName.Contains("tgpu", StringComparison.OrdinalIgnoreCase);

    public static bool NameLooksClassic(string processName) =>
        processName.Contains("iSP", StringComparison.OrdinalIgnoreCase)
        || processName.Contains("iDP", StringComparison.OrdinalIgnoreCase)
        || processName.Contains("Classic", StringComparison.OrdinalIgnoreCase);

    public static bool CmdLooksGpu(string commandLine) =>
        commandLine.Contains("-gpu", StringComparison.OrdinalIgnoreCase)
        || commandLine.Contains("CUDA", StringComparison.OrdinalIgnoreCase)
        || commandLine.Contains("OpenCL", StringComparison.OrdinalIgnoreCase);

    [GeneratedRegex(@"-nt(?:\s*|=)?(\d+)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex NtArgRegex();
}
