using Microsoft.AspNetCore.Mvc;

namespace Heimdall.Api.Services;

/// <summary>
/// Gates Flood hub. Full Flood = AdminEmails ∪ Full Flood list (config or Admin DB override).
/// Live-only = full ∪ Flood Live list. Checks staff cookie email and Windows candidate emails.
/// </summary>
public sealed class FloodAccessGuard(
    AccessAllowlistService allowlists,
    WindowsStaffIdentityService identity)
{
    public bool CanAccessFlood(HttpContext ctx) =>
        ResolveCandidateEmails(ctx).Any(e => allowlists.IsEmailAllowed(AccessAllowlistCatalog.FloodFull, e));

    public bool CanAccessFloodLive(HttpContext ctx) =>
        ResolveCandidateEmails(ctx).Any(e =>
            allowlists.IsEmailAllowed(AccessAllowlistCatalog.FloodFull, e)
            || allowlists.IsEmailAllowed(AccessAllowlistCatalog.FloodLive, e));

    /// <summary>Full Flood access only (not Live-only).</summary>
    public IActionResult? ForbidIfDenied(HttpContext ctx) =>
        CanAccessFlood(ctx) ? null : new StatusCodeResult(StatusCodes.Status403Forbidden);

    /// <summary>Live tab / SSE — full Flood or FloodLiveEmails.</summary>
    public IActionResult? ForbidIfLiveDenied(HttpContext ctx) =>
        CanAccessFloodLive(ctx) ? null : new StatusCodeResult(StatusCodes.Status403Forbidden);

    public bool IsLiveOnly(HttpContext ctx) =>
        CanAccessFloodLive(ctx) && !CanAccessFlood(ctx);

    public bool EmailOnFloodAllowlist(string? email) =>
        allowlists.IsEmailAllowed(AccessAllowlistCatalog.FloodFull, email);

    public bool EmailOnFloodLiveAllowlist(string? email) =>
        allowlists.IsEmailAllowed(AccessAllowlistCatalog.FloodLive, email);

    public IReadOnlyList<string> GetFloodAllowlist() =>
        allowlists.GetFloodFullEffective();

    public IReadOnlyList<string> GetFloodLiveAllowlist() =>
        allowlists.GetFloodLiveOnly();

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
