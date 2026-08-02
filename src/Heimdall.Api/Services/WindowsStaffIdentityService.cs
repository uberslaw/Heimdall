using Microsoft.Extensions.Options;

namespace Heimdall.Api.Services;

/// <summary>
/// Maps the Windows identity sent by the browser (Negotiate/NTLM/Kerberos) to staff emails configured in
/// Remote Access Groups. Matching is case-insensitive.
/// </summary>
public sealed class WindowsStaffIdentityService(IOptions<StaffAccessOptions> options)
{
    /// <summary>Raw Windows principal name, e.g. DOMAIN\user or user@contoso.com (UPN).</summary>
    public string? GetWindowsPrincipalName(HttpContext ctx)
    {
        var user = ctx.User;
        if (user?.Identity?.IsAuthenticated != true)
            return null;

        var name = user.Identity.Name;
        if (string.IsNullOrWhiteSpace(name))
            return null;

        return name.Trim();
    }

    /// <summary>
    /// Candidate staff emails implied by the current Windows login. Used for auto sign-in when one matches
    /// a Remote Access Group registration.
    /// </summary>
    public IReadOnlyList<string> GetCandidateEmails(HttpContext ctx)
    {
        var principal = GetWindowsPrincipalName(ctx);
        return principal is null ? [] : GetCandidateEmails(principal);
    }

    /// <summary>Whether <paramref name="email"/> belongs to the Windows user on this request.</summary>
    public bool EmailMatchesWindowsUser(HttpContext ctx, string email) =>
        EmailMatchesPrincipal(GetWindowsPrincipalName(ctx), email);

    public IReadOnlyList<string> GetCandidateEmails(string windowsPrincipalName)
    {
        var candidates = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var parsed = ParsePrincipal(windowsPrincipalName);

        if (parsed.Upn is { Length: > 0 })
            candidates.Add(NormalizeEmail(parsed.Upn));

        foreach (var suffix in options.Value.EmailDomainSuffixes.Where(s => !string.IsNullOrWhiteSpace(s)))
        {
            var trimmed = suffix.Trim().TrimStart('@');
            if (parsed.SamAccountName is { Length: > 0 })
                candidates.Add(NormalizeEmail($"{parsed.SamAccountName}@{trimmed}"));
        }

        return candidates.Where(c => c.Contains('@')).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
    }

    public bool EmailMatchesPrincipal(string? windowsPrincipalName, string email)
    {
        var normalized = NormalizeEmail(email);
        if (normalized.Length == 0 || windowsPrincipalName is null)
            return false;

        var candidates = GetCandidateEmails(windowsPrincipalName);
        if (candidates.Any(c => string.Equals(c, normalized, StringComparison.OrdinalIgnoreCase)))
            return true;

        var parsed = ParsePrincipal(windowsPrincipalName);
        var localPart = normalized.Split('@')[0];

        if (parsed.SamAccountName is { Length: > 0 }
            && string.Equals(parsed.SamAccountName, localPart, StringComparison.OrdinalIgnoreCase))
            return true;

        if (parsed.Upn is { Length: > 0 })
        {
            var upnLocal = parsed.Upn.Split('@')[0];
            if (string.Equals(upnLocal, localPart, StringComparison.OrdinalIgnoreCase))
                return true;
        }

        return false;
    }

    private static (string? SamAccountName, string? Upn) ParsePrincipal(string principalName)
    {
        if (principalName.Contains('@', StringComparison.Ordinal))
            return (null, principalName.Trim());

        var slash = principalName.IndexOf('\\');
        if (slash >= 0 && slash < principalName.Length - 1)
            return (principalName[(slash + 1)..].Trim(), null);

        return (principalName.Trim(), null);
    }

    public static string NormalizeEmail(string email) =>
        email.Trim().ToLowerInvariant();

    /// <summary>Human-readable label for UI, e.g. CONTOSO\jsmith.</summary>
    public static string FormatDisplayName(string? windowsPrincipalName) =>
        string.IsNullOrWhiteSpace(windowsPrincipalName) ? "unknown" : windowsPrincipalName;
}
