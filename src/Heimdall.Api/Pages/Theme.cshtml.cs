using System.Text.RegularExpressions;
using Heimdall.Api.Data;
using Heimdall.Api.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;

namespace Heimdall.Api.Pages;

public partial class ThemeModel(HeimdallDbContext db, IWebHostEnvironment env, IConfiguration configuration) : PageModel
{
    private static readonly string[] AllowedImageExtensions = [".jpg", ".jpeg", ".png", ".webp", ".gif"];
    private const long MaxUploadBytes = 8 * 1024 * 1024;

    public IReadOnlyList<ThemeCatalog.PremadeSwatch> Premades => ThemeCatalog.Premades;
    public IReadOnlyList<CustomTheme> SavedThemes { get; private set; } = [];
    public IReadOnlyList<string> ExistingImages { get; private set; } = [];
    public IReadOnlyList<ThemeFonts.FontOption> HeadingFontOptions => ThemeFonts.HeadingOptions;
    public IReadOnlyList<ThemeFonts.FontOption> BodyFontOptions => ThemeFonts.BodyOptions;

    public string ActiveRaw { get; private set; } = UiTheme.Cosmic;
    public bool ActiveIsCustom { get; private set; }
    public int? ActiveCustomId { get; private set; }
    public string ActiveBasePreset { get; private set; } = UiTheme.Cosmic;
    public string ActiveGold { get; private set; } = UiGoldVariant.Default;

    [BindProperty]
    public int? EditingId { get; set; }

    [BindProperty]
    public CustomThemeFormModel Form { get; set; } = new();

    [BindProperty]
    public string BackgroundExisting { get; set; } = "";

    [BindProperty]
    public string BackgroundUrl { get; set; } = "";

    [BindProperty]
    public IFormFile? BackgroundUpload { get; set; }

    public async Task OnGetAsync(int? edit)
    {
        await LoadAsync();

        if (edit is int id)
        {
            var existing = await db.CustomThemes.AsNoTracking().FirstOrDefaultAsync(t => t.Id == id);
            if (existing is not null)
            {
                EditingId = existing.Id;
                Form = CustomThemeFormModel.FromEntity(existing);
                BackgroundExisting = existing.BackgroundImagePath ?? "";
            }
        }
    }

    public async Task<IActionResult> OnPostSaveAsync()
    {
        await LoadAsync();

        if (string.IsNullOrWhiteSpace(Form.Name))
        {
            TempData["Error"] = "Give the theme a name.";
            return RedirectToPage(EditingId is int eid ? new { edit = eid } : null);
        }

        if (!UiTheme.IsPreset(Form.BasePreset))
            Form.BasePreset = UiTheme.Cosmic;

        var backgroundPath = await ResolveBackgroundPathAsync();
        if (backgroundPath.Error is not null)
        {
            TempData["Error"] = backgroundPath.Error;
            return RedirectToPage(EditingId is int eid2 ? new { edit = eid2 } : null);
        }

        CustomTheme entity;
        var now = DateTime.UtcNow;
        if (EditingId is int id)
        {
            var existing = await db.CustomThemes.FindAsync(id);
            if (existing is null)
            {
                TempData["Error"] = "Theme not found — it may have been deleted.";
                return RedirectToPage();
            }

            entity = existing;
        }
        else
        {
            entity = new CustomTheme { Name = Form.Name.Trim(), CreatedUtc = now };
            db.CustomThemes.Add(entity);
        }

        Form.ApplyTo(entity);
        entity.BackgroundImagePath = string.IsNullOrWhiteSpace(backgroundPath.Path) ? null : backgroundPath.Path;
        entity.UpdatedUtc = now;

        await db.SaveChangesAsync();

        ApplyThemeCookie(UiTheme.CustomToken(entity.Id));
        TempData["Message"] = $"Saved and applied “{entity.Name}”.";
        return RedirectToPage(new { edit = entity.Id });
    }

    public async Task<IActionResult> OnPostDeleteAsync(int id)
    {
        var theme = await db.CustomThemes.FindAsync(id);
        if (theme is null)
        {
            TempData["Error"] = "Theme not found.";
            return RedirectToPage();
        }

        var wasActive = UiTheme.TryParseCustomId(UiTheme.Resolve(configuration, Request.Cookies), out var activeId)
            && activeId == id;

        db.CustomThemes.Remove(theme);
        await db.SaveChangesAsync();

        if (wasActive)
            ApplyThemeCookie(UiTheme.IsPreset(theme.BasePreset) ? theme.BasePreset : UiTheme.Cosmic);

        TempData["Message"] = $"Deleted “{theme.Name}”.";
        return RedirectToPage();
    }

