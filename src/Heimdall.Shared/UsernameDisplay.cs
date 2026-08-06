namespace Heimdall.Shared;

/// <summary>
/// Display-time formatting for Windows / AD usernames. Strips <c>DOMAIN\</c> prefixes
/// (including <c>Global\</c> / <c>GLOBAL\</c>) so the UI shows the bare account token.
/// Does not mutate stored database values.
/// </summary>
public static class UsernameDisplay
{
    /// <summary>
    /// Returns the bare username for UI display.
    /// When <paramref name="username"/> already contains <c>DOMAIN\user</c>, the domain is stripped.
    /// When domain is supplied separately, it is omitted from the display (including Global).
    /// </summary>
    public static string Format(string? username, string? domain = null)
    {
        if (string.IsNullOrWhiteSpace(username))
            return "";

        var u = username.Trim();
        if (u.Contains('\\'))
            return StripDomainPrefix(u) ?? u;

        // Domain is stored separately — never surface DOMAIN\ for display.
        _ = domain;
        return u;
    }

    /// <summary>
    /// Like <see cref="Format"/> but returns an em dash when empty.
    /// </summary>
    public static string FormatOrDash(string? username, string? domain = null)
    {
        var formatted = Format(username, domain);
        return string.IsNullOrWhiteSpace(formatted) ? "—" : formatted;
    }

    /// <summary>
    /// Strips a leading <c>DOMAIN\</c> segment (case-insensitive for the domain token).
    /// Also normalizes embedded <c>Global\</c> / <c>GLOBAL\</c> prefixes.
    /// </summary>
    public static string? StripDomainPrefix(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return value;

        var s = value.Trim();
        var slash = s.IndexOf('\\');
        if (slash > 0 && slash < s.Length - 1)
            return s[(slash + 1)..].Trim();

        return s;
    }
}
