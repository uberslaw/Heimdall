using System.Diagnostics;
using System.IO.Compression;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Heimdall.Api.Services;

public enum ClientPackStatus
{
    Checking,
    MissingPack,
    Stale,
    Ready,
    Packing,
    Error
}

public sealed record ClientPackReadiness(
    ClientPackStatus Status,
    string? Message,
    string? RepoRoot,
    string? PackFolder,
    string? LiveSourceFingerprint,
    string? PackSourceFingerprint,
    string? PackProductVersion,
    string? ZipSha256,
    bool CanPack,
    bool DeployUnlocked,
    string? ApiInstallNote,
    DateTimeOffset CheckedUtc,
    bool IsPacking = false,
    double? PackingElapsedSeconds = null,
    string? PackStage = null,
    string? PackStageLabel = null,
    int? LastPackExitCode = null,
    string? LastPackMessage = null,
    string? LastPackLogTail = null,
    string? LastPackLogPath = null,
    DateTimeOffset? LastPackFinishedUtc = null);

/// <summary>
/// Detects whether dist/Heimdall-Client matches live source on the API host (D8R),
/// can trigger Pack-WorkstationCollector.cmd, and builds a downloadable zip for agents.
/// Singleton — holds in-flight pack process + zip cache.
/// </summary>
public sealed class ClientPackReadinessService
{
    /// <summary>Kill hung pack scripts before UI/API stay stuck on Packing forever.</summary>
    internal static readonly TimeSpan PackTimeout = TimeSpan.FromMinutes(18);

    private const int LogTailMaxChars = 4000;
    private const int PackStageTotal = 5;

