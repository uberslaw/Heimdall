// NEW FILE — drop in as-is at:
//   Heimdall.Agent/Collectors/TuflowRunHelper.cs
//
// Mirrors TermServiceHelper's static-class, TryExecuteCommand(command, logger, out detail) shape (see
// that file) so Worker.ProcessPendingCommands can try both helpers for an unrecognised command without
// a bigger dispatch rewrite. Also owns starting new runs (TryStartIfRequested, called from the
// config-refresh block, not from ProcessPendingCommands, since a start needs a payload that
// PendingCommands' bare string list can't carry — see AgentConfigDto.PendingTuflowStart instead).
//
// State: this agent tracks at most one TUFLOW run at a time via a small pointer file
// (%ProgramData%\Heimdall\tuflow-runs\current-run.json) pointing at that run's working directory,
// where TuflowLauncher.exe writes run-spec.json / status.json / stop.request. This matches the
// modelling-workstation reality (one machine, one TUFLOW job) and the API-side one-run-per-machine
// assumption in TuflowRunService.

using System.Diagnostics;
using System.Runtime.Versioning;
using System.Text.Json;
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

    /// <summary>
    /// Path to TuflowLauncher.exe on this machine. Override via HEIMDALL_TUFLOW_LAUNCHER_EXE.
    /// Default matches the agent install layout under Program Files.
    /// </summary>
    private static string LauncherExePath =>
        Environment.GetEnvironmentVariable("HEIMDALL_TUFLOW_LAUNCHER_EXE")
        ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Heimdall", "Agent", "TuflowLauncher", "TuflowLauncher.exe");

    /// <summary>Called once per config-refresh cycle from Worker.ExecuteAsync. No-ops unless a start is
    /// requested and no run is currently tracked (or the tracked run is the same RunId, already started).</summary>
    public static void TryStartIfRequested(TuflowStartRequestDto? request, ILogger logger)
    {
        if (request is null)
            return;

        var current = ReadPointer();
        if (current is not null)
        {
            if (string.Equals(current.RunId, request.RunId, StringComparison.OrdinalIgnoreCase))
                return; // already started this exact run — config refresh just saw it again before the ack landed

            logger.LogWarning(
                "Ignoring TUFLOW start request {NewRunId}: already tracking {CurrentRunId} on this agent. " +
                "The Api side should prevent double-queueing (TuflowRunService.QueueStartAsync checks " +
                "IsActiveRunState first) — seeing this warning means that check was bypassed or stale.",
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

            var workingDirectory = !string.IsNullOrWhiteSpace(request.WorkingDirectory)
                ? request.WorkingDirectory
                : isCmdMode
                    ? Path.GetDirectoryName(request.CmdPath) ?? runDir
                    : Path.GetDirectoryName(request.TcfPath) ?? runDir;

            // Field names here are camelCase to match TuflowLauncher's LauncherJsonContext
            // (JsonKnownNamingPolicy.CamelCase) — see TuflowLauncher/RunModels.cs. Using an anonymous
            // object rather than a shared RunSpec type since RunSpec lives in the launcher project,
            // which the agent doesn't reference (kept as a separate deployable exe, not a shared DLL).
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
                resultsFolder = request.ResultsFolder
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

            WritePointer(new RunPointer(request.RunId, runDir));
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

    /// <summary>Same signature as TermServiceHelper.TryExecuteCommand so Worker can try both helpers uniformly.</summary>
    public static bool TryExecuteCommand(string command, ILogger logger, out string detail)
    {
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
            // TuflowLauncher polls for this file and sends CTRL_BREAK_EVENT to the TUFLOW process group
            // when it appears — see TuflowLauncher/Program.cs. This does not touch the launcher or
            // TUFLOW process directly; the launcher owns the actual stop signal.
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

    /// <summary>
    /// Reads TuflowLauncher's status.json (if any) and passes it straight through as the DTO included in
    /// each heartbeat. Called from Worker.FlushAsync, same cadence as TermServiceHelper.GetStatus().
    /// </summary>
    public static TuflowRunStatusDto? ReadCurrentStatus()
    {
        var current = ReadPointer();
        if (current is null)
            return null;

        var statusPath = Path.Combine(current.RunDir, "status.json");
        if (!File.Exists(statusPath))
        {
            // run-spec.json was written and the launcher was spawned, but it hasn't written its first
            // status.json yet (process still starting up) — report a synthetic Starting status rather
            // than nothing, so the Api side doesn't show a stale "no run" state in between.
            return new TuflowRunStatusDto
            {
                RunId = current.RunId,
                State = TuflowRunStates.Starting,
                UpdatedUtc = DateTimeOffset.UtcNow
            };
        }

        try
        {
            var raw = File.ReadAllText(statusPath);
            // TuflowLauncher's RunStatus.State is already one of the TuflowRunStates.* strings (see
            // RunStateWire.ToWireState in TuflowLauncher/RunModels.cs) — deserializing straight into
            // TuflowRunStatusDto works because both types have the same shape; no re-mapping needed.
            var dto = JsonSerializer.Deserialize<TuflowRunStatusDto>(raw, JsonOptions);
            if (dto is null)
                return null;

            // Terminal states release the pointer so a future start isn't blocked by a finished run.
            // The Api side keeps the last-reported status around (TuflowRunStatusJson isn't cleared),
            // it's only this agent's "what am I actively tracking" pointer that resets.
            if (dto.State is TuflowRunStates.Stopped or TuflowRunStates.Completed or TuflowRunStates.Failed)
                ClearPointer();

            return dto;
        }
        catch (Exception)
        {
            // status.json mid-write (launcher writes via temp-file-then-copy specifically to avoid this,
            // but a transient read race is still cheaper to shrug off than to crash the agent's upload cycle).
            return new TuflowRunStatusDto
            {
                RunId = current.RunId,
                State = TuflowRunStates.Running,
                Message = "status.json unreadable this cycle",
                UpdatedUtc = DateTimeOffset.UtcNow
            };
        }
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
            // best effort — a stale pointer just means the next start attempt gets a warning and no-op
            // (see TryStartIfRequested) until this is cleaned up manually or the file becomes deletable.
        }
    }

    private sealed record RunPointer(string RunId, string RunDir);
}
