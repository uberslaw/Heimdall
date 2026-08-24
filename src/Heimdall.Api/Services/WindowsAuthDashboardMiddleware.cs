namespace Heimdall.Api.Services;

/// <summary>
/// Ensures Negotiate runs for HTML dashboard navigations so layout gates (Flood nav, admin links)
/// can resolve <c>DOMAIN\user</c> → email. Without this, only pages that call
/// <see cref="StaffAccessGuard.EnsureWindowsAuthAsync"/> (e.g. Access Lists) show Flood —
/// Fleet/Apps/etc. leave <c>HttpContext.User</c> anonymous and hide the menu.
/// </summary>
public sealed class WindowsAuthDashboardMiddleware(RequestDelegate next)
{
    public async Task InvokeAsync(HttpContext ctx, StaffAccessGuard staff)
    {
        if (staff.IsWindowsAuthRequired && ShouldEnsureWindowsAuth(ctx))
        {
            if (!await staff.EnsureWindowsAuthAsync(ctx))
                return; // Challenge issued — do not continue to Razor layout.
        }

        await next(ctx);
    }

    private static bool ShouldEnsureWindowsAuth(HttpContext ctx)
    {
        var path = ctx.Request.Path.Value ?? "";
        if (path.Length == 0 || path == "/")
            return true;

        // Agent / JSON / SSE APIs handle auth themselves (API key or explicit EnsureWindowsAuth).
        if (path.StartsWith("/api/", StringComparison.OrdinalIgnoreCase))
            return false;

        // Static assets
        if (path.StartsWith("/css/", StringComparison.OrdinalIgnoreCase)
            || path.StartsWith("/js/", StringComparison.OrdinalIgnoreCase)
            || path.StartsWith("/lib/", StringComparison.OrdinalIgnoreCase)
            || path.StartsWith("/img/", StringComparison.OrdinalIgnoreCase)
            || path.StartsWith("/favicon", StringComparison.OrdinalIgnoreCase))
            return false;

        // Database-mode redirect helper is a simple redirect; auth not required for the hop.
        if (path.Equals("/database-mode", StringComparison.OrdinalIgnoreCase))
            return false;

        // Only document navigations / Razor partials — not arbitrary POSTs from tools.
        if (HttpMethods.IsGet(ctx.Request.Method) || HttpMethods.IsHead(ctx.Request.Method))
            return true;

        // Form POSTs from the dashboard still need identity for admin/Flood handlers.
        var contentType = ctx.Request.ContentType ?? "";
        if (contentType.Contains("application/x-www-form-urlencoded", StringComparison.OrdinalIgnoreCase)
            || contentType.Contains("multipart/form-data", StringComparison.OrdinalIgnoreCase))
            return true;

        return false;
    }
}
