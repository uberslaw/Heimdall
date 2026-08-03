namespace Heimdall.Api.Services;

/// <summary>Curated heading/body font choices for custom themes. Web-safe entries need no stylesheet link;
/// Google Fonts entries carry a families query fragment for the shared preconnect link in _Layout.</summary>
public static class ThemeFonts
{
    public sealed record FontOption(string Key, string Label, string CssFamily, string? GoogleFamilyParam);

    public const string DefaultHeading = "plex-serif";
    public const string DefaultBody = "plex-sans";

    public static readonly IReadOnlyList<FontOption> HeadingOptions =
    [
        new(DefaultHeading, "IBM Plex Serif (default)", "\"IBM Plex Serif\", Georgia, serif", null),
        new("playfair", "Playfair Display", "\"Playfair Display\", Georgia, serif", "Playfair+Display:wght@600;700"),
        new("space-grotesk", "Space Grotesk", "\"Space Grotesk\", system-ui, sans-serif", "Space+Grotesk:wght@600;700"),
        new("georgia", "Georgia (web-safe)", "Georgia, \"Times New Roman\", serif", null),
    ];

    public static readonly IReadOnlyList<FontOption> BodyOptions =
    [
        new(DefaultBody, "IBM Plex Sans (default)", "\"IBM Plex Sans\", system-ui, sans-serif", null),
        new("inter", "Inter", "\"Inter\", system-ui, sans-serif", "Inter:wght@400;500;600"),
        new("space-grotesk", "Space Grotesk", "\"Space Grotesk\", system-ui, sans-serif", "Space+Grotesk:wght@400;500"),
        new("system", "System UI (web-safe)", "system-ui, -apple-system, \"Segoe UI\", sans-serif", null),
    ];

    public static FontOption? FindHeading(string? key) =>
        string.IsNullOrWhiteSpace(key) ? null : HeadingOptions.FirstOrDefault(f => f.Key == key);

    public static FontOption? FindBody(string? key) =>
        string.IsNullOrWhiteSpace(key) ? null : BodyOptions.FirstOrDefault(f => f.Key == key);

    /// <summary>Builds the single Google Fonts stylesheet URL for whichever curated fonts need it.</summary>
    public static string? BuildGoogleFontsUrl(string? headingKey, string? bodyKey)
    {
        var families = new List<string>();
        var heading = FindHeading(headingKey);
        var body = FindBody(bodyKey);
        if (heading?.GoogleFamilyParam is not null) families.Add(heading.GoogleFamilyParam);
        if (body?.GoogleFamilyParam is not null && body.GoogleFamilyParam != heading?.GoogleFamilyParam)
            families.Add(body.GoogleFamilyParam);

        if (families.Count == 0) return null;
        return "https://fonts.googleapis.com/css2?" + string.Join("&", families.Select(f => $"family={f}")) + "&display=swap";
    }
}