    private void ApplyThemeCookie(string value)
    {
        Response.Cookies.Append(UiTheme.CookieName, value, new CookieOptions
        {
            Path = "/",
            MaxAge = TimeSpan.FromDays(365),
            SameSite = SameSiteMode.Lax,
            IsEssential = true
        });
    }

    private async Task<(string? Path, string? Error)> ResolveBackgroundPathAsync()
    {
        if (BackgroundUpload is { Length: > 0 })
        {
            if (BackgroundUpload.Length > MaxUploadBytes)
                return (null, "Background image is too large (max 8 MB).");

            var ext = Path.GetExtension(BackgroundUpload.FileName).ToLowerInvariant();
            if (!AllowedImageExtensions.Contains(ext))
                return (null, "Background image must be .jpg, .png, .webp or .gif.");

            var dir = Path.Combine(env.WebRootPath, "img", "custom-themes");
            Directory.CreateDirectory(dir);
            var fileName = $"{Guid.NewGuid():N}{ext}";
            var fullPath = Path.Combine(dir, fileName);
            await using (var stream = System.IO.File.Create(fullPath))
            {
                await BackgroundUpload.CopyToAsync(stream);
            }

            return ($"/img/custom-themes/{fileName}", null);
        }

        if (!string.IsNullOrWhiteSpace(BackgroundUrl))
            return (BackgroundUrl.Trim(), null);

        if (!string.IsNullOrWhiteSpace(BackgroundExisting))
            return (BackgroundExisting.Trim(), null);

        return (null, null);
    }

    private async Task LoadAsync()
    {
        SavedThemes = await db.CustomThemes.AsNoTracking().OrderBy(t => t.Name).ToListAsync();
        ExistingImages = ScanExistingImages();

        ActiveRaw = UiTheme.Resolve(configuration, Request.Cookies);
        ActiveGold = UiGoldVariant.Resolve(Request.Cookies);
        if (UiTheme.TryParseCustomId(ActiveRaw, out var customId))
        {
            ActiveIsCustom = true;
            ActiveCustomId = customId;
            var active = SavedThemes.FirstOrDefault(t => t.Id == customId);
            ActiveBasePreset = active is not null && UiTheme.IsPreset(active.BasePreset) ? active.BasePreset : UiTheme.Cosmic;
        }
        else
        {
            ActiveBasePreset = ActiveRaw;
        }
    }

