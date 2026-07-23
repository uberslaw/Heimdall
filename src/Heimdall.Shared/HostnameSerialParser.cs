using System.Text.RegularExpressions;

namespace Heimdall.Shared;

/// <summary>
/// Parses asset serial / city / chassis hints from hostnames such as BNEDTABC12345 or SYDLT98765.
/// Default pattern: 3-letter city, optional DT (desktop) / LT (laptop), then serial remainder.
/// Override with Heimdall:HostnameSerialPattern (named groups city, chassis, serial).
/// </summary>
public static class HostnameSerialParser
{
    public const string DefaultPattern =
        @"^(?<city>[A-Za-z]{3})(?<chassis>DT|LT)?(?<serial>[A-Za-z0-9\-]+)$";

    public sealed record ParseResult(
        string? CityCode,
        string? ChassisHint,
        string? AssetSerial,
        bool Matched);

    public static ParseResult Parse(string? hostname, string? pattern = null)
    {
        if (string.IsNullOrWhiteSpace(hostname))
            return new ParseResult(null, null, null, false);

        var host = hostname.Trim();
        // Strip domain suffix if FQDN
        var dot = host.IndexOf('.');
        if (dot > 0)
            host = host[..dot];

        var rxPattern = string.IsNullOrWhiteSpace(pattern) ? DefaultPattern : pattern.Trim();
        try
        {
            var match = Regex.Match(host, rxPattern, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
            if (!match.Success)
                return new ParseResult(null, null, null, false);

            var city = GroupOrNull(match, "city")?.ToUpperInvariant();
            var chassis = GroupOrNull(match, "chassis")?.ToUpperInvariant();
            var serial = GroupOrNull(match, "serial");
            if (string.IsNullOrWhiteSpace(serial) || serial.Length < 3)
                return new ParseResult(city, chassis, null, match.Success);

            return new ParseResult(city, chassis, serial.Trim().ToUpperInvariant(), true);
        }
        catch (RegexParseException)
        {
            return new ParseResult(null, null, null, false);
        }
    }

    private static string? GroupOrNull(Match match, string name)
    {
        var g = match.Groups[name];
        if (!g.Success || string.IsNullOrWhiteSpace(g.Value))
            return null;
        return g.Value.Trim();
    }

    /// <summary>BIOS / OEM placeholder serials that should not be preferred over hostname-derived asset IDs.</summary>
    public static bool IsGenericBiosSerial(string? serial)
    {
        if (string.IsNullOrWhiteSpace(serial))
            return true;

        var s = serial.Trim();
        return s.Equals("To Be Filled By O.E.M.", StringComparison.OrdinalIgnoreCase)
               || s.Equals("Default string", StringComparison.OrdinalIgnoreCase)
               || s.Equals("None", StringComparison.OrdinalIgnoreCase)
               || s.Equals("System Serial Number", StringComparison.OrdinalIgnoreCase)
               || s.Equals("0", StringComparison.OrdinalIgnoreCase)
               || s.Equals("N/A", StringComparison.OrdinalIgnoreCase)
               || s.Equals("NA", StringComparison.OrdinalIgnoreCase)
               || s.Equals("Not Specified", StringComparison.OrdinalIgnoreCase)
               || s.Equals("Not Available", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Prefer hostname-derived asset serial when BIOS is generic or when hostname matched the pattern.
    /// </summary>
    public static string? PreferAssetSerial(string? biosSerial, string? hostnameSerial, bool hostnameMatched)
    {
        if (!string.IsNullOrWhiteSpace(hostnameSerial) &&
            (hostnameMatched || IsGenericBiosSerial(biosSerial)))
            return hostnameSerial.Trim();

        if (!IsGenericBiosSerial(biosSerial))
            return biosSerial!.Trim();

        return string.IsNullOrWhiteSpace(hostnameSerial) ? null : hostnameSerial.Trim();
    }
}
