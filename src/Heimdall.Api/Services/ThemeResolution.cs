using Heimdall.Api.Data;
using Microsoft.EntityFrameworkCore;

namespace Heimdall.Api.Services;

/// <summary>Resolves the theme actually being rendered this request: a built-in preset, or a
/// custom theme (which itself carries a base preset for structural CSS + brand logo behaviour).</summary>
public static class ThemeResolution
{
    public sealed record ActiveTheme(string BasePreset, CustomTheme? Custom)
    {
        public bool IsCustom => Custom is not null;
    }

    public static async Task<ActiveTheme> ResolveAsync(
        IConfiguration configuration,
        IRequestCookieCollection? cookies,
        HeimdallDbContext db)
    {
        var raw = UiTheme.Resolve(configuration, cookies);
        if (!UiTheme.TryParseCustomId(raw, out var id))
            return new ActiveTheme(raw, null);

        try
        {
            var custom = await db.CustomThemes.AsNoTracking().FirstOrDefaultAsync(t => t.Id == id);
            if (custom is not null)
                return new ActiveTheme(UiTheme.IsPreset(custom.BasePreset) ? custom.BasePreset : UiTheme.Cosmic, custom);
        }
        catch
        {
            // Custom theme table unreachable (mid-upgrade DB hiccup) — fail safe to Cosmic rather than 500 every page.
        }

        return new ActiveTheme(UiTheme.Cosmic, null);
    }
}
