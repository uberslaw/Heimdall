using Heimdall.Api.Data;

namespace Heimdall.Api.Services;

/// <summary>
/// Turns a <see cref="CustomTheme"/> row into CSS custom-property overrides. A handful of tokens
/// (gold light/dim/deep/glow, glass shine/rim, focus ring, contrasting button text) are derived from
/// the smaller set of colours a user actually picks, so the editor stays POC-simple while still driving
/// every hd-* variable the stylesheet already understands.
/// </summary>
public static class CustomThemeStyle
{
    public static IReadOnlyList<(string Var, string Value)> BuildVariables(CustomTheme t)
    {
        var shadeScale = t.ShadeOpacityPercent / 12.0;
        var vars = new List<(string, string)>
        {
            ("--hd-accent", t.PrimaryHex),
            ("--hd-accent-secondary", t.SecondaryHex),
            ("--hd-accent-dim", t.AccentHex),
            ("--hd-text", t.TextHex),
            ("--hd-muted", t.MutedHex),
            ("--hd-panel", ThemeColorMath.Rgba(t.PanelHex, t.PanelOpacity)),
            ("--hd-panel-2", ThemeColorMath.Rgba(t.PanelAltHex, t.PanelAltOpacity)),
            ("--hd-header-bg", ThemeColorMath.Rgba(t.HeaderBgHex, t.HeaderBgOpacity)),
            ("--hd-border", ThemeColorMath.Rgba(t.BorderHex, t.BorderOpacity)),
            ("--hd-gold", t.GoldHex),
            ("--hd-gold-light", ThemeColorMath.Lighten(t.GoldHex, 0.35)),
            ("--hd-gold-dim", ThemeColorMath.Darken(t.GoldHex, 0.30)),
            ("--hd-gold-deep", ThemeColorMath.Darken(t.GoldHex, 0.55)),
            ("--hd-gold-glow", ThemeColorMath.Rgba(t.GoldHex, 0.32)),
            ("--hd-glass-shine", ThemeColorMath.Rgba(t.ShadeHex, t.ShadeOpacityPercent / 100.0)),
            ("--hd-glass-rim", ThemeColorMath.Rgba(t.ShadeHex, Math.Clamp(0.55 * shadeScale, 0, 1))),
            ("--hd-glass-rim-dim", ThemeColorMath.Rgba(t.ShadeHex, Math.Clamp(0.35 * shadeScale, 0, 1))),
            ("--hd-silver-rim", ThemeColorMath.Rgba(t.SecondaryHex, Math.Clamp(0.42 * shadeScale, 0, 1))),
            ("--hd-link-hover", t.HoverHex),
            ("--hd-btn-primary-hover", t.HoverHex),
            ("--hd-btn-primary-text", ThemeColorMath.ContrastText(t.PrimaryHex)),
            ("--hd-focus-ring", ThemeColorMath.Rgba(t.PrimaryHex, 0.3)),
            ("--hd-bg", t.BackgroundHex)
        };

        var heading = ThemeFonts.FindHeading(t.HeadingFont);
        if (heading is not null) vars.Add(("--hd-font-heading", heading.CssFamily));

        var body = ThemeFonts.FindBody(t.BodyFont);
        if (body is not null) vars.Add(("--hd-font-body", body.CssFamily));

        return vars;
    }

    public static string BuildDeclarationBlock(CustomTheme t) =>
        string.Join(" ", BuildVariables(t).Select(v => $"{v.Var}: {v.Value};"));

    public static string BuildBodyBackground(CustomTheme t)
    {
        if (string.IsNullOrWhiteSpace(t.BackgroundImagePath))
            return $"background: {t.BackgroundHex};";

        var overlay = ThemeColorMath.Rgba(t.BackgroundHex, Math.Clamp(t.BackgroundOverlayOpacity, 0, 1));
        var url = t.BackgroundImagePath.Replace("\"", "");
        return $"background: linear-gradient({overlay}, {overlay}), url(\"{url}\") center center / cover no-repeat fixed, {t.BackgroundHex};";
    }

    /// <summary>Full inline &lt;style&gt; contents applying a custom theme document-wide. <paramref name="id"/>
    /// selects via [data-custom-theme] with enough specificity (and later source order, from _Layout placing
    /// this after the site.css link) to beat the built-in preset rules it sits on top of.</summary>
    public static string BuildOverrideStyleBlock(CustomTheme t, int id)
    {
        var selector = $"html[data-theme][data-custom-theme=\"{id}\"]";
        var decl = BuildDeclarationBlock(t);
        var body = BuildBodyBackground(t);
        return $"{selector} {{ {decl} }}\n{selector} body {{ {body} }}";
    }

    /// <summary>Inline style="" attribute value for the live-preview panel on the Theme editor
    /// (scoped to that one element instead of the whole document).</summary>
    public static string BuildInlineStyleAttr(CustomTheme t) =>
        BuildDeclarationBlock(t);
}
