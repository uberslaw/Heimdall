// Mirrors TermServiceHelper's static-class shape. Also owns starting new runs and
// post-run verified archive offload (local scratch → UNC).

using System.Diagnostics;
using System.Runtime.Versioning;
using System.Text.Json;
using Heimdall.Shared;
using Heimdall.Shared.Contracts;
using Microsoft.Extensions.Logging;

namespace Heimdall.Agent.Collectors;

[SupportedOSPlatform("windows")]
internal static class TuflowRunHelper
{
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true };

    private static string StateDir => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "Heimdall", "tuflow-runs");

    private static string PointerFile => Path.Combine(StateDir, "current-run.json");

    private static string LauncherExePath =>
        Environment.GetEnvironmentVariable("HEIMDALL_TUFLOW_LAUNCHER_EXE")
        ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Heimdall", "Agent", "TuflowLauncher", "TuflowLauncher.exe");

    public static void TryStartIfRequested(TuflowStartRequestDto? request, ILogger logger)
    {
        if (request is null)
            return;

        if (TuflowLaunchPath.ValidateLaunch(
                request.LaunchMode,
                request.ExePath,
                request.TcfPath,
                request.CmdPath,
                request.WorkingDirectory,
                request.ResultsFolder) is { } pathErr)
        {
            logger.LogWarning("TUFLOW start {RunId} path looks unusable from a service session: {Error}", request.RunId, pathErr);
        }

        var current = ReadPointer();
        if (current is not null)
        {
            if (string.Equals(current.RunId, request.RunId, StringComparison.OrdinalIgnoreCase))
                return;

            logger.LogWarning(
                "Ignoring TUFLOW start request {NewRunId}: already tracking {CurrentRunId} on this agent.",
                request.RunId, current.RunId);
            return;
        }

        if (!File.Exists(LauncherExePath))
        {
            logger.LogError("Cannot start TUFLOW run {RunId}: launcher not found at {Path}", request.RunId, LauncherExePath);
            return;
        }

        try
        {
            Directory.CreateDirectory(StateDir);
            var runDir = Path.Combine(StateDir, request.RunId);
            Directory.CreateDirectory(runDir);

            var isCmdMode = string.Equals(request.LaunchMode, TuflowLaunchModes.Cmd, StringComparison.OrdinalIgnoreCase)
                || !string.IsNullOrWhiteSpace(request.CmdPath);

            string? scratchDrive = null;
            double? scratchFreeGb = null;
            var workingDirectory = !string.IsNullOrWhiteSpace(request.WorkingDirectory)
                ? request.WorkingDirectory
                : null;

            if (string.IsNullOrWhiteSpace(workingDirectory) && request.UseLocalScratch)
            {
                var pick = TuflowScratchPicker.TryPick(
                    request.RunId,
                    request.ScratchMinFreeGb > 0 ? request.ScratchMinFreeGb : 50,
                    request.AllowScratchOnC);
                if (pick is not null)
                {
                    workingDirectory = pick.FolderPath;
                    scratchDrive = pick.Drive;
                    scratchFreeGb = pick.FreeGb;
                    logger.LogWarning(
                        "TUFLOW scratch for {RunId}: {Drive} ({Free:0.0} GB free) → {Folder}",
                        request.RunId, pick.Drive, pick.FreeGb, pick.FolderPath);
                }
                else
                {
                    logger.LogWarning(
                        "TUFLOW scratch requested for {RunId} but no suitable local drive found; falling back to tcf/cmd folder.",
                        request.RunId);
                }
            }

            workingDirectory ??= isCmdMode
                ? Path.GetDirectoryName(request.CmdPath) ?? runDir
                : Path.GetDirectoryName(request.TcfPath) ?? runDir;

            var archivePath = string.IsNullOrWhiteSpace(request.ArchiveShare)
                ? null
                : CombineRunArchive(request.ArchiveShare!, request.RunName, request.RunId);

            var runSpecJson = JsonSerializer.Serialize(new
            {
                runId = request.RunId,
                runName = request.RunName,
                launchMode = isCmdMode ? TuflowLaunchModes.Cmd : TuflowLaunchModes.ExeTcf,
                exePath = request.ExePath ?? "",
                tcfPath = request.TcfPath ?? "",
                cmdPath = request.CmdPath,
                workingDirectory,
                scenarios = request.Scenarios,
                events = request.Events,
                runDir,
                resultsFolder = request.ResultsFolder ?? workingDirectory
            });
            var runSpecPath = Path.Combine(runDir, "run-spec.json");
            File.WriteAllText(runSpecPath, runSpecJson);

            var psi = new ProcessStartInfo
            {
                FileName = LauncherExePath,
                Arguments = $"\"{runSpecPath}\"",
                UseShellExecute = false,
                CreateNoWindow = true,
                WorkingDirectory = runDir
            };
            Process.Start(psi);

            var offload = new TuflowResultsOffload.OffloadState
            {
                LocalResultsPath = workingDirectory,
                ArchivePath = archivePath,
                ScratchDrive = scratchDrive,
                AutoCleanAfterVerify = request.AutoCleanAfterVerify,
                TransferState = string.IsNullOrWhiteSpace(archivePath)
                    ? TuflowTransferStates.Skipped
                    : TuflowTransferStates.Pending,
                TransferDetail = string.IsNullOrWhiteSpace(archivePath)
                    ? "No archive share configured."
                    : "Waiting for run to finish before offload."
            };

            WritePointer(new RunPointer(
                request.RunId,
                runDir,
                scratchDrive,
                scratchFreeGb,
                workingDirectory,
                archivePath,
                request.AutoCleanAfterVerify,
                TuflowResultsOffload.Serialize(offload)));

            logger.LogWarning(
                "Started TUFLOW run {RunId} via launcher: {Target}",
                request.RunId,
                isCmdMode ? request.CmdPath : request.TcfPath);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to start TUFLOW run {RunId}", request.RunId);
        }
    }

    public static bool TryExecuteCommand(string command, ILogger logger, out string detail)
    {
        if (string.Equals(command, RemoteMachineCommands.TuflowClearRun, StringComparison.OrdinalIgnoreCase))
        {
            var pointer = ReadPointer();
            if (pointer is null)
            {
                detail = "No TUFLOW run pointer to clear";
                return true;
            }

            try
            {
                // Best-effort stop signal if a run dir still exists, then drop the pointer so heartbeats
                // stop resurrecting Starting after a dashboard Force clear.
                try
                {
                    Directory.CreateDirectory(pointer.RunDir);
                    File.WriteAllText(Path.Combine(pointer.RunDir, "stop.request"), DateTimeOffset.UtcNow.ToString("O"));
                }
                catch
                {
                    /* pointer clear still proceeds */
                }

                ClearPointer();
                detail = $"Cleared TUFLOW run pointer {pointer.RunId}";
                logger.LogWarning("Cleared local TUFLOW run pointer {RunId} (TuflowClearRun)", pointer.RunId);
                return true;
            }
            catch (Exception ex)
            {
                detail = ex.Message;
                logger.LogError(ex, "Failed to clear TUFLOW run pointer {RunId}", pointer.RunId);
                return false;
            }
        }

        if (!string.Equals(command, RemoteMachineCommands.TuflowStopGraceful, StringComparison.OrdinalIgnoreCase))
        {
            detail = $"Unknown command: {command}";
            return false;
        }

        var current = ReadPointer();
        if (current is null)
        {
            detail = "No TUFLOW run is currently tracked on this agent";
            return false;
        }

        try
        {
            File.WriteAllText(Path.Combine(current.RunDir, "stop.request"), DateTimeOffset.UtcNow.ToString("O"));
            detail = $"Stop signal written for run {current.RunId}";
            logger.LogWarning("Wrote graceful stop.request for TUFLOW run {RunId}", current.RunId);
            return true;
        }
        catch (Exception ex)
        {
            detail = ex.Message;
            logger.LogError(ex, "Failed to write stop.request for TUFLOW run {RunId}", current.RunId);
            return false;
        }
    }

    public static TuflowRunStatusDto? ReadCurrentStatus(ILogger logger)
    {
        var current = ReadPointer();
        if (current is null)
            return null;

        var statusPath = Path.Combine(current.RunDir, "status.json");
        if (!File.Exists(statusPath))
        {
            return Enrich(new TuflowRunStatusDto
            {
                RunId = current.RunId,
                State = TuflowRunStates.Starting,
                UpdatedUtc = DateTimeOffset.UtcNow
            }, current, offload: null);
        }

        try
        {
            var raw = File.ReadAllText(statusPath);
            var dto = JsonSerializer.Deserialize<TuflowRunStatusDto>(raw, JsonOptions);
            if (dto is null)
                return null;

            var offload = TuflowResultsOffload.Deserialize(current.OffloadJson)
                          ?? new TuflowResultsOffload.OffloadState
                          {
                              LocalResultsPath = current.LocalResultsPath,
                              ArchivePath = current.ArchivePath,
                              ScratchDrive = current.ScratchDrive,
                              AutoCleanAfterVerify = current.AutoCleanAfterVerify,
                              TransferState = string.IsNullOrWhiteSpace(current.ArchivePath)
                                  ? TuflowTransferStates.Skipped
                                  : TuflowTransferStates.Pending
                          };

            if (dto.State is TuflowRunStates.Completed or TuflowRunStates.Stopped)
            {
                var done = TuflowResultsOffload.Tick(offload, logger);
                current = current with { OffloadJson = TuflowResultsOffload.Serialize(offload) };
                WritePointer(current);
                if (done)
                    ClearPointer();
            }
            else if (dto.State is TuflowRunStates.Failed)
            {
                offload.TransferState = TuflowTransferStates.Skipped;
                offload.TransferDetail = "Run failed — archive offload skipped; local left in place.";
                ClearPointer();
            }

            return Enrich(dto, current, offload);
        }
        catch (Exception)
        {
            return Enrich(new TuflowRunStatusDto
            {
                RunId = current.RunId,
                State = TuflowRunStates.Running,
                Message = "status.json unreadable this cycle",
                UpdatedUtc = DateTimeOffset.UtcNow
            }, current, null);
        }
    }

    private static TuflowRunStatusDto Enrich(
        TuflowRunStatusDto dto,
        RunPointer current,
        TuflowResultsOffload.OffloadState? offload)
    {
        return new TuflowRunStatusDto
        {
            RunId = dto.RunId,
            RunName = dto.RunName,
            State = dto.State,
            ProcessId = dto.ProcessId,
            TcfPath = dto.TcfPath,
            CmdPath = dto.CmdPath,
            StartedUtc = dto.StartedUtc,
            StopRequestedUtc = dto.StopRequestedUtc,
            LastCheckpointUtc = dto.LastCheckpointUtc,
            LastCheckpointFile = dto.LastCheckpointFile,
            ExitCode = dto.ExitCode,
            Message = dto.Message,
            UpdatedUtc = dto.UpdatedUtc,
            PercentComplete = dto.PercentComplete,
            SimulationTimeHours = dto.SimulationTimeHours,
            SimulationEndTimeHours = dto.SimulationEndTimeHours,
            ClockTimeRemainingHours = dto.ClockTimeRemainingHours,
            WarningCount = dto.WarningCount,
            MassErrorPercent = dto.MassErrorPercent,
            ErrorSummary = dto.ErrorSummary,
            ScratchDrive = offload?.ScratchDrive ?? current.ScratchDrive,
            LocalResultsPath = offload?.LocalResultsPath ?? current.LocalResultsPath,
            ArchivePath = offload?.ArchivePath ?? current.ArchivePath,
            TransferState = offload?.TransferState,
            TransferDetail = offload?.TransferDetail,
            TransferLocalFileCount = offload?.LocalFileCount,
            TransferDestFileCount = offload?.DestFileCount,
            TransferLocalBytes = offload?.LocalBytes,
            TransferDestBytes = offload?.DestBytes
        };
    }

    private static string CombineRunArchive(string archiveRootOrTemplate, string? runName, string runId)
    {
        var root = archiveRootOrTemplate.Trim().TrimEnd('\\', '/');
        var folder = TuflowScratchSettingsSanitize(runName, runId);
        return $"{root}\\{folder}";
    }

    private static string TuflowScratchSettingsSanitize(string? runName, string runId)
    {
        var s = string.IsNullOrWhiteSpace(runName) ? runId : runName.Trim();
        foreach (var c in Path.GetInvalidFileNameChars())
            s = s.Replace(c, '_');
        return s.Length == 0 ? runId : s;
    }

    private static RunPointer? ReadPointer()
    {
        if (!File.Exists(PointerFile))
            return null;
        try
        {
            return JsonSerializer.Deserialize<RunPointer>(File.ReadAllText(PointerFile), JsonOptions);
        }
        catch
        {
            return null;
        }
    }

    private static void WritePointer(RunPointer pointer)
    {
        Directory.CreateDirectory(StateDir);
        File.WriteAllText(PointerFile, JsonSerializer.Serialize(pointer));
    }

    private static void ClearPointer()
    {
        try
        {
            if (File.Exists(PointerFile))
                File.Delete(PointerFile);
        }
        catch
        {
        }
    }

    private sealed record RunPointer(
        string RunId,
        string RunDir,
        string? ScratchDrive = null,
        double? ScratchFreeGb = null,
        string? LocalResultsPath = null,
        string? ArchivePath = null,
        bool AutoCleanAfterVerify = false,
        string? OffloadJson = null);
}
