using Microsoft.AspNetCore.Mvc;

namespace Heimdall.Api.Services;

/// <summary>
/// Gates Flood hub. Full Flood = configured site admins ∪ Full Flood list (config or Admin DB override).
/// Live-only = full ∪ Flood Live list. Checks staff cookie email and Windows candidate emails.
/// Challenges Negotiate (when required) before forbidding so direct /Flood hits are not a bare 403.
/// </summary>
public sealed class FloodAccessGuard(
    AccessAllowlistService allowlists,
    WindowsStaffIdentityService identity,
    StaffAccessGuard staff)
{
    /// <summary>
    /// Site admins (<see cref="StaffAccessGuard.IsConfiguredAdmin"/>) always have Full Flood.
    /// Same bar as Admin → Access lists; also mirrored in <see cref="AccessAllowlistService.GetFloodFullEffective"/>.
    /// </summary>
    public bool CanAccessFlood(HttpContext ctx) =>
        staff.IsConfiguredAdmin(ctx)
        || ResolveCandidateEmails(ctx).Any(e => allowlists.IsEmailAllowed(AccessAllowlistCatalog.FloodFull, e));

    public bool CanAccessFloodLive(HttpContext ctx) =>
        CanAccessFlood(ctx)
        || ResolveCandidateEmails(ctx).Any(e => allowlists.IsEmailAllowed(AccessAllowlistCatalog.FloodLive, e));

    /// <summary>Full Flood access only (not Live-only). Prefer <see cref="ForbidIfDeniedAsync"/> on page entry.</summary>
    public IActionResult? ForbidIfDenied(HttpContext ctx) =>
        CanAccessFlood(ctx) ? null : new StatusCodeResult(StatusCodes.Status403Forbidden);

    /// <summary>Live tab / SSE — full Flood or FloodLiveEmails. Prefer <see cref="ForbidIfLiveDeniedAsync"/> on page entry.</summary>
    public IActionResult? ForbidIfLiveDenied(HttpContext ctx) =>
        CanAccessFloodLive(ctx) ? null : new StatusCodeResult(StatusCodes.Status403Forbidden);

    /// <summary>Negotiate (when required) then full Flood allowlist check.</summary>
    public async Task<IActionResult?> ForbidIfDeniedAsync(HttpContext ctx)
    {
        if (!await staff.EnsureWindowsAuthAsync(ctx))
            return new EmptyResult();
        return ForbidIfDenied(ctx);
    }

    /// <summary>Negotiate (when required) then Live allowlist check.</summary>
    public async Task<IActionResult?> ForbidIfLiveDeniedAsync(HttpContext ctx)
    {
        if (!await staff.EnsureWindowsAuthAsync(ctx))
            return new EmptyResult();
        return ForbidIfLiveDenied(ctx);
    }

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
