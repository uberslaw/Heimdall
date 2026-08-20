using System.Diagnostics;
using System.Runtime.Versioning;
using System.Text.Json;
using Heimdall.Shared.Contracts;
using Microsoft.Extensions.Logging;

namespace Heimdall.Agent.Collectors;

/// <summary>
/// Post-run verified robocopy of local scratch results to a UNC archive share.
/// Cleanup runs only after verify succeeds and AutoCleanAfterVerify is set.
/// </summary>
[SupportedOSPlatform("windows")]
internal static class TuflowResultsOffload
{
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true };

    public sealed class OffloadState
    {
        public string? LocalResultsPath { get; set; }
        public string? ArchivePath { get; set; }
        public string? ScratchDrive { get; set; }
        public bool AutoCleanAfterVerify { get; set; }
        public string TransferState { get; set; } = TuflowTransferStates.Pending;
        public string? TransferDetail { get; set; }
        public int? RobocopyPid { get; set; }
        public string? RobocopyLogPath { get; set; }
        public int? LocalFileCount { get; set; }
        public int? DestFileCount { get; set; }
        public long? LocalBytes { get; set; }
        public long? DestBytes { get; set; }
    }

    /// <summary>
    /// Advances offload for a finished run. Returns true when the agent may clear its run pointer
    /// (transfer finished Verified/Failed/Skipped, or no archive configured).
    /// </summary>
    public static bool Tick(OffloadState state, ILogger logger)
    {
        if (string.IsNullOrWhiteSpace(state.ArchivePath) || string.IsNullOrWhiteSpace(state.LocalResultsPath))
        {
            state.TransferState = TuflowTransferStates.Skipped;
            state.TransferDetail = "No archive share or local results path — offload skipped.";
            return true;
        }

        if (!Directory.Exists(state.LocalResultsPath))
        {
            state.TransferState = TuflowTransferStates.Failed;
            state.TransferDetail = $"Local results path missing: {state.LocalResultsPath}";
            return true;
        }

        if (state.TransferState is TuflowTransferStates.Verified or TuflowTransferStates.Failed or TuflowTransferStates.Skipped)
            return true;

        if (state.TransferState == TuflowTransferStates.Copying && state.RobocopyPid is int pid)
        {
            try
            {
                var proc = Process.GetProcessById(pid);
                if (!proc.HasExited)
                {
                    state.TransferDetail = $"Robocopy in progress (PID {pid})…";
                    return false;
                }

                var exit = proc.ExitCode;
                return FinishAfterRobocopy(state, exit, logger);
            }
            catch (ArgumentException)
            {
                // Process already gone — treat as finished with unknown code; verify by counts.
                return FinishAfterRobocopy(state, exitCode: 0, logger);
            }
        }

        // Start robocopy
        try
        {
            Directory.CreateDirectory(state.ArchivePath);
            var logDir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
                "Heimdall", "logs");
            Directory.CreateDirectory(logDir);
            var logPath = Path.Combine(logDir, $"tuflow-offload-{DateTime.UtcNow:yyyyMMdd-HHmmss}.log");
            state.RobocopyLogPath = logPath;

            var psi = new ProcessStartInfo
            {
                FileName = "robocopy.exe",
                Arguments =
                    $"\"{state.LocalResultsPath}\" \"{state.ArchivePath}\" /E /COPY:DAT /R:2 /W:5 /NFL /NDL /NP /LOG:\"{logPath}\"",
                UseShellExecute = false,
                CreateNoWindow = true
            };
            var p = Process.Start(psi);
            if (p is null)
            {
                state.TransferState = TuflowTransferStates.Failed;
                state.TransferDetail = "Failed to start robocopy.";
                return true;
            }

            state.RobocopyPid = p.Id;
            state.TransferState = TuflowTransferStates.Copying;
            state.TransferDetail = $"Robocopy started (PID {p.Id}) → {state.ArchivePath}";
            logger.LogWarning(
                "TUFLOW offload robocopy started: {Local} → {Archive} (PID {Pid})",
                state.LocalResultsPath, state.ArchivePath, p.Id);
            return false;
        }
        catch (Exception ex)
        {
            state.TransferState = TuflowTransferStates.Failed;
            state.TransferDetail = $"Robocopy start failed: {ex.Message}";
            logger.LogError(ex, "TUFLOW offload start failed");
            return true;
        }
    }

    private static bool FinishAfterRobocopy(OffloadState state, int exitCode, ILogger logger)
    {
        // Robocopy 0–7 = success (files copied / extras / mismatches within copy semantics).
        if (exitCode >= 8)
        {
            state.TransferState = TuflowTransferStates.Failed;
            state.TransferDetail = $"Robocopy failed with exit {exitCode}. Log: {state.RobocopyLogPath}";
            logger.LogWarning("TUFLOW offload robocopy exit {Code}", exitCode);
            return true;
        }

        var localStats = CountTree(state.LocalResultsPath!);
        var destStats = CountTree(state.ArchivePath!);
        state.LocalFileCount = localStats.Files;
        state.DestFileCount = destStats.Files;
        state.LocalBytes = localStats.Bytes;
        state.DestBytes = destStats.Bytes;

        if (localStats.Files != destStats.Files || localStats.Bytes != destStats.Bytes)
        {
            state.TransferState = TuflowTransferStates.Failed;
            state.TransferDetail =
                $"Verify failed: local {localStats.Files} files / {localStats.Bytes} bytes vs " +
                $"dest {destStats.Files} files / {destStats.Bytes} bytes. Local NOT deleted.";
            logger.LogWarning("TUFLOW offload verify failed: {Detail}", state.TransferDetail);
            return true;
        }

        state.TransferState = TuflowTransferStates.Verified;
        state.TransferDetail =
            $"Verified {localStats.Files} files / {localStats.Bytes} bytes → {state.ArchivePath}";

        if (state.AutoCleanAfterVerify)
        {
            try
            {
                Directory.Delete(state.LocalResultsPath!, recursive: true);
                state.TransferDetail += " Local scratch deleted after verify.";
                logger.LogWarning("TUFLOW offload cleaned local scratch {Path}", state.LocalResultsPath);
            }
            catch (Exception ex)
            {
                state.TransferDetail += $" Local cleanup failed (left in place): {ex.Message}";
                logger.LogWarning(ex, "TUFLOW offload local cleanup failed");
            }
        }
        else
        {
            state.TransferDetail += " Local scratch left in place (AutoCleanAfterVerify off).";
        }

        return true;
    }

    private static (int Files, long Bytes) CountTree(string root)
    {
        if (!Directory.Exists(root))
            return (0, 0);
        var files = 0;
        long bytes = 0;
        foreach (var f in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
        {
            files++;
            try { bytes += new FileInfo(f).Length; }
            catch { /* locked/transient */ }
        }

        return (files, bytes);
    }

    public static string Serialize(OffloadState state) => JsonSerializer.Serialize(state);

    public static OffloadState? Deserialize(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return null;
        try { return JsonSerializer.Deserialize<OffloadState>(json, JsonOptions); }
        catch { return null; }
    }
}
