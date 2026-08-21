using System.IO.Compression;
using System.Text.Json;
using Heimdall.Shared;
using Microsoft.Extensions.Configuration;

namespace Heimdall.Api.Services;

/// <summary>
/// Copies a versioned client pack zip to a network share (same behaviour as Launch Control
/// Push pack zip to network share — non-destructive if the zip already exists).
/// </summary>
public sealed class ClientPackSharePushService(
    ClientPackReadinessService packReadiness,
    IConfiguration config,
    ILogger<ClientPackSharePushService> logger)
{
    public const string DefaultSharePath = @"\\global\australasia\bne\programs\installfiles\Heimdall";
    private static readonly string SettingsPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
        "Heimdall",
        "pack-zip-share.json");

    public string GetPreferredSharePath()
    {
        var remembered = TryReadRememberedSharePath();
        if (!string.IsNullOrWhiteSpace(remembered))
            return remembered.Trim().TrimEnd('\\');

        var configured = config["Heimdall:ClientPackSharePath"];
        if (!string.IsNullOrWhiteSpace(configured))
            return configured.Trim().TrimEnd('\\');

        return DefaultSharePath;
    }

    public void RememberSharePath(string sharePath)
    {
        if (string.IsNullOrWhiteSpace(sharePath))
            return;
        try
        {
            var dir = Path.GetDirectoryName(SettingsPath);
            if (!string.IsNullOrWhiteSpace(dir))
                Directory.CreateDirectory(dir);
            var payload = JsonSerializer.Serialize(new
            {
                SharePath = sharePath.Trim().TrimEnd('\\'),
                UpdatedUtc = DateTimeOffset.UtcNow
            });
            File.WriteAllText(SettingsPath, payload);
        }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "Could not persist pack zip share path.");
        }
    }

    /// <summary>
    /// Ensure <c>Heimdall-Client-v{version}.zip</c> beside the Ready pack, then copy to share if missing.
    /// </summary>
    public (bool Ok, string Message, string? DestPath, bool SkippedExisting) PushVersionedZipToShare(string? sharePathOverride)
    {
        var readiness = packReadiness.GetStatus();
        if (readiness.Status != ClientPackStatus.Ready
            || string.IsNullOrWhiteSpace(readiness.PackFolder)
            || !Directory.Exists(readiness.PackFolder))
        {
            return (false, "Client pack is not Ready. Pack client (or Refresh from disk) first.", null, false);
        }

        var version = readiness.PackProductVersion
                      ?? ClientPackFingerprint.TryReadProductVersion(readiness.PackFolder)
                      ?? "unknown";
        var zipName = ClientPackFolderNames.BuildZipFileName(version);
        var zipDir = Path.GetDirectoryName(readiness.PackFolder) ?? readiness.PackFolder;
        Directory.CreateDirectory(zipDir);
        var localZip = Path.Combine(zipDir, zipName);

        try
        {
            if (!File.Exists(localZip))
            {
                ZipFile.CreateFromDirectory(
                    readiness.PackFolder,
                    localZip,
                    CompressionLevel.Optimal,
                    includeBaseDirectory: false);
                logger.LogInformation("Created versioned pack zip {Zip}", localZip);
            }
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to create versioned pack zip {Zip}", localZip);
            return (false, $"Could not build {zipName}: {ex.Message}", null, false);
        }

        var share = (sharePathOverride ?? GetPreferredSharePath()).Trim().TrimEnd('\\');
        if (string.IsNullOrWhiteSpace(share))
            return (false, "Share folder path is empty.", null, false);

        try
        {
            if (!Directory.Exists(share))
                return (false, $"Share folder not found or not accessible: {share}. HeimdallApi (often LocalSystem) needs read/write on that UNC.", null, false);

            var dest = Path.Combine(share, zipName);
            if (File.Exists(dest))
            {
                RememberSharePath(share);
                return (true, $"Already on share (skipped overwrite): {dest}", dest, true);
            }

            File.Copy(localZip, dest, overwrite: false);
            RememberSharePath(share);
            logger.LogInformation("Copied pack zip to share {Dest}", dest);
            return (true, $"Copied {zipName} → {dest}", dest, false);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to copy pack zip to share {Share}", share);
            return (false,
                $"Copy to share failed: {ex.Message}. If the API runs as LocalSystem, grant the machine account write on the share, or use Launch Control from your login.",
                null,
                false);
        }
    }

    private static string? TryReadRememberedSharePath()
    {
        try
        {
            if (!File.Exists(SettingsPath))
                return null;
            using var doc = JsonDocument.Parse(File.ReadAllText(SettingsPath));
            if (doc.RootElement.TryGetProperty("SharePath", out var p))
                return p.GetString();
        }
        catch
        {
            /* ignore */
        }

        return null;
    }
}
