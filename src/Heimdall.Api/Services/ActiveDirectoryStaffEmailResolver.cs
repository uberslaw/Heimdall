using System.Collections.Concurrent;
using System.DirectoryServices.AccountManagement;
using System.Runtime.Versioning;

namespace Heimdall.Api.Services;

/// <summary>
/// Best-effort UPN / mail lookup for a domain sAMAccountName via in-process DirectoryServices.
/// Does not shell out to PowerShell — suitable for the HeimdallApi Windows service.
/// </summary>
public sealed class ActiveDirectoryStaffEmailResolver(ILogger<ActiveDirectoryStaffEmailResolver> logger)
{
    private static readonly TimeSpan CacheTtl = TimeSpan.FromMinutes(5);
    private readonly ConcurrentDictionary<string, CacheEntry> _cache = new(StringComparer.OrdinalIgnoreCase);

    public IReadOnlyList<string> ResolveEmails(string samAccountName)
    {
        if (string.IsNullOrWhiteSpace(samAccountName) || !OperatingSystem.IsWindows())
            return [];

        var key = samAccountName.Trim();
        if (_cache.TryGetValue(key, out var entry) && entry.ExpiresAt > DateTimeOffset.UtcNow)
            return entry.Emails;

        var emails = LookupAd(key);
        _cache[key] = new CacheEntry(emails, DateTimeOffset.UtcNow.Add(CacheTtl));
        return emails;
    }

    [SupportedOSPlatform("windows")]
    private IReadOnlyList<string> LookupAd(string samAccountName)
    {
        try
        {
            using var context = new PrincipalContext(ContextType.Domain);
            using var user = UserPrincipal.FindByIdentity(context, IdentityType.SamAccountName, samAccountName);
            if (user is null)
                return [];

            var emails = new List<string>(2);
            if (!string.IsNullOrWhiteSpace(user.UserPrincipalName))
                emails.Add(user.UserPrincipalName.Trim());
            if (!string.IsNullOrWhiteSpace(user.EmailAddress))
                emails.Add(user.EmailAddress.Trim());

            return emails;
        }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "Active Directory lookup failed for sAMAccountName {SamAccountName}", samAccountName);
            return [];
        }
    }

    private sealed record CacheEntry(IReadOnlyList<string> Emails, DateTimeOffset ExpiresAt);
}
