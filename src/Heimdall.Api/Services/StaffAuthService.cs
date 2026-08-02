namespace Heimdall.Api.Services;

/// <summary>
/// Staff session cookie: HttpOnly email set after group membership is confirmed and (when enabled)
/// <see cref="StaffAccessOptions.RequireWindowsAuth"/> verifies the email matches the browser's Windows login.
/// The cookie alone is not trusted when Windows auth is required — StaffAccessGuard re-checks identity on each request.
/// </summary>
public static class StaffAuthService
{
    public const string CookieName = "hd_staff_email";
    public static readonly TimeSpan SessionLifetime = TimeSpan.FromHours(12);

    public static void SignIn(HttpContext ctx, string email)
    {
        ctx.Response.Cookies.Append(CookieName, email.Trim().ToLowerInvariant(), new CookieOptions
        {
            Path = "/",
            HttpOnly = true,
            SameSite = SameSiteMode.Lax,
            MaxAge = SessionLifetime,
            IsEssential = true
        });
    }

    public static void SignOut(HttpContext ctx) =>
        ctx.Response.Cookies.Delete(CookieName, new CookieOptions { Path = "/" });

    public static string? TryGetEmail(HttpContext ctx) =>
        ctx.Request.Cookies.TryGetValue(CookieName, out var email) && !string.IsNullOrWhiteSpace(email)
            ? email.Trim().ToLowerInvariant()
            : null;
}