    private static readonly Regex PackStageLineRegex = new(
        @"^HEIMDALL_PACK_STAGE=(\d+)\s*/\s*(\d+)\s+(.+)$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private readonly IConfiguration _config;
    private readonly ILogger<ClientPackReadinessService> _logger;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly object _packLock = new();
    private Process? _packProcess;
    private DateTimeOffset? _packStartedUtc;
    private bool _packWatchActive;
    private bool _packCancelRequested;
    private string? _packStage;
    private string? _packStageLabel;
    private string? _currentPackLogPath;
    private string? _cachedZipPath;
    private string? _cachedZipSha;
    private string? _cachedZipFingerprint;

    private int? _lastPackExitCode;
    private string? _lastPackMessage;
    private string? _lastPackLogTail;
    private string? _lastPackLogPath;
    private DateTimeOffset? _lastPackFinishedUtc;

    public ClientPackReadinessService(
        IConfiguration config,
        ILogger<ClientPackReadinessService> logger,
        IServiceScopeFactory scopeFactory)
    {
        _config = config;
        _logger = logger;
        _scopeFactory = scopeFactory;
    }

    public string? ResolveRepoRoot()
    {
        var configured = _config["Heimdall:RepoRoot"];
        if (!string.IsNullOrWhiteSpace(configured) && Directory.Exists(configured))
            return Path.GetFullPath(configured);

        var candidates = new[]
        {
            Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..")),
            @"C:\Heimdall",
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Heimdall")
        };
        foreach (var c in candidates)
        {
            if (File.Exists(Path.Combine(c, "scripts", "Pack-WorkstationCollector.cmd")))
                return Path.GetFullPath(c);
        }

        return null;
    }

    public string ResolvePackFolder(string? repoRoot = null)
    {
        var root = repoRoot ?? ResolveRepoRoot();
        var rel = _config["Heimdall:ClientPackRelativePath"] ?? @"dist\Heimdall-Client";
        if (root is null)
            return Path.GetFullPath(rel);
        return Path.GetFullPath(Path.Combine(root, rel));
    }

    public ClientPackReadiness GetStatus()
    {
        var now = DateTimeOffset.UtcNow;
        if (IsPackRunning())
        {
            string? stage;
            string? stageLabel;
            double? elapsed;
            lock (_packLock)
            {
                elapsed = _packStartedUtc is { } started
                    ? (now - started).TotalSeconds
                    : null;
                stage = _packStage;
                stageLabel = _packStageLabel;
            }

            return new ClientPackReadiness(
                ClientPackStatus.Packing,
                "Client pack is building…",
                ResolveRepoRoot(),
                ResolvePackFolder(),
                null, null, null, null,
                CanPack: false,
                DeployUnlocked: false,
                ApiInstallNote: GetApiInstallNote(),
                CheckedUtc: now,
                IsPacking: true,
                PackingElapsedSeconds: elapsed,
                PackStage: stage,
                PackStageLabel: stageLabel,
                LastPackExitCode: _lastPackExitCode,
                LastPackMessage: _lastPackMessage,
                LastPackLogTail: _lastPackLogTail,
                LastPackLogPath: _lastPackLogPath,
                LastPackFinishedUtc: _lastPackFinishedUtc);
        }

        var repoRoot = ResolveRepoRoot();
        var packFolder = ResolvePackFolder(repoRoot);
        var canPack = repoRoot is not null
            && File.Exists(Path.Combine(repoRoot, "scripts", "Pack-WorkstationCollector.cmd"));

        string? liveFp = null;
        if (repoRoot is not null)
        {
            try
            {
                liveFp = ClientPackFingerprint.ComputeSourceFingerprint(repoRoot);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed computing source fingerprint");
                return WithLastPack(new ClientPackReadiness(
                    ClientPackStatus.Error,
                    "Could not hash client source: " + ex.Message,
                    repoRoot, packFolder, null, null, null, null,
                    canPack, false, GetApiInstallNote(), now));
            }
        }

        var exe = Path.Combine(packFolder, "payload", "Heimdall.Agent.exe");
        if (!File.Exists(exe))
        {
            return WithLastPack(new ClientPackReadiness(
                ClientPackStatus.MissingPack,
                canPack
                    ? "No client pack found — Pack client before Deploy."
                    : "No client pack and RepoRoot not configured (set Heimdall:RepoRoot).",
                repoRoot, packFolder, liveFp, null, null, null,
                canPack, false, GetApiInstallNote(), now));
        }

        var packFp = ClientPackFingerprint.TryReadSourceFingerprintFromVersionJson(packFolder);
        var productVersion = ClientPackFingerprint.TryReadProductVersion(packFolder);
        var manifestPath = Path.Combine(packFolder, "MANIFEST.sha256");
        var manifestMissing = !File.Exists(manifestPath);

        // Incomplete pack (hung/killed mid-run): VERSION/exe may exist without fingerprint or MANIFEST.
        if (manifestMissing || packFp is null)
        {
            return WithLastPack(new ClientPackReadiness(
                ClientPackStatus.Stale,
                manifestMissing && packFp is null
                    ? "Deploy locked: incomplete pack (missing MANIFEST.sha256 and sourceFingerprint) — Pack client again."
                    : manifestMissing
                        ? "Deploy locked: incomplete pack (missing MANIFEST.sha256) — Pack client again."
                        : "Deploy locked: pack has no source fingerprint — Pack client again.",
                repoRoot, packFolder, liveFp, packFp, productVersion, null,
                canPack, false, GetApiInstallNote(), now));
        }

        if (liveFp is not null && !string.Equals(liveFp, packFp, StringComparison.OrdinalIgnoreCase))
        {
            var hint =
                "Deploy locked: pack is stale (source fingerprint mismatch) — Pack client again.";
            // Common after packing from RepoRoot while Program Files API is an older build:
            // pack script hashes with current Write-ClientPackManifest.ps1; live hash uses the
            // installed Heimdall.Api.dll ClientPackFingerprint — they diverge until API republish.
            if (!string.IsNullOrEmpty(packFp) && !string.IsNullOrEmpty(liveFp)
                && !string.Equals(packFp, liveFp, StringComparison.OrdinalIgnoreCase))
            {
                hint =
                    "Deploy locked: pack fingerprint ≠ live source hash. "
                    + "If Pack just succeeded, republish the API (so Program Files matches RepoRoot fingerprint code), "
                    + "then Refresh from disk — or Pack client again after the API is current.";
            }

            return WithLastPack(new ClientPackReadiness(
                ClientPackStatus.Stale,
                hint,
                repoRoot, packFolder, liveFp, packFp, productVersion, null,
                canPack, false, GetApiInstallNote(), now));
        }

        string? zipSha = null;
        try
        {
            var (_, sha) = EnsureZip(packFolder, liveFp ?? packFp);
            zipSha = sha;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed ensuring client pack zip");
            return WithLastPack(new ClientPackReadiness(
                ClientPackStatus.Error,
                "Pack ready but zip failed: " + ex.Message,
                repoRoot, packFolder, liveFp, packFp, productVersion, null,
                canPack, false, GetApiInstallNote(), now));
        }

        return WithLastPack(new ClientPackReadiness(
            ClientPackStatus.Ready,
            "Ready — select machines and Deploy. Pack client rebuilds only when source changed (or you force a bump).",
            repoRoot, packFolder, liveFp, packFp, productVersion, zipSha,
            canPack, true, GetApiInstallNote(), now));
    }

    /// <summary>
    /// Drop cached agent zip and re-read pack folder / fingerprints from disk.
    /// Call after Launch Control (or any external) pack so Deploy unlocks without a redundant N+1 rebuild.
    /// </summary>
    public ClientPackReadiness RefreshFromDisk()
    {
        InvalidateZipCache();
        _logger.LogInformation("Client pack zip cache invalidated — re-reading {PackFolder}", ResolvePackFolder());
        var status = GetStatus();
        OpsFileLog.Write(
            "PackRefreshFromDisk",
            $"status={status.Status}; deployUnlocked={status.DeployUnlocked}; version={status.PackProductVersion}");
        return status;
    }

    /// <param name="force">
    /// When false and the on-disk pack already matches live source, skip rebuild (no version bump).
    /// When true, always run Pack-WorkstationCollector (N+1) even if Ready.
    /// </param>
    public (bool Started, string Message, string Outcome) TryStartPack(bool force = false)
    {
        // Outside pack lock: cheap Ready check so Launch Control packs are not wasted.
        if (!force)
        {
            var current = GetStatus();
            if (current.Status == ClientPackStatus.Ready)
            {
                InvalidateZipCache();
                try
                {
                    EnsureZip(current.PackFolder, current.LiveSourceFingerprint ?? current.PackSourceFingerprint);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Zip refresh failed for already-ready pack");
                }

                var ver = current.PackProductVersion ?? "?";
                return (false,
                    $"Pack on disk already matches source (v{ver}) at {current.PackFolder}. Deploy unlocked — no rebuild. Confirm Pack again to force an N+1 bump.",
                    "already-ready");
            }
        }

        lock (_packLock)
        {
            if (IsPackRunningUnlocked())
                return (false, "Pack already in progress.", "rejected");

            var repoRoot = ResolveRepoRoot();
            if (repoRoot is null)
                return (false, "Heimdall:RepoRoot is not set or scripts\\Pack-WorkstationCollector.cmd was not found.", "rejected");

            var cmd = Path.Combine(repoRoot, "scripts", "Pack-WorkstationCollector.cmd");
            if (!File.Exists(cmd))
                return (false, "Pack script not found: " + cmd, "rejected");

            var psi = new ProcessStartInfo
            {
                FileName = "cmd.exe",
                Arguments = $"/c \"\"{cmd}\"\"",
                WorkingDirectory = Path.Combine(repoRoot, "scripts"),
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };
            // Match Launch Control: never pause under redirected/non-interactive API spawn.
            psi.Environment["HEIMDALL_PACK_FROM_API"] = "1";
            psi.Environment["HEIMDALL_NOPAUSE"] = "1";
            // Pack from UI must always resolve N+1 — never honor a stale machine-wide ForceVersion pin.
            psi.Environment.Remove("HEIMDALL_CLIENT_PRODUCT_VERSION");

            // Floor for Resolve-ClientPackVersion: next = max(csproj, last pack, published) + 1.
            // Bump is independent of source fingerprint / Ready (Ready only unlocks Deploy).
            try
            {
                using var scope = _scopeFactory.CreateScope();
                var published = scope.ServiceProvider.GetRequiredService<PublishedVersionService>();
                var info = published.GetAsync().GetAwaiter().GetResult();
                if (!string.IsNullOrWhiteSpace(info.Version))
                    psi.Environment["HEIMDALL_PUBLISHED_CLIENT_VERSION"] = info.Version.Trim();
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Could not read published client version before pack (bump will use csproj/VERSION.json only)");
            }

            try
            {
                var logDir = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
                    "Heimdall", "logs");
                Directory.CreateDirectory(logDir);
                var logPath = Path.Combine(logDir, $"pack-{DateTime.UtcNow:yyyyMMdd-HHmmss}.log");

                _packProcess = Process.Start(psi);
                if (_packProcess is null)
                    return (false, "Failed to start pack process.", "rejected");

                _packStartedUtc = DateTimeOffset.UtcNow;
                _packWatchActive = true;
                _packCancelRequested = false;
                _packStage = $"1/{PackStageTotal}";
                _packStageLabel = "preparing";
                _currentPackLogPath = logPath;
                var proc = _packProcess;
                var startedUtc = _packStartedUtc.Value;

                _ = Task.Run(() => WatchPackProcessAsync(proc, repoRoot, logPath, startedUtc));

                OpsFileLog.Write("PackClient", $"started=true; force={force}; log={logPath}");
                return (true, "Pack started — full rebuild + version bump.", "started");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed starting pack");
                ClearPackProcessUnlocked();
                OpsFileLog.Write("PackClient", $"started=false; error={ex.Message}");
                return (false, ex.Message, "rejected");
            }
        }
    }

