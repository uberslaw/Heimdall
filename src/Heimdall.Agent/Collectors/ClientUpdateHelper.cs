using System.Diagnostics;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using Heimdall.Agent.Services;
using Heimdall.Shared;
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
    /// Durable installer lock written by <c>Install-WorkstationCollector.ps1</c>.
    /// Survives agent process stop/delete; cleared only on successful install.
    /// </summary>
    public const string InstallLockFileName = "install.lock";

    public const string InstallStateFileName = "install-state.json";

    /// <summary>Committed last-known-good agent tree under %ProgramData%\Heimdall\update\lkg\ (Phase 2).</summary>
    public const string LkgDirectoryName = "lkg";

    /// <summary>Pre-replace staging backup; not committed until install succeeds.</summary>
    public const string LkgStagingDirectoryName = "lkg.staging";

    /// <summary>Minutes after which a lock with a dead owner is treated as stale (may start a new update).</summary>
    public const int InstallLockStaleMinutes = 30;

    /// <summary>True while a silent UpdateClient download/extract/spawn is in progress, or a durable install lock is held.</summary>
    public static bool IsUpdateInFlight
    {
        get
        {
            lock (Gate)
            {
                if (_inFlight)
                    return true;
            }

            return IsDurableInstallLockHeld(out _);
        }
    }

    internal static string GetUpdateRoot() =>
        Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
            "Heimdall", "update");

    /// <summary>
    /// True when %ProgramData%\Heimdall\update\install.lock exists and is not stale
    /// (live owner pid, or age under <see cref="InstallLockStaleMinutes"/>).
    /// </summary>
    public static bool IsDurableInstallLockHeld(out string detail)
    {
        detail = "";
        try
        {
            var lockPath = Path.Combine(GetUpdateRoot(), InstallLockFileName);
            if (!File.Exists(lockPath))
                return false;

            var text = File.ReadAllText(lockPath);
            var ownerPid = 0;
            DateTimeOffset? startedUtc = null;
            foreach (var raw in text.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries))
            {
                var line = raw.Trim();
                if (line.StartsWith("pid=", StringComparison.OrdinalIgnoreCase)
                    && int.TryParse(line.AsSpan(4), out var pid))
                    ownerPid = pid;
                else if (line.StartsWith("startedUtc=", StringComparison.OrdinalIgnoreCase)
                         && DateTimeOffset.TryParse(line.AsSpan("startedUtc=".Length), out var started))
                    startedUtc = started;
            }

            var ageMinutes = startedUtc is { } s
                ? (DateTimeOffset.UtcNow - s).TotalMinutes
                : (DateTimeOffset.UtcNow - File.GetLastWriteTimeUtc(lockPath)).TotalMinutes;

            var ownerAlive = false;
            if (ownerPid > 0)
            {
                try
                {
                    using var _ = Process.GetProcessById(ownerPid);
                    ownerAlive = true;
                }
                catch
                {
                    ownerAlive = false;
                }
            }

            if (ownerAlive)
            {
                detail = $"install.lock held by live pid {ownerPid} (age {ageMinutes:0}m)";
                return true;
            }

            if (ageMinutes < InstallLockStaleMinutes)
            {
                detail = $"install.lock present (pid {ownerPid} not running, age {ageMinutes:0}m < {InstallLockStaleMinutes}m stale threshold)";
                return true;
            }

            detail = $"stale install.lock (pid {ownerPid}, age {ageMinutes:0}m) — ignored for UpdateClient gate";
            return false;
        }
        catch (Exception ex)
        {
            detail = "install.lock check failed: " + ex.Message;
            // Fail closed: if we cannot read the lock, avoid racing a possible installer.
            return true;
        }
    }

    /// <summary>
    /// Extract folder under %ProgramData%\Heimdall\update\:
    /// <c>extracted-v{version}-{yyyyMMdd-HHmmss}</c> (unique per attempt; readable for support).
    /// Legacy bare <c>extracted</c> / <c>extracted-{guid}</c> are cleaned up when possible.
    /// </summary>
    internal static string BuildExtractFolderName(string? version, DateTime? utcStamp = null)
    {
        var stamp = (utcStamp ?? DateTime.UtcNow).ToString("yyyyMMdd-HHmmss");
        return "extracted-v" + ClientPackFolderNames.SanitizeVersion(version) + "-" + stamp;
    }

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

        if (IsDurableInstallLockHeld(out var lockDetail))
            return (false, true, "Applying: durable install.lock present — " + lockDetail);

        if (sessions.HasActiveInteractiveSession)
            return (false, true, "DeferredWaitingForIdle: active interactive session — will retry when idle");

        lock (Gate) { _inFlight = true; }
        var installerWaiterOwnsGate = false;

        try
        {
            // Re-check after taking in-process gate (installer may have just started).
            if (IsDurableInstallLockHeld(out lockDetail))
                return (false, true, "Applying: durable install.lock present — " + lockDetail);

            var updateRoot = GetUpdateRoot();
            Directory.CreateDirectory(updateRoot);
            var zipPath = Path.Combine(updateRoot, "heimdall-client-agent.zip");
            // Versioned + timestamp — readable on the host, unique per attempt. Avoids the old fixed
            // "extracted" folder that often stayed locked by a previous installer / AV.
            var extractLeaf = BuildExtractFolderName(request.Version);
            var extractDir = Path.Combine(updateRoot, extractLeaf);
            if (Directory.Exists(extractDir))
                extractDir = Path.Combine(updateRoot, extractLeaf + "-" + Guid.NewGuid().ToString("n")[..6]);

            TryCleanupStaleExtractDirs(updateRoot, logger, keepExtractDir: extractDir);

            logger.LogInformation(
                "Client update: downloading pack (expect version {Version}) → {ExtractDir}",
                request.Version, extractDir);

            var (ok, _) = await api.DownloadClientPackAsync(request.DownloadPath, zipPath, ct);
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
            // NOPAUSE so silent Deploy never blocks on pause; SKIP_LAUNCH skips Install.cmd wizard redirect.
            var args =
                $"/c set HEIMDALL_SKIP_LAUNCH=1&& set HEIMDALL_NOPAUSE=1&& \"{installCmd}\" -ApiUrl \"{apiUrl}\" -ApiKey \"{apiKey}\" -MachineGroup \"{machineGroup}\"";

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

            logger.LogWarning(
                "Client update: spawning silent installer for version {Version} from {PackRoot}",
                request.Version, packRoot);
            var proc = Process.Start(psi);
            if (proc is null)
                return (false, false, "could not start installer process");

            // Keep IsUpdateInFlight until the installer exits — clearing immediately allowed a second
            // UpdateClient (same pending command / next config poll) to race: stop/delete service while
            // the first install was still starting (seen on BNEDT4CE548CX13 2026-08-13 ~05:39).
            var pid = proc.Id;
            installerWaiterOwnsGate = true;
            _ = Task.Run(() =>
            {
                try
                {
                    if (!proc.HasExited)
                        proc.WaitForExit(milliseconds: 15 * 60 * 1000);
                    logger.LogInformation(
                        "Client update: installer pid {Pid} exited with {Code}",
                        pid, proc.HasExited ? proc.ExitCode : -1);
                }
                catch (Exception ex)
                {
                    logger.LogDebug(ex, "Client update: wait for installer pid {Pid} ended", pid);
                }
                finally
                {
                    try { proc.Dispose(); } catch { /* ignore */ }
                    lock (Gate) { _inFlight = false; }
                }
            });

            return (true, true, $"Applying: silent installer started (pid {pid}), target version {request.Version}, extract {Path.GetFileName(extractDir)}");
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Client update failed");
            // Do not prefix "Failed: " — ClientVersion FormatUpdateProgress already prepends Phase.
            return (false, false, ex.Message);
        }
        finally
        {
            if (!installerWaiterOwnsGate)
                lock (Gate) { _inFlight = false; }
        }
    }

    /// <summary>
    /// Best-effort cleanup of prior extract folders. Skips the folder about to be used, and anything
    /// touched in the last 30 minutes (running installer .cmd lives under extract — deleting it mid-run
    /// surfaces as "The batch file cannot be found").
    /// </summary>
    private static void TryCleanupStaleExtractDirs(string updateRoot, ILogger logger, string? keepExtractDir = null)
    {
        IEnumerable<string> candidates;
        try
        {
            var grace = DateTime.UtcNow.AddMinutes(-30);
            candidates = Directory.EnumerateDirectories(updateRoot)
                .Where(d =>
                {
                    var name = Path.GetFileName(d);
                    if (!(name.Equals("extracted", StringComparison.OrdinalIgnoreCase)
                          || name.StartsWith("extracted-", StringComparison.OrdinalIgnoreCase)
                          || name.StartsWith("extracted.old.", StringComparison.OrdinalIgnoreCase)))
                        return false;
                    if (keepExtractDir is not null
                        && string.Equals(
                            Path.GetFullPath(d).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
                            Path.GetFullPath(keepExtractDir).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
                            StringComparison.OrdinalIgnoreCase))
                        return false;
                    try
                    {
                        if (Directory.GetLastWriteTimeUtc(d) > grace)
                            return false;
                    }
                    catch { /* treat as eligible */ }
                    return true;
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
