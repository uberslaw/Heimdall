using System.Globalization;

namespace Heimdall.Api.Services;

/// <summary>
/// Client version helpers for the Client Version page and published-version checks.
/// Legacy SemVer strings (e.g. "0.1.0", "0.1.0+hash") map to simple integer <c>1</c>;
/// pure integer strings parse as that int. Keep scripts/Heimdall-VersionCompare.ps1 in sync.
/// </summary>
public static class VersionCompare
{
    /// <summary>Legacy / pre-integer agent builds are treated as simple version 1.</summary>
    public const int LegacySimpleVersion = 1;

    /// <summary>
    /// First simple version that understands the silent <c>UpdateClient</c> command.
    /// Agents below this need a one-time Launch Control / Install.lnk bootstrap install.
    /// </summary>
    public const int MinUpdateClientVersion = 3;

    /// <summary>
    /// True when the agent can process silent UpdateClient (simple version ≥ <see cref="MinUpdateClientVersion"/>).
    /// Null/empty AgentVersion is treated as incapable (unknown → bootstrap).
    /// </summary>
    public static bool SupportsUpdateClient(string? agentVersion)
    {
        var simple = TryGetSimpleVersion(agentVersion);
        return simple is int n && n >= MinUpdateClientVersion;
    }

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
    /// Maps a reported or published version string to a simple integer.
    /// Pure integer cores (e.g. "2" from "2" or "2+hash") parse as that int; SemVer / other
    /// non-integer cores map to <see cref="LegacySimpleVersion"/>; empty/whitespace returns null.
    /// </summary>
    public static int? TryGetSimpleVersion(string? version)
    {
        var core = GetCoreVersion(version);
        if (core.Length == 0)
            return null;

        // Require an all-digit core so "2.0" / SemVer do not partially parse.
        if (core.All(char.IsDigit)
            && int.TryParse(core, NumberStyles.None, CultureInfo.InvariantCulture, out var n))
            return n;

        return LegacySimpleVersion;
    }

    /// <summary>True when the core version is a pure integer (not legacy SemVer mapped to 1).</summary>
    public static bool IsPureIntegerVersion(string? version)
    {
        var core = GetCoreVersion(version);
        return core.Length > 0 && core.All(char.IsDigit)
            && int.TryParse(core, NumberStyles.None, CultureInfo.InvariantCulture, out _);
    }

    /// <summary>
    /// Display form of the simple version (integer string), or empty when unknown.
    /// </summary>
    public static string FormatSimpleVersion(string? version)
    {
        var simple = TryGetSimpleVersion(version);
        return simple is null ? "" : simple.Value.ToString(CultureInfo.InvariantCulture);
    }

    /// <summary>
    /// Unlike the PS helper (which treats missing data as a tolerant "match" for install-time skip logic),
    /// this is used for the Client Version page status badge, where missing/unknown must read as NOT current.
    /// Compares simple integers when both sides parse.
    /// </summary>
    public static bool CoreVersionsMatch(string? versionA, string? versionB)
    {
        var a = TryGetSimpleVersion(versionA);
        var b = TryGetSimpleVersion(versionB);
        if (a is null || b is null)
            return false;
        return a.Value == b.Value;
    }

    /// <summary>
    /// publishedSimple − clientSimple when both known and client &lt; published; otherwise null
    /// (up to date, ahead, or unknown).
    /// </summary>
    public static int? GetVersionsBehind(string? publishedVersion, string? clientVersion)
    {
        var published = TryGetSimpleVersion(publishedVersion);
        var client = TryGetSimpleVersion(clientVersion);
        if (published is null || client is null)
            return null;
        if (client.Value >= published.Value)
            return null;
        return published.Value - client.Value;
    }
}
