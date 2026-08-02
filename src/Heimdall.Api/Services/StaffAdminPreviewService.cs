namespace Heimdall.Api.Services;

/// <summary>
/// Short-lived admin preview session for Staff group pages. Set from Admin → Remote Access Groups
/// after Windows identity matches <see cref="StaffAccessOptions.AdminEmails"/>.
/// </summary>
public static class StaffAdminPreviewService
{
    public const string CookieName = "hd_staff_admin_preview";

    public static void Enable(HttpContext ctx, StaffAccessOptions options) =>
        ctx.Response.Cookies.Append(CookieName, "1", new CookieOptions
        {
            Path = "/",
            HttpOnly = true,
            SameSite = SameSiteMode.Lax,
            MaxAge = TimeSpan.FromMinutes(Math.Max(1, options.AdminPreviewMinutes)),
            IsEssential = true
        });

    public static void Disable(HttpContext ctx) =>
        ctx.Response.Cookies.Delete(CookieName, new CookieOptions { Path = "/" });

    public static bool IsActive(HttpContext ctx) =>
        ctx.Request.Cookies.TryGetValue(CookieName, out var v) && v == "1";
}
