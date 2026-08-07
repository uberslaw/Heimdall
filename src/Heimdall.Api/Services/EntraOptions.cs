namespace Heimdall.Api.Services;

/// <summary>Microsoft Entra ID app registration used for Graph membership reads (not SSO).</summary>
public sealed class EntraOptions
{
    public const string SectionName = "Heimdall:Entra";

    /// <summary>Directory (tenant) ID. Prefer <c>%ProgramData%\Heimdall\secrets\entra.json</c>.</summary>
    public string? TenantId { get; set; }

    /// <summary>Application (client) ID. Prefer the ProgramData secrets file.</summary>
    public string? ClientId { get; set; }

    /// <summary>
    /// Client secret in plain text — <strong>do not put this in git or appsettings</strong>.
    /// Use <c>Protect-HeimdallEntraSecret.ps1</c> so the secret is DPAPI-encrypted under ProgramData.
    /// Env <c>Heimdall__Entra__ClientSecret</c> is a temporary fallback only.
    /// </summary>
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
