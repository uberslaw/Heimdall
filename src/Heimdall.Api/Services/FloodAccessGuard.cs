using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;

namespace Heimdall.Api.Services;

/// <summary>
/// Gates TUFLOW / Flood hub (Historical + Sims + Enrollment) to AdminEmails ∪ FloodTeamEmails.
/// Checks staff cookie email and Windows candidate emails (Negotiate).
/// </summary>
public sealed class FloodAccessGuard(
    IOptions<StaffAccessOptions> staffOptions,
    IConfiguration config,
    WindowsStaffIdentityService identity)
{
    public bool CanAccessFlood(HttpContext ctx)
    {
        foreach (var candidate in ResolveCandidateEmails(ctx))
        {
            if (EmailOnFloodAllowlist(candidate))
                return true;
        }

        return false;
    }

    public IActionResult? ForbidIfDenied(HttpContext ctx) =>
        CanAccessFlood(ctx) ? null : new StatusCodeResult(StatusCodes.Status403Forbidden);

    public bool EmailOnFloodAllowlist(string? email)
    {
        if (string.IsNullOrWhiteSpace(email))
            return false;

        var normalized = WindowsStaffIdentityService.NormalizeEmail(email);
        if (normalized.Length == 0)
            return false;

        return GetFloodAllowlist().Any(allowed =>
            string.Equals(
                WindowsStaffIdentityService.NormalizeEmail(allowed),
                normalized,
                StringComparison.OrdinalIgnoreCase));
    }

    public IReadOnlyList<string> GetFloodAllowlist()
    {
        var admins = staffOptions.Value.AdminEmails ?? [];
        var flood = config.GetSection("Heimdall:FloodTeamEmails").Get<string[]>() ?? [];
        return admins.Concat(flood)
            .Where(e => !string.IsNullOrWhiteSpace(e))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private IEnumerable<string> ResolveCandidateEmails(HttpContext ctx)
    {
        var cookie = StaffAuthService.TryGetEmail(ctx);
        if (cookie is not null)
            yield return cookie;

        if (identity.GetWindowsPrincipalName(ctx) is not null)
        {
            foreach (var c in identity.GetCandidateEmails(ctx))
                yield return c;
        }
    }
}
