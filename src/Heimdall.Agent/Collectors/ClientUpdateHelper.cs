using System.Diagnostics;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using Heimdall.Agent.Services;
using Heimdall.Shared.Contracts;
using Microsoft.Extensions.Logging;

namespace Heimdall.Agent.Collectors;

/// <summary>
/// Silent client self-update: download pack zip, verify hash, spawn detached installer.
/// Never reboots or logs off users; defers while any interactive session is Active.
/// </summary>
internal static class ClientUpdateHelper
{
    private static readonly object Gate = new();
    private static bool _inFlight;

    /// <summary>
    /// Returns true when the UpdateClient command should be acknowledged (installer spawned).
    /// Returns false when deferred or failed (keep command pending for retry).
    /// </summary>
    public static async Task<(bool Ack, bool Success, string Detail)> TryApplyAsync(
        ClientUpdateRequestDto request,
        SessionCollector sessions,
        HeimdallApiClient api,
        IConfiguration config,
        ILogger logger,
        CancellationToken ct)
    {
        lock (Gate)
        {
            if (_inFlight)
                return (false, true, "Applying: update already in progress");
        }

        if (sessions.HasActiveInteractiveSession)
            return (false, true, "DeferredWaitingForIdle: active interactive session — will retry when idle");

        lock (Gate) { _inFlight = true; }

        try
        {
            var updateRoot = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
                "Heimdall", "update");
            Directory.CreateDirectory(updateRoot);
            var zipPath = Path.Combine(updateRoot, "heimdall-client-agent.zip");
            // Unique per attempt — a previous installer (or AV) often still holds handles on the fixed
            // "extracted" folder, which made Directory.Delete throw and surface as
            // "cannot access …\update\extracted because it is being used by another process".
            var extractDir = Path.Combine(updateRoot, "extracted-" + Guid.NewGuid().ToString("n"));

            TryCleanupStaleExtractDirs(updateRoot, logger);

            logger.LogInformation("Client update: downloading pack (expect version {Version})", request.Version);

            var ok = await api.DownloadClientPackAsync(request.DownloadPath, zipPath, ct);
            if (!ok)
                return (false, false, "Pack download from API failed");

            var sha = HashFile(zipPath);
            if (!string.Equals(sha, request.ZipSha256, StringComparison.OrdinalIgnoreCase))
            {
                var exp = request.ZipSha256.Length >= 12 ? request.ZipSha256[..12] : request.ZipSha256;
                return (false, false, $"zip SHA256 mismatch (got {sha[..12]}… expected {exp}…)");
            }

            ZipFile.ExtractToDirectory(zipPath, extractDir);

            var packRoot = extractDir;
            var nested = Path.Combine(extractDir, "Heimdall-Client");
            if (Directory.Exists(nested) && File.Exists(Path.Combine(nested, "Install-WorkstationCollector.cmd")))
                packRoot = nested;
            else if (!File.Exists(Path.Combine(packRoot, "Install-WorkstationCollector.cmd")))
            {
                var found = Directory.EnumerateFiles(extractDir, "Install-WorkstationCollector.cmd", SearchOption.AllDirectories).FirstOrDefault();
                if (found is null)
                    return (false, false, "Install-WorkstationCollector.cmd missing from pack");
                packRoot = Path.GetDirectoryName(found)!;
            }

            var (apiUrl, apiKey, machineGroup) = ReadInstallSettings(config);
            var installCmd = Path.Combine(packRoot, "Install-WorkstationCollector.cmd");
            var args =
                $"/c set HEIMDALL_SKIP_LAUNCH=1&& \"{installCmd}\" -ApiUrl \"{apiUrl}\" -ApiKey \"{apiKey}\" -MachineGroup \"{machineGroup}\"";

            if (sessions.HasActiveInteractiveSession)
                return (false, true, "DeferredWaitingForIdle: active session after download — will retry");

            var psi = new ProcessStartInfo
            {
                FileName = "cmd.exe",
                Arguments = args,
                WorkingDirectory = packRoot,
                UseShellExecute = false,
                CreateNoWindow = true,
                WindowStyle = ProcessWindowStyle.Hidden
            };

            logger.LogWarning("Client update: spawning silent installer for version {Version}", request.Version);
            var proc = Process.Start(psi);
            if (proc is null)
                return (false, false, "could not start installer process");

            return (true, true, $"Applying: silent installer started (pid {proc.Id}), target version {request.Version}");
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Client update failed");
            // Do not prefix "Failed: " — ClientVersion FormatUpdateProgress already prepends Phase.
            return (false, false, ex.Message);
        }
        finally
        {
            lock (Gate) { _inFlight = false; }
        }
    }

    /// <summary>
    /// Best-effort cleanup of prior extract folders. Locked dirs are renamed aside so they no longer
    /// block the next attempt; rename failures are ignored (unique extract path still proceeds).
    /// </summary>
    private static void TryCleanupStaleExtractDirs(string updateRoot, ILogger logger)
    {
        IEnumerable<string> candidates;
        try
        {
            candidates = Directory.EnumerateDirectories(updateRoot)
                .Where(d =>
                {
                    var name = Path.GetFileName(d);
                    return name.Equals("extracted", StringComparison.OrdinalIgnoreCase)
                           || name.StartsWith("extracted-", StringComparison.OrdinalIgnoreCase)
                           || name.StartsWith("extracted.old.", StringComparison.OrdinalIgnoreCase);
                })
                .ToList();
        }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "Client update: could not enumerate extract dirs under {Root}", updateRoot);
            return;
        }

        foreach (var dir in candidates)
        {
            try
            {
                Directory.Delete(dir, recursive: true);
                continue;
            }
            catch (Exception deleteEx)
            {
                logger.LogDebug(deleteEx, "Client update: could not delete {Dir}; trying rename", dir);
            }

            try
            {
                var quarantine = Path.Combine(
                    updateRoot,
                    "extracted.old." + DateTime.UtcNow.ToString("yyyyMMddHHmmss") + "-" + Guid.NewGuid().ToString("n")[..8]);
                Directory.Move(dir, quarantine);
            }
            catch (Exception renameEx)
            {
                logger.LogDebug(renameEx, "Client update: leaving locked extract dir {Dir}", dir);
            }
        }
    }

    private static (string ApiUrl, string ApiKey, string MachineGroup) ReadInstallSettings(IConfiguration config)
    {
        var apiUrl = config["Heimdall:ApiBaseUrl"] ?? "http://localhost:5080";
        var apiKey = config["Heimdall:ApiKey"] ?? "heimdall-poc-key";
        var group = config["Heimdall:MachineGroup"] ?? "POC";

        try
        {
            var exeDir = Path.GetDirectoryName(Environment.ProcessPath)
                         ?? AppContext.BaseDirectory;
            var settingsPath = Path.Combine(exeDir, "appsettings.json");
            if (File.Exists(settingsPath))
            {
                using var doc = JsonDocument.Parse(File.ReadAllText(settingsPath));
                if (doc.RootElement.TryGetProperty("Heimdall", out var h))
                {
                    if (h.TryGetProperty("ApiBaseUrl", out var u) && u.GetString() is { Length: > 0 } url)
                        apiUrl = url;
                    if (h.TryGetProperty("ApiKey", out var k) && k.GetString() is { Length: > 0 } key)
                        apiKey = key;
                    if (h.TryGetProperty("MachineGroup", out var g) && g.GetString() is { Length: > 0 } mg)
                        group = mg;
                }
            }
        }
        catch
        {
            /* keep config defaults */
        }

        return (apiUrl.TrimEnd('/'), apiKey, group);
    }

    private static string HashFile(string path)
    {
        using var stream = File.OpenRead(path);
        var hash = SHA256.HashData(stream);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }
}
