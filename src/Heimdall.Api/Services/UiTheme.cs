namespace Heimdall.Api.Services;

/// <summary>
/// Active UI theme selection. A theme value is either one of the built-in presets
/// (<see cref="Original"/>, <see cref="Cosmic"/>, <see cref="DarkSlate"/>, <see cref="LightClean"/>)
/// or a custom theme reference in the form <c>custom:{id}</c> pointing at a <c>CustomThemes</c> row.
/// Custom themes always carry a <see cref="Data.CustomTheme.BasePreset"/> that supplies the structural
/// look (glass/blur for Cosmic, flat panels otherwise) while their own colours/fonts override the vars.
/// </summary>
public static class UiTheme
{
    public const string Original = "original";
    public const string Cosmic = "cosmic";
    public const string DarkSlate = "dark-slate";
    public const string LightClean = "light-clean";

    public const string CookieName = "hd_theme";
    public const string ConfigKey = "Heimdall:UiTheme";

    private const string CustomPrefix = "custom:";

    public static readonly IReadOnlyList<string> Presets = [Original, Cosmic, DarkSlate, LightClean];

    public static bool IsPreset(string? value) =>
        !string.IsNullOrWhiteSpace(value) && Presets.Contains(value, StringComparer.OrdinalIgnoreCase);

    /// <summary>Normalizes a raw value to a known preset key. Custom-theme tokens are preserved as-is
    /// (validity of the referenced id is checked later, against the database, by the caller).</summary>
    public static string Normalize(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return Cosmic;

        if (TryParseCustomId(value, out _))
            return value;

        var match = Presets.FirstOrDefault(p => string.Equals(p, value, StringComparison.OrdinalIgnoreCase));
        return match ?? Cosmic;
    }

    public static string Resolve(IConfiguration configuration, IRequestCookieCollection? cookies)
    {
        if (cookies?.TryGetValue(CookieName, out var cookie) == true && !string.IsNullOrWhiteSpace(cookie))
            return Normalize(cookie);

        return Normalize(configuration[ConfigKey]);
    }

    public static string CustomToken(int id) => $"{CustomPrefix}{id}";

    public static bool TryParseCustomId(string? value, out int id)
    {
        id = 0;
        if (string.IsNullOrWhiteSpace(value) || !value.StartsWith(CustomPrefix, StringComparison.OrdinalIgnoreCase))
            return false;

        return int.TryParse(value.AsSpan(CustomPrefix.Length), out id) && id > 0;
    }

    public static string Label(string preset) => preset switch
    {
        Original => "Original Recipe",
        Cosmic => "Cosmic",
        DarkSlate => "Dark Slate",
        LightClean => "Light Clean",
        _ => preset
    };
}
