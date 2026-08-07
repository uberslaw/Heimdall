using Azure.Identity;
using Microsoft.Extensions.Options;
using Microsoft.Graph;
using Microsoft.Graph.Models;
using Microsoft.Graph.Models.ODataErrors;

namespace Heimdall.Api.Services;

/// <summary>App-only Microsoft Graph client for Entra group membership reads.</summary>
public sealed class EntraGraphService(IOptions<EntraOptions> options, ILogger<EntraGraphService> log)
{
    private readonly EntraOptions _opts = options.Value;
    private GraphServiceClient? _client;

    public bool IsConfigured => _opts.IsConfigured;

    public string SetupHint =>
        "Configure Entra via scripts/Protect-HeimdallEntraSecret.ps1 (writes DPAPI-encrypted "
        + $"%ProgramData%\\Heimdall\\secrets\\entra.json). App registration needs Application permissions "
        + "Group.Read.All + User.Read.All (or GroupMember.Read.All) with admin consent. "
        + "Do not put ClientSecret in git or appsettings.";

    private GraphServiceClient GetClient()
    {
        if (!_opts.IsConfigured)
            throw new InvalidOperationException(SetupHint);

        return _client ??= new GraphServiceClient(
            new ClientSecretCredential(_opts.TenantId!, _opts.ClientId!, _opts.ClientSecret!),
            ["https://graph.microsoft.com/.default"]);
    }

    /// <summary>
    /// Validates credentials and (if possible) Group.Read permission.
    /// Distinguishes “secret works but admin consent missing” from full readiness.
    /// </summary>
    public async Task<EntraProbeResult> ProbeAsync(CancellationToken ct)
    {
        if (!_opts.IsConfigured)
            return new EntraProbeResult(false, false, false, SetupHint);

        try
        {
            var credential = new ClientSecretCredential(_opts.TenantId!, _opts.ClientId!, _opts.ClientSecret!);
            var token = await credential.GetTokenAsync(
                new Azure.Core.TokenRequestContext(["https://graph.microsoft.com/.default"]), ct);
            if (string.IsNullOrWhiteSpace(token.Token))
                return new EntraProbeResult(true, false, false, "Token response was empty.");

            try
            {
                var page = await GetClient().Groups.GetAsync(r =>
                {
                    r.QueryParameters.Select = ["id", "displayName"];
                    r.QueryParameters.Top = 1;
                }, ct);
                _ = page?.Value;
                return new EntraProbeResult(true, true, true,
                    "Credentials and Group.Read permission look good.");
            }
            catch (ODataError ex) when (ex.ResponseStatusCode is 401 or 403)
            {
                return new EntraProbeResult(true, true, false,
                    "App can sign in, but Graph group read failed (likely missing admin consent for Group.Read.All / GroupMember.Read.All). "
                    + "Keep Manual/CSV membership on until consent is granted. "
                    + FormatGraphError("list groups", ex));
            }
            catch (ODataError ex)
            {
                return new EntraProbeResult(true, true, false, FormatGraphError("list groups", ex));
            }
        }
        catch (Exception ex)
        {
            log.LogWarning(ex, "Entra probe failed");
            return new EntraProbeResult(true, false, false,
                "Could not obtain a Graph token — check TenantId, ClientId, and the DPAPI client secret. " + ex.Message);
        }
    }

    public async Task<EntraGroupInfo?> GetGroupAsync(string groupId, CancellationToken ct)
    {
        var id = NormalizeGuid(groupId)
            ?? throw new ArgumentException("Entra group id must be a GUID (Object ID from Entra admin center).");

        try
        {
            var group = await GetClient().Groups[id].GetAsync(r =>
            {
                r.QueryParameters.Select = ["id", "displayName", "mail", "securityEnabled"];
            }, ct);
            if (group?.Id is null) return null;
            return new EntraGroupInfo(group.Id, group.DisplayName, group.Mail, group.SecurityEnabled == true);
        }
        catch (ODataError ex) when (ex.ResponseStatusCode == 404)
        {
            return null;
        }
        catch (ODataError ex)
        {
            log.LogWarning(ex, "Graph GetGroup failed for {GroupId}: {Code} {Message}",
                id, ex.Error?.Code, ex.Error?.Message);
            throw new InvalidOperationException(FormatGraphError("read group", ex), ex);
        }
    }

    /// <summary>Transitive user members of the group (users only; nested groups expanded).</summary>
    public async Task<IReadOnlyList<EntraUserMember>> ListGroupUserMembersAsync(string groupId, CancellationToken ct)
    {
        var id = NormalizeGuid(groupId)
            ?? throw new ArgumentException("Entra group id must be a GUID.");

        var client = GetClient();
        var results = new List<EntraUserMember>();
        try
        {
            var page = await client.Groups[id].TransitiveMembers.GetAsync(r =>
            {
                r.QueryParameters.Select =
                [
                    "id", "displayName", "mail", "userPrincipalName",
                    "onPremisesSamAccountName", "onPremisesDomainName"
                ];
                r.QueryParameters.Top = 100;
            }, ct);

            while (page is not null)
            {
                foreach (var item in page.Value ?? [])
                {
                    if (item is not User user) continue;
                    var mapped = MapUser(user);
                    if (mapped is not null)
                        results.Add(mapped);
                }

                if (string.IsNullOrEmpty(page.OdataNextLink))
                    break;

                page = await client.Groups[id].TransitiveMembers
                    .WithUrl(page.OdataNextLink)
                    .GetAsync(cancellationToken: ct);
            }
        }
        catch (ODataError ex)
        {
            log.LogWarning(ex, "Graph ListGroupMembers failed for {GroupId}: {Code} {Message}",
                id, ex.Error?.Code, ex.Error?.Message);
            throw new InvalidOperationException(FormatGraphError("list group members", ex), ex);
        }

        return results
            .GroupBy(m => m.Username, StringComparer.OrdinalIgnoreCase)
            .Select(g => g.First())
            .OrderBy(m => m.Username, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private EntraUserMember? MapUser(User user)
    {
        var sam = NullIfEmpty(user.OnPremisesSamAccountName);
        var upn = NullIfEmpty(user.UserPrincipalName);
        var mail = NullIfEmpty(user.Mail);
        var username = sam
            ?? LocalPart(upn)
            ?? LocalPart(mail);
        if (username is null)
            return null;

        var domain = NullIfEmpty(user.OnPremisesDomainName)
            ?? NullIfEmpty(_opts.DefaultNetBiosDomain);
        var email = mail ?? upn;
        return new EntraUserMember(
            username,
            domain,
            NullIfEmpty(user.DisplayName),
            email);
    }

    private static string? LocalPart(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        var at = value.IndexOf('@');
        var local = at > 0 ? value[..at] : value;
        return NullIfEmpty(local);
    }

    private static string? NullIfEmpty(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    public static string? NormalizeGuid(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        return Guid.TryParse(value.Trim(), out var g) ? g.ToString("D") : null;
    }

    private static string FormatGraphError(string action, ODataError ex)
    {
        var code = ex.Error?.Code ?? $"HTTP {ex.ResponseStatusCode}";
        var msg = ex.Error?.Message ?? ex.Message;
        return $"Microsoft Graph could not {action}: {code} — {msg}";
    }
}

public sealed record EntraGroupInfo(string Id, string? DisplayName, string? Mail, bool SecurityEnabled);

public sealed record EntraUserMember(string Username, string? Domain, string? DisplayName, string? Email);

public sealed record EntraProbeResult(
    bool SecretsPresent,
    bool TokenOk,
    bool GroupReadOk,
    string Message);
