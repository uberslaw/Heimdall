namespace Heimdall.Api.Services;

public static class UiTheme
{
    public const string Original = "original";
    public const string Cosmic = "cosmic";
    public const string CookieName = "hd_theme";
    public const string ConfigKey = "Heimdall:UiTheme";

    public static string Normalize(string? value) =>
        string.Equals(value, Original, StringComparison.OrdinalIgnoreCase)
            ? Original
            : Cosmic;

    public static string Resolve(IConfiguration configuration, IRequestCookieCollection? cookies)
    {
        if (cookies?.TryGetValue(CookieName, out var cookie) == true && !string.IsNullOrWhiteSpace(cookie))
            return Normalize(cookie);

        return Normalize(configuration[ConfigKey]);
    }
}
