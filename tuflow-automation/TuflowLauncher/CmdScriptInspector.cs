using System.Text.RegularExpressions;

namespace TuflowLauncher;

/// <summary>
/// Lightweight preflight for ready-made TUFLOW .cmd/.bat scripts. Not a full batch parser —
/// enough to reject empty/non-TUFLOW files and surface obvious .tcf / flag hints before CreateProcess.
/// </summary>
internal static class CmdScriptInspector
{
    private static readonly Regex TcfPathRegex = new(
        @"(?<![A-Za-z0-9_])(?<path>(?:""[^""]+\.tcf""|'[^']+\.tcf'|[^\s""']+\.tcf))",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex FlagRegex = new(
        @"-(?:pu|gpu|x|b|nc|nq|nmb|s\d+|e\d+)\b",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    public static CmdInspection Inspect(string cmdPath)
    {
        if (string.IsNullOrWhiteSpace(cmdPath))
            return CmdInspection.Fail("CmdPath is empty.");

        var ext = Path.GetExtension(cmdPath);
        if (!ext.Equals(".cmd", StringComparison.OrdinalIgnoreCase)
            && !ext.Equals(".bat", StringComparison.OrdinalIgnoreCase))
        {
            return CmdInspection.Fail($"CmdPath must be a .cmd or .bat file (got '{ext}').");
        }

        if (!File.Exists(cmdPath))
        {
            var msg = Directory.Exists(cmdPath)
                ? $"CmdPath is a folder, not a script: {cmdPath}"
                : $"CmdPath not found (service account may not see this path/drive): {cmdPath}";
            return CmdInspection.Fail(msg);
        }

        string text;
        try
        {
            text = File.ReadAllText(cmdPath);
        }
        catch (Exception ex)
        {
            return CmdInspection.Fail($"CmdPath is not readable: {ex.Message}");
        }

        if (string.IsNullOrWhiteSpace(text))
            return CmdInspection.Fail("Cmd/BAT file is empty.");

        var tcfPaths = TcfPathRegex.Matches(text)
            .Select(m => m.Groups["path"].Value.Trim().Trim('"', '\''))
            .Where(p => !string.IsNullOrWhiteSpace(p))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        var flags = FlagRegex.Matches(text)
            .Select(m => m.Value)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(f => f, StringComparer.OrdinalIgnoreCase)
            .ToList();

        var mentionsTuflow = text.Contains("tuflow", StringComparison.OrdinalIgnoreCase);
        if (!mentionsTuflow && tcfPaths.Count == 0)
        {
            return CmdInspection.Fail(
                "Cmd/BAT does not look like a TUFLOW launch script (no 'tuflow' mention and no .tcf path found).");
        }

        var parts = new List<string>();
        if (tcfPaths.Count > 0)
            parts.Add($"tcf: {string.Join(", ", tcfPaths.Select(Path.GetFileName))}");
        if (flags.Count > 0)
            parts.Add($"flags: {string.Join(" ", flags)}");
        if (mentionsTuflow)
            parts.Add("mentions TUFLOW");

        return new CmdInspection(
            Ok: true,
            ErrorSummary: null,
            TcfPaths: tcfPaths,
            Flags: flags,
            Summary: parts.Count == 0 ? "Looks like a TUFLOW script" : string.Join("; ", parts));
    }
}

internal sealed record CmdInspection(
    bool Ok,
    string? ErrorSummary,
    IReadOnlyList<string> TcfPaths,
    IReadOnlyList<string> Flags,
    string? Summary)
{
    public static CmdInspection Fail(string error) =>
        new(false, error, [], [], null);
}
