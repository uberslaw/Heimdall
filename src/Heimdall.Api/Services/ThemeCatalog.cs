namespace Heimdall.Api.Services;

/// <summary>Display metadata for the built-in theme presets shown on the Theme page gallery.
/// Swatch colours here are illustrative only — the presets' real behaviour lives in site.css.</summary>
public static class ThemeCatalog
{
    public sealed record PremadeSwatch(string Preset, string Label, string Description, string Bg, string Panel, string Accent, string Text);

    public static readonly IReadOnlyList<PremadeSwatch> Premades =
    [
        new(UiTheme.Original, "Original Recipe", "The classic POC look — teal accent on deep navy.", "#0f1419", "#182029", "#3db8a0", "#e7eef5"),
        new(UiTheme.Cosmic, "Cosmic", "Nebula backdrop, glass panels and a gold trim. Default for new installs.", "#060810", "rgba(20,24,38,0.9)", "#d4b86a", "#eef1f8"),
        new(UiTheme.DarkSlate, "Dark Slate", "Graphite and steel blue — calm, high-contrast, no glare.", "#12151a", "#1a1f27", "#5b8fd4", "#e5e9ee"),
        new(UiTheme.LightClean, "Light Clean", "Crisp whites with a deep teal accent for bright rooms.", "#f4f6f8", "#ffffff", "#178a72", "#1c2530"),
    ];
}
