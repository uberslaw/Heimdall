namespace Heimdall.Shared;

/// <summary>
/// Path rules for unattended TUFLOW launches. HeimdallAgent runs as a Windows service
/// (typically LocalSystem) with no interactive logon, so mapped drive letters (P:\, S:\, T:\)
/// do not exist even when they work in Explorer. Use UNC (\\server\share\...) for network
/// locations, or a local volume letter on the modelling host.
/// </summary>
public static class TuflowLaunchPath
{
    /// <summary>Drive letters treated as local disks on Flood hosts. Other letters are treated as mapped shares.</summary>
    public static IReadOnlySet<char> AllowedLocalDriveLetters { get; } =
        new HashSet<char> { 'C', 'D', 'E', 'F' };

    private static readonly HashSet<char> LocalDriveLetters = ['C', 'D', 'E', 'F'];

    public static string? ValidateOptional(string? path, string fieldName)
    {
        if (string.IsNullOrWhiteSpace(path))
            return null;
        return ValidateRequired(path.Trim(), fieldName);
    }

    public static string? ValidateRequired(string path, string fieldName)
    {
        var p = path.Trim().Replace('/', '\\');
        if (p.Length == 0)
            return $"{fieldName} is required.";

        if (p.StartsWith(@"\\?\UNC\", StringComparison.OrdinalIgnoreCase))
            p = @"\\" + p[8..];
        else if (p.StartsWith(@"\\?\", StringComparison.Ordinal))
            p = p[4..];

        if (p.StartsWith(@"\\", StringComparison.Ordinal))
        {
            var rest = p[2..];
            var slash = rest.IndexOf('\\');
            if (slash <= 0 || slash >= rest.Length - 1)
                return $"{fieldName} UNC path must be \\\\server\\share\\... (got '{path}').";
            return null;
        }

        if (p.Length >= 3 && char.IsAsciiLetter(p[0]) && p[1] == ':' && p[2] == '\\')
        {
            var letter = char.ToUpperInvariant(p[0]);
            if (LocalDriveLetters.Contains(letter))
                return null;

            return $"{fieldName} uses drive {letter}: which is almost always a mapped network letter. " +
                   "The Heimdall agent runs as a Windows service and those letters disappear when nobody is logged on. " +
                   $"Use a UNC path (\\\\server\\share\\...) instead of '{path}'.";
        }

        if (Path.IsPathRooted(p))
        {
            return $"{fieldName} must be a local path (C:\\ … F:\\) or UNC (\\\\server\\share\\...). Got '{path}'.";
        }

        return $"{fieldName} must be an absolute local path or UNC (got '{path}').";
    }

    public static string? ValidateLaunch(
        string launchMode,
        string? exePath,
        string? tcfPath,
        string? cmdPath,
        string? workingDirectory,
        string? resultsFolder)
    {
        var cmd = string.Equals(launchMode, "Cmd", StringComparison.OrdinalIgnoreCase);
        if (cmd)
        {
            if (ValidateRequired(cmdPath ?? "", "CMD/BAT path") is { } cmdErr)
                return cmdErr;
        }
        else
        {
            if (ValidateRequired(exePath ?? "", "TUFLOW .exe path") is { } exeErr)
                return exeErr;
            if (ValidateRequired(tcfPath ?? "", "Tcf path") is { } tcfErr)
                return tcfErr;
        }

        if (ValidateOptional(workingDirectory, "Working directory") is { } wdErr)
            return wdErr;
        if (ValidateOptional(resultsFolder, "Results folder") is { } resErr)
            return resErr;
        return null;
    }
}
