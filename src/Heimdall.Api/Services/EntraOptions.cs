namespace Heimdall.Api.Services;

/// <summary>Microsoft Entra ID app registration used for Graph membership reads (not SSO).</summary>
public sealed class EntraOptions
{
    public const string SectionName = "Heimdall:Entra";

    /// <summary>Directory (tenant) ID.</summary>
    public string? TenantId { get; set; }

    /// <summary>Application (client) ID.</summary>
    public string? ClientId { get; set; }

    /// <summary>Client secret. Prefer env var <c>Heimdall__Entra__ClientSecret</c> over committing to disk.</summary>
    public string? ClientSecret { get; set; }

    /// <summary>
    /// Optional NETBIOS-style domain stored on <see cref="Data.PersonTeam.Domain"/> when Graph
    /// does not return <c>onPremisesDomainName</c> (cloud-only users). Example: <c>ARUP</c>.
    /// </summary>
    public string? DefaultNetBiosDomain { get; set; }

    public bool IsConfigured =>
        !string.IsNullOrWhiteSpace(TenantId)
        && !string.IsNullOrWhiteSpace(ClientId)
        && !string.IsNullOrWhiteSpace(ClientSecret);
}