    /// <summary>Kill in-flight pack (same tree-kill as timeout). Safe no-op if nothing packing.</summary>
    public (bool Cancelled, string Message) TryCancelPack()
    {
        Process? proc;
        string? logPath;
        lock (_packLock)
        {
            if (!IsPackRunningUnlocked())
                return (false, "No pack in progress.");

            _packCancelRequested = true;
            proc = _packProcess;
            logPath = _currentPackLogPath ?? _lastPackLogPath;
        }

        if (proc is not null)
        {
            _logger.LogWarning("Client pack cancel requested — killing process tree (pid={Pid})", proc.Id);
            TryKillProcessTree(proc);
        }

        // Watcher records the cancelled result when the process exits; if watcher already gone, record here.
        lock (_packLock)
        {
            if (_packWatchActive)
                return (true, "Pack cancel requested — stopping…");

            _lastPackExitCode = -2;
            _lastPackMessage = "Pack cancelled by user."
                + (string.IsNullOrWhiteSpace(logPath) ? "" : $" Log: {logPath}");
            _lastPackLogPath = logPath;
            _lastPackFinishedUtc = DateTimeOffset.UtcNow;
            ClearPackProcessUnlocked();
        }

        _logger.LogWarning("Client pack cancelled by user");
        OpsFileLog.Write("PackCancel", "cancelled=true");
        return (true, "Pack cancelled.");
    }

