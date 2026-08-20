namespace TuflowLauncher;

/// <summary>
/// Keep in sync with Heimdall.Shared.TuflowLaunchPath. Mapped letters are invisible to the agent service.
/// </summary>
internal static class LaunchPathRules
{
    private static readonly HashSet<char> LocalDriveLetters = ['C', 'D', 'E', 'F'];

    public static string? Validate(string? path, string fieldName)
    {
        if (string.IsNullOrWhiteSpace(path))
            return null;

        var p = path.Trim().Replace('/', '\\');
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

            return $"{fieldName} uses drive {letter}: which is not visible to the Heimdall agent service when nobody is logged on. Use UNC (\\\\server\\share\\...) instead of '{path}'.";
        }

        return $"{fieldName} must be a local path (C:\\ … F:\\) or UNC. Got '{path}'.";
    }
}
