namespace Heimdall.Api.Services;

public static class UiGoldVariant
{
    public const string Bright = "bright";
    public const string Champagne = "champagne";
    public const string Brass = "brass";
    public const string Obsidian = "obsidian";
    public const string CookieName = "hd_gold";
    public const string Default = Champagne;

    private static readonly HashSet<string> Valid = new(StringComparer.OrdinalIgnoreCase)
    {
        Bright, Champagne, Brass, Obsidian
    };

    public static string Normalize(string? value) =>
        !string.IsNullOrWhiteSpace(value) && Valid.Contains(value)
            ? value.ToLowerInvariant()
            : Default;

    public static string Resolve(IRequestCookieCollection? cookies) =>
        cookies?.TryGetValue(CookieName, out var cookie) == true && !string.IsNullOrWhiteSpace(cookie)
            ? Normalize(cookie)
            : Default;
}