    private List<string> ScanExistingImages()
    {
        var dir = Path.Combine(env.WebRootPath, "img");
        if (!Directory.Exists(dir)) return [];

        return Directory.GetFiles(dir, "*.*", SearchOption.AllDirectories)
            .Where(f => AllowedImageExtensions.Contains(Path.GetExtension(f).ToLowerInvariant()))
            .Select(f => "/img/" + Path.GetRelativePath(dir, f).Replace('\\', '/'))
            .OrderBy(f => f, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    [GeneratedRegex("^#([0-9a-fA-F]{3}|[0-9a-fA-F]{6})$")]
    private static partial Regex HexPattern();

    public static string NormalizeHex(string? value, string fallback) =>
        !string.IsNullOrWhiteSpace(value) && HexPattern().IsMatch(value.Trim()) ? value.Trim() : fallback;
}

/// <summary>Form-bindable mirror of <see cref="CustomTheme"/> — kept separate from the EF entity so the
/// Theme page's model binder doesn't fight the entity's `required` members or DB-only fields.</summary>
public class CustomThemeFormModel
{
    public string Name { get; set; } = "";
    public string BasePreset { get; set; } = UiTheme.Cosmic;

    public string PrimaryHex { get; set; } = "#d4b86a";
    public string SecondaryHex { get; set; } = "#c8ced8";
    public string AccentHex { get; set; } = "#a88838";

    public string TextHex { get; set; } = "#eef1f8";
    public string MutedHex { get; set; } = "#a4aec4";

    public string PanelHex { get; set; } = "#0a0e1a";
    public double PanelOpacity { get; set; } = 0.72;
    public string PanelAltHex { get; set; } = "#0e1220";
    public double PanelAltOpacity { get; set; } = 0.80;

    public string HeaderBgHex { get; set; } = "#060810";
    public double HeaderBgOpacity { get; set; } = 0.72;

    public string BorderHex { get; set; } = "#c0c6d0";
    public double BorderOpacity { get; set; } = 0.16;
    public string GoldHex { get; set; } = "#d4b86a";

    public string ShadeHex { get; set; } = "#ffecbe";
    public double ShadeOpacityPercent { get; set; } = 12;

    public string HoverHex { get; set; } = "#ecd898";

    public string BackgroundHex { get; set; } = "#060810";
    public double BackgroundOverlayOpacity { get; set; } = 0.38;

    public string? HeadingFont { get; set; }
    public string? BodyFont { get; set; }

    public static CustomThemeFormModel FromEntity(CustomTheme t) => new()
    {
        Name = t.Name,
        BasePreset = t.BasePreset,
        PrimaryHex = t.PrimaryHex,
        SecondaryHex = t.SecondaryHex,
        AccentHex = t.AccentHex,
        TextHex = t.TextHex,
        MutedHex = t.MutedHex,
        PanelHex = t.PanelHex,
        PanelOpacity = t.PanelOpacity,
        PanelAltHex = t.PanelAltHex,
        PanelAltOpacity = t.PanelAltOpacity,
        HeaderBgHex = t.HeaderBgHex,
        HeaderBgOpacity = t.HeaderBgOpacity,
        BorderHex = t.BorderHex,
        BorderOpacity = t.BorderOpacity,
        GoldHex = t.GoldHex,
        ShadeHex = t.ShadeHex,
        ShadeOpacityPercent = t.ShadeOpacityPercent,
        HoverHex = t.HoverHex,
        BackgroundHex = t.BackgroundHex,
        BackgroundOverlayOpacity = t.BackgroundOverlayOpacity,
        HeadingFont = t.HeadingFont,
        BodyFont = t.BodyFont
    };

    public void ApplyTo(CustomTheme t)
    {
        t.Name = Name.Trim();
        t.BasePreset = BasePreset;
        t.PrimaryHex = ThemeModel.NormalizeHex(PrimaryHex, t.PrimaryHex);
        t.SecondaryHex = ThemeModel.NormalizeHex(SecondaryHex, t.SecondaryHex);
        t.AccentHex = ThemeModel.NormalizeHex(AccentHex, t.AccentHex);
        t.TextHex = ThemeModel.NormalizeHex(TextHex, t.TextHex);
        t.MutedHex = ThemeModel.NormalizeHex(MutedHex, t.MutedHex);
        t.PanelHex = ThemeModel.NormalizeHex(PanelHex, t.PanelHex);
        t.PanelOpacity = Math.Clamp(PanelOpacity, 0, 1);
        t.PanelAltHex = ThemeModel.NormalizeHex(PanelAltHex, t.PanelAltHex);
        t.PanelAltOpacity = Math.Clamp(PanelAltOpacity, 0, 1);
        t.HeaderBgHex = ThemeModel.NormalizeHex(HeaderBgHex, t.HeaderBgHex);
        t.HeaderBgOpacity = Math.Clamp(HeaderBgOpacity, 0, 1);
        t.BorderHex = ThemeModel.NormalizeHex(BorderHex, t.BorderHex);
        t.BorderOpacity = Math.Clamp(BorderOpacity, 0, 1);
        t.GoldHex = ThemeModel.NormalizeHex(GoldHex, t.GoldHex);
        t.ShadeHex = ThemeModel.NormalizeHex(ShadeHex, t.ShadeHex);
        t.ShadeOpacityPercent = Math.Clamp(ShadeOpacityPercent, 0, 100);
        t.HoverHex = ThemeModel.NormalizeHex(HoverHex, t.HoverHex);
        t.BackgroundHex = ThemeModel.NormalizeHex(BackgroundHex, t.BackgroundHex);
        t.BackgroundOverlayOpacity = Math.Clamp(BackgroundOverlayOpacity, 0, 1);
        t.HeadingFont = string.IsNullOrWhiteSpace(HeadingFont) ? null : HeadingFont;
        t.BodyFont = string.IsNullOrWhiteSpace(BodyFont) ? null : BodyFont;
    }
}