    private async Task WatchPackProcessAsync(Process proc, string repoRoot, string logPath, DateTimeOffset startedUtc)
    {
        var stdoutSb = new StringBuilder();
        var stderrSb = new StringBuilder();
        var logLock = new object();
        StreamWriter? logWriter = null;

        try
        {
            logWriter = new StreamWriter(new FileStream(logPath, FileMode.Create, FileAccess.Write, FileShare.Read), Encoding.UTF8)
            {
                AutoFlush = true
            };
            await logWriter.WriteLineAsync($"# Heimdall client pack started {startedUtc:O}");
            await logWriter.WriteLineAsync($"# timeout={PackTimeout.TotalMinutes:0}m log={logPath}");

            void WriteLogLine(string line)
            {
                lock (logLock)
                {
                    try { logWriter.WriteLine(line); }
                    catch { /* ignore */ }
                }
            }

            void HandleOutputLine(string line)
            {
                TryParseAndSetPackStage(line);
                lock (stdoutSb) stdoutSb.AppendLine(line);
                WriteLogLine(line);
            }

            void OnOutput(object sender, DataReceivedEventArgs e)
            {
                if (e.Data is null) return;
                HandleOutputLine(e.Data);
            }

            void OnError(object sender, DataReceivedEventArgs e)
            {
                if (e.Data is null) return;
                TryParseAndSetPackStage(e.Data);
                lock (stderrSb) stderrSb.AppendLine(e.Data);
                WriteLogLine("[stderr] " + e.Data);
            }

            // Read stdout and stderr in parallel via async line events — never sequential ReadToEndAsync
            // (that deadlocks when either pipe buffer fills while the other is unread).
            proc.OutputDataReceived += OnOutput;
            proc.ErrorDataReceived += OnError;
            proc.BeginOutputReadLine();
            proc.BeginErrorReadLine();

            using var cts = new CancellationTokenSource(PackTimeout);
            var timedOut = false;
            try
            {
                await proc.WaitForExitAsync(cts.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                timedOut = true;
                _logger.LogError(
                    "Client pack timed out after {Minutes} minutes — killing process tree (pid={Pid})",
                    PackTimeout.TotalMinutes, proc.Id);
                TryKillProcessTree(proc);
                try
                {
                    // Give pipes a moment to drain after kill.
                    await proc.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(15)).ConfigureAwait(false);
                }
                catch
                {
                    /* ignore */
                }
            }

            // Ensure async output handlers finish after exit (documented WaitForExit pattern).
            try { proc.WaitForExit(5000); } catch { /* ignore */ }

            string stdout;
            string stderr;
            lock (stdoutSb) stdout = stdoutSb.ToString();
            lock (stderrSb) stderr = stderrSb.ToString();
            var combinedTail = TruncateTail(CombineStreams(stdout, stderr), LogTailMaxChars);

            bool cancelled;
            lock (_packLock) cancelled = _packCancelRequested;

            if (cancelled)
            {
                var msg = $"Pack cancelled by user. See log: {logPath}";
                WriteLogLine("# CANCELLED by user — process killed");
                RecordPackResult(exitCode: -2, success: false, message: msg, logTail: combinedTail, logPath: logPath);
                return;
            }

            if (timedOut)
            {
                var msg =
                    $"Client pack timed out after {PackTimeout.TotalMinutes:0} minutes. " +
                    $"Process tree killed. See log: {logPath}";
                WriteLogLine($"# TIMEOUT after {PackTimeout.TotalMinutes:0}m — process killed");

                RecordPackResult(exitCode: -1, success: false, message: msg, logTail: combinedTail, logPath: logPath);
                return;
            }

            var exitCode = proc.ExitCode;
            _logger.LogInformation(
                "Client pack finished exit={Code}. stdout length={OutLen} stderr length={ErrLen} log={Log}",
                exitCode, stdout.Length, stderr.Length, logPath);

            WriteLogLine($"# exit={exitCode} finished={DateTimeOffset.UtcNow:O}");

            if (exitCode == 0)
            {
                InvalidateZipCache();
                var packFolder = ResolvePackFolder(repoRoot);
                var packFp = ClientPackFingerprint.TryReadSourceFingerprintFromVersionJson(packFolder);
                var manifestOk = File.Exists(Path.Combine(packFolder, "MANIFEST.sha256"));
                if (packFp is null || !manifestOk)
                {
                    var incomplete =
                        "Pack process exited 0 but pack is incomplete (missing "
                        + (packFp is null ? "sourceFingerprint" : "")
                        + (packFp is null && !manifestOk ? " and " : "")
                        + (!manifestOk ? "MANIFEST.sha256" : "")
                        + $"). See log: {logPath}";
                    RecordPackResult(exitCode, success: false, message: incomplete, logTail: combinedTail, logPath: logPath);
                    return;
                }

                var ver = ClientPackFingerprint.TryReadProductVersion(packFolder);
                if (!string.IsNullOrWhiteSpace(ver))
                {
                    try
                    {
                        await using var scope = _scopeFactory.CreateAsyncScope();
                        var published = scope.ServiceProvider.GetRequiredService<PublishedVersionService>();
                        await published.SetAsync(ver, "Pack from Client Version page");
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(ex, "Failed to publish client version after pack");
                    }
                }

                RecordPackResult(
                    exitCode,
                    success: true,
                    message: $"Last pack exit 0"
                        + (string.IsNullOrWhiteSpace(ver) ? "" : $" · v{ver}")
                        + $". Log: {logPath}",
                    logTail: combinedTail,
                    logPath: logPath);
            }
            else
            {
                var msg = $"Client pack failed (exit {exitCode}). See log: {logPath}";
                RecordPackResult(exitCode, success: false, message: msg, logTail: combinedTail, logPath: logPath);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Pack process watcher failed");
            RecordPackResult(
                exitCode: -1,
                success: false,
                message: "Pack watcher failed: " + ex.Message,
                logTail: TruncateTail(ex.ToString(), LogTailMaxChars),
                logPath: logPath);
        }
        finally
        {
            try { logWriter?.Dispose(); } catch { /* ignore */ }
            lock (_packLock)
            {
                if (ReferenceEquals(_packProcess, proc))
                    _packProcess = null;
                _packWatchActive = false;
                _packStartedUtc = null;
                _packCancelRequested = false;
                _packStage = null;
                _packStageLabel = null;
                _currentPackLogPath = null;
            }

            try { proc.Dispose(); } catch { /* ignore */ }
        }
    }

    private void TryParseAndSetPackStage(string line)
    {
        var m = PackStageLineRegex.Match(line.Trim());
        if (!m.Success)
            return;

        var stage = $"{m.Groups[1].Value}/{m.Groups[2].Value}";
        var label = m.Groups[3].Value.Trim();
        lock (_packLock)
        {
            _packStage = stage;
            _packStageLabel = label;
        }
    }

    public (string ZipPath, string Sha256) EnsureZip(string? packFolder = null, string? fingerprint = null)
    {
        packFolder ??= ResolvePackFolder();
        fingerprint ??= ClientPackFingerprint.TryReadSourceFingerprintFromVersionJson(packFolder)
                        ?? ClientPackFingerprint.HashFile(Path.Combine(packFolder, "payload", "Heimdall.Agent.exe"));

        if (_cachedZipPath is not null
            && _cachedZipSha is not null
            && string.Equals(_cachedZipFingerprint, fingerprint, StringComparison.OrdinalIgnoreCase)
            && File.Exists(_cachedZipPath))
        {
            return (_cachedZipPath, _cachedZipSha);
        }

        var repoRoot = ResolveRepoRoot();
        var zipDir = repoRoot is not null
            ? Path.Combine(repoRoot, "dist")
            : Path.GetDirectoryName(packFolder) ?? packFolder;
        Directory.CreateDirectory(zipDir);
        var zipPath = Path.Combine(zipDir, "heimdall-client-agent.zip");
        if (File.Exists(zipPath))
            File.Delete(zipPath);

        ZipFile.CreateFromDirectory(packFolder, zipPath, CompressionLevel.Optimal, includeBaseDirectory: false);
        var sha = ClientPackFingerprint.HashFile(zipPath);
        _cachedZipPath = zipPath;
        _cachedZipSha = sha;
        _cachedZipFingerprint = fingerprint;
        return (zipPath, sha);
    }

    public void InvalidateZipCache()
    {
        _cachedZipPath = null;
        _cachedZipSha = null;
        _cachedZipFingerprint = null;
    }

    private ClientPackReadiness WithLastPack(ClientPackReadiness r) =>
        r with
        {
            IsPacking = false,
            PackingElapsedSeconds = null,
            PackStage = null,
            PackStageLabel = null,
            LastPackExitCode = _lastPackExitCode,
            LastPackMessage = _lastPackMessage,
            LastPackLogTail = _lastPackLogTail,
            LastPackLogPath = _lastPackLogPath,
            LastPackFinishedUtc = _lastPackFinishedUtc
        };

    private void RecordPackResult(int exitCode, bool success, string message, string? logTail, string? logPath)
    {
        lock (_packLock)
        {
            _lastPackExitCode = exitCode;
            _lastPackMessage = message;
            _lastPackLogTail = logTail;
            _lastPackLogPath = logPath;
            _lastPackFinishedUtc = DateTimeOffset.UtcNow;
        }

        if (success)
            _logger.LogInformation("Client pack result: {Message}", message);
        else
            _logger.LogError("Client pack result: {Message}", message);
    }

    private bool IsPackRunning()
    {
        lock (_packLock)
            return IsPackRunningUnlocked();
    }

    private bool IsPackRunningUnlocked()
    {
        // Stay "Packing" until the watcher records result and clears — avoids UI flicker and double-start.
        if (_packWatchActive)
            return true;

        if (_packProcess is null)
            return false;

        try
        {
            if (!_packProcess.HasExited)
                return true;
        }
        catch
        {
            ClearPackProcessUnlocked();
            return false;
        }

        ClearPackProcessUnlocked();
        return false;
    }

    private void ClearPackProcessUnlocked()
    {
        _packProcess = null;
        _packStartedUtc = null;
        _packWatchActive = false;
        _packCancelRequested = false;
        _packStage = null;
        _packStageLabel = null;
        _currentPackLogPath = null;
    }

    private static void TryKillProcessTree(Process proc)
    {
        try
        {
            if (!proc.HasExited)
                proc.Kill(entireProcessTree: true);
        }
        catch (Exception)
        {
            try
            {
                if (!proc.HasExited)
                    proc.Kill();
            }
            catch
            {
                /* ignore */
            }
        }
    }

    private static string CombineStreams(string stdout, string stderr)
    {
        if (string.IsNullOrEmpty(stderr)) return stdout ?? "";
        if (string.IsNullOrEmpty(stdout)) return stderr;
        return stdout.TrimEnd() + "\n--- stderr ---\n" + stderr;
    }

    private static string TruncateTail(string text, int maxChars)
    {
        if (string.IsNullOrEmpty(text) || text.Length <= maxChars)
            return text ?? "";
        return "…\n" + text[(text.Length - maxChars)..];
    }

    private string? GetApiInstallNote()
    {
        var apiDir = _config["Heimdall:ApiInstallDir"]
                     ?? @"C:\Program Files\Heimdall\Api";
        if (!Directory.Exists(apiDir))
            return "API install dir not found (informational only).";

        try
        {
            var dll = Directory.EnumerateFiles(apiDir, "Heimdall.Api.dll", SearchOption.TopDirectoryOnly).FirstOrDefault()
                      ?? Directory.EnumerateFiles(apiDir, "*.dll", SearchOption.TopDirectoryOnly).FirstOrDefault();
            if (dll is null)
                return "API binaries present; fingerprint skipped.";
            var hash = ClientPackFingerprint.HashFile(dll);
            return $"API host binary {Path.GetFileName(dll)} sha256={hash[..12]}… (does not block Deploy).";
        }
        catch
        {
            return null;
        }
    }
}
