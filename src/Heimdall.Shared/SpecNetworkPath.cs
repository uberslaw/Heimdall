namespace Heimdall.Shared;

/// <summary>
/// Network / non-local install paths that stay linked in the Spec catalog even when
/// temporarily missing from a machine inventory (e.g. Tuflow on P:\ or UNC).
/// </summary>
public static class SpecNetworkPath
{
    /// <summary>True for UNC shares or drive letters other than C: (case-insensitive).</summary>
    public static bool IsStickyNetworkPath(string? executablePath)
    {
        if (string.IsNullOrWhiteSpace(executablePath))
            return false;

        var p = executablePath.Trim().Replace('/', '\\');
        if (p.StartsWith(@"\\", StringComparison.Ordinal))
            return true;

        if (p.Length >= 2 && p[1] == ':')
        {
            var drive = char.ToUpperInvariant(p[0]);
            return drive is >= 'A' and <= 'Z' && drive != 'C';
        }

        return false;
    }
}
