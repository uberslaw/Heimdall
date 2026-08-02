using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Negotiate;
using Microsoft.Extensions.Options;

namespace Heimdall.Api.Services;

/// <summary>
/// Central gate for Staff Access: optional Negotiate challenge, Windows-to-email verification, and dev bypass.
/// Agent API-key endpoints never use this.
/// </summary>
public sealed class StaffAccessGuard(
    IOptions<StaffAccessOptions> options,
    WindowsStaffIdentityService identity,
    IWebHostEnvironment env)
{
    public bool IsWindowsAuthRequired =>
        options.Value.RequireWindowsAuth && !(env.IsDevelopment() && options.Value.AllowDevBypass);

    public bool IsDevBypassActive =>
        env.IsDevelopment() && options.Value.AllowDevBypass;

    /// <summary>
    /// Ensures Negotiate ran when required. Returns false if a challenge was issued (response started).
    /// </summary>
    public async Task<bool> EnsureWindowsAuthAsync(HttpContext ctx)
    {
        if (!IsWindowsAuthRequired)
            return true;

        var auth = await ctx.AuthenticateAsync(NegotiateDefaults.AuthenticationScheme);
        if (auth.Succeeded && auth.Principal?.Identity?.IsAuthenticated == true)
        {
            ctx.User = auth.Principal;
            return true;
        }

        await ctx.ChallengeAsync(NegotiateDefaults.AuthenticationScheme);
        return false;
    }

    /// <summary>
    /// Resolves the staff email for this request: cookie email verified against Windows identity, or auto from Windows.
    /// </summary>
    public string? TryGetVerifiedEmail(HttpContext ctx)
    {
        if (!IsWindowsAuthRequired)
            return StaffAuthService.TryGetEmail(ctx);

        if (identity.GetWindowsPrincipalName(ctx) is null)
            return null;

        var cookieEmail = StaffAuthService.TryGetEmail(ctx);
        if (cookieEmail is not null && identity.EmailMatchesWindowsUser(ctx, cookieEmail))
            return cookieEmail;

        return null;
    }

    /// <summary>
    /// Best registered staff email for the current Windows login, if any Remote Access Group lists it.
    /// </summary>
    public async Task<string?> TryResolveEmailFromWindowsAsync(
        HttpContext ctx,
        RemoteAccessGroupService groups,
        CancellationToken ct)
    {
        if (!IsWindowsAuthRequired)
            return null;

        if (identity.GetWindowsPrincipalName(ctx) is null)
            return null;

        foreach (var candidate in identity.GetCandidateEmails(ctx))
        {
            var found = await groups.FindGroupsForEmailAsync(candidate, ct);
            if (found.Count > 0)
                return WindowsStaffIdentityService.NormalizeEmail(candidate);
        }

        return null;
    }

    /// <summary>Sign-in allowed only when typed email matches Windows identity (or dev bypass).</summary>
    public bool CanSignInWithEmail(HttpContext ctx, string email)
    {
        if (!IsWindowsAuthRequired)
            return true;

        return identity.EmailMatchesWindowsUser(ctx, email);
    }
}
