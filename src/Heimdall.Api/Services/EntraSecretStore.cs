using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Options;

namespace Heimdall.Api.Services;

/// <summary>
/// Loads Entra credentials from <c>%ProgramData%\Heimdall\secrets\entra.json</c>.
/// <see cref="EntraSecretDocument.ClientSecretProtected"/> is DPAPI LocalMachine ciphertext (not git, not plain text).
/// </summary>
public sealed class EntraSecretStore(ILogger<EntraSecretStore> log)
{
    public static string DefaultSecretsDirectory =>
        Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
            "Heimdall", "secrets");

    public static string DefaultSecretsPath => Path.Combine(DefaultSecretsDirectory, "entra.json");

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        WriteIndented = true
    };

    public string SecretsPath { get; } = DefaultSecretsPath;

    public bool FileExists => File.Exists(SecretsPath);

    public EntraSecretDocument? TryReadDocument()
    {
        if (!FileExists) return null;
        try
        {
            var json = File.ReadAllText(SecretsPath);
            return JsonSerializer.Deserialize<EntraSecretDocument>(json, JsonOptions);
        }
        catch (Exception ex)
        {
            log.LogError(ex, "Failed to read Entra secrets file {Path}", SecretsPath);
            return null;
        }
    }

    public string? TryUnprotectClientSecret(EntraSecretDocument doc)
    {
        if (string.IsNullOrWhiteSpace(doc.ClientSecretProtected))
            return null;

        if (!OperatingSystem.IsWindows())
        {
            log.LogWarning("Entra DPAPI secrets require Windows; cannot decrypt {Path}", SecretsPath);
            return null;
        }

        try
        {
            return UnprotectLocalMachine(doc.ClientSecretProtected);
        }
        catch (Exception ex)
        {
            log.LogError(ex, "Failed to DPAPI-unprotect Entra client secret from {Path}", SecretsPath);
            return null;
        }
    }

    [SupportedOSPlatform("windows")]
    public static string ProtectLocalMachine(string plainText)
    {
        var bytes = Encoding.UTF8.GetBytes(plainText);
        var protectedBytes = ProtectedData.Protect(bytes, optionalEntropy: null, DataProtectionScope.LocalMachine);
        return Convert.ToBase64String(protectedBytes);
    }

    [SupportedOSPlatform("windows")]
    public static string UnprotectLocalMachine(string protectedBase64)
    {
        var protectedBytes = Convert.FromBase64String(protectedBase64.Trim());
        var bytes = ProtectedData.Unprotect(protectedBytes, optionalEntropy: null, DataProtectionScope.LocalMachine);
        return Encoding.UTF8.GetString(bytes);
    }
}

public sealed class EntraSecretDocument
{
    public string? TenantId { get; set; }
    public string? ClientId { get; set; }
    /// <summary>Base64 DPAPI LocalMachine blob of the client secret.</summary>
    public string? ClientSecretProtected { get; set; }
    public string? DefaultNetBiosDomain { get; set; }
}

/// <summary>Overlays <see cref="EntraOptions"/> from the ProgramData DPAPI secrets file (file wins for set fields).</summary>
public sealed class EntraOptionsPostConfigure(EntraSecretStore store, ILogger<EntraOptionsPostConfigure> log)
    : IPostConfigureOptions<EntraOptions>
{
    public void PostConfigure(string? name, EntraOptions options)
    {
        var doc = store.TryReadDocument();
        if (doc is null)
        {
            if (!string.IsNullOrWhiteSpace(options.ClientSecret))
            {
                log.LogWarning(
                    "Heimdall:Entra:ClientSecret is set in configuration. Prefer DPAPI file {Path} (see Protect-HeimdallEntraSecret.ps1) so the secret is not plain text on disk.",
                    store.SecretsPath);
            }
            return;
        }

        if (!string.IsNullOrWhiteSpace(doc.TenantId))
            options.TenantId = doc.TenantId.Trim();
        if (!string.IsNullOrWhiteSpace(doc.ClientId))
            options.ClientId = doc.ClientId.Trim();
        if (!string.IsNullOrWhiteSpace(doc.DefaultNetBiosDomain))
            options.DefaultNetBiosDomain = doc.DefaultNetBiosDomain.Trim();

        var secret = store.TryUnprotectClientSecret(doc);
        if (!string.IsNullOrWhiteSpace(secret))
        {
            options.ClientSecret = secret;
            log.LogInformation("Loaded Entra client secret from DPAPI file {Path}", store.SecretsPath);
        }
        else if (store.FileExists && string.IsNullOrWhiteSpace(options.ClientSecret))
        {
            log.LogWarning("Entra secrets file present but client secret could not be decrypted: {Path}", store.SecretsPath);
        }
    }
}
