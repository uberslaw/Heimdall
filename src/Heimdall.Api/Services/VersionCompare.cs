namespace Heimdall.Api.Services;

/// <summary>
/// Mirrors scripts/Heimdall-VersionCompare.ps1 — strips InformationalVersion build metadata after
/// '+' before comparing (e.g. "0.1.0+549a17b6..." vs "0.1.0"). Keep both in sync if the format changes.
/// </summary>
public static class VersionCompare
{
    public static string GetCoreVersion(string? version)
    {
        if (string.IsNullOrWhiteSpace(version))
            return "";

        var v = version.Trim();
        var plusIdx = v.IndexOf('+');
        if (plusIdx >= 0)
            v = v[..plusIdx];
        return v.Trim();
    }

    /// <summary>
    /// Unlike the PS helper (which treats missing data as a tolerant "match" for install-time skip logic),
    /// this is used for the Clients page status badge, where missing/unknown must read as NOT current.
    /// </summary>
    public static bool CoreVersionsMatch(string? versionA, string? versionB)
    {
        var a = GetCoreVersion(versionA);
        var b = GetCoreVersion(versionB);
        if (a.Length == 0 || b.Length == 0)
            return false;
        return string.Equals(a, b, StringComparison.OrdinalIgnoreCase);
    }
}
