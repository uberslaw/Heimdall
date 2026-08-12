using System.Text.Json.Serialization;

namespace TuflowLauncher;

/// <summary>
/// Written by Heimdall.Agent's TuflowRunHelper as "run-spec.json" in the run directory before the
/// launcher starts, and read by this process on startup (path passed as argv[0]).
/// </summary>
public sealed class RunSpec
{
    public required string RunId { get; init; }
    /// <summary>Human-facing label — see TuflowStartRequestDto.RunName in Heimdall.Shared. Carried through
    /// to every status.json write (RunStatus.RunName) purely so the Api side never has to merge it back in
    /// when it wholesale-replaces Machine.TuflowRunStatusJson from a heartbeat.</summary>
    public required string RunName { get; init; }
    /// <summary>ExeTcf (default) or Cmd — see Heimdall.Shared.Contracts.TuflowLaunchModes.</summary>
    public string LaunchMode { get; init; } = "ExeTcf";
    public string ExePath { get; init; } = "";
    public string TcfPath { get; init; } = "";
    /// <summary>Absolute/UNC .cmd/.bat path when LaunchMode is Cmd.</summary>
    public string? CmdPath { get; init; }
    public required string WorkingDirectory { get; init; }
    public List<string> Scenarios { get; init; } = [];
    public List<string> Events { get; init; } = [];
    public required string RunDir { get; init; }
    /// <summary>
    /// Optional explicit results/trf folder if Heimdall's caller knows it (e.g. from Output Folder ==
    /// in the .tcf). When null the launcher falls back to searching WorkingDirectory for a "trf" or
    /// "erf" subfolder — see FindCheckpointFolder in Program.cs. Verify this against your actual
    /// TCF's Output Folder convention before relying on checkpoint detection.
    /// </summary>
    public string? ResultsFolder { get; init; }
    /// <summary>
    /// Optional explicit log folder if known (e.g. from Log Folder == in the .tcf). When null the
    /// launcher falls back to searching WorkingDirectory for *.tsf/*.tlf files or a "log" subfolder —
    /// see FindLogFolder in Program.cs. Per the manual, TUFLOW's default (no Log Folder command) is the
    /// same folder the .tcf runs from, so WorkingDirectory itself is usually right without any hint.
    /// </summary>
    public string? LogFolder { get; init; }
}

/// <summary>Launcher's internal state machine.</summary>
public enum RunState { Starting, Running, StopRequested, Stopped, Completed, Failed }

/// <summary>
/// Maps RunState to the exact string vocabulary in Heimdall.Shared.Contracts.TuflowRunStates, so
/// status.json's "state" field can be passed through by the Agent unmodified — no separate mapping
/// table to keep in sync on that side.
/// </summary>
public static class RunStateWire
{
    public static string ToWireState(this RunState state) => state switch
    {
        RunState.Starting => "Starting",
        RunState.Running => "Running",
        RunState.StopRequested => "StopRequested",
        RunState.Stopped => "Stopped",
        RunState.Completed => "Completed",
        RunState.Failed => "Failed",
        _ => "Running"
    };
}

/// <summary>
/// Written to "status.json" in RunDir on every state change and checkpoint detection. Polled by
/// Heimdall.Agent's TuflowRunHelper.ReadCurrentStatus() once per config-refresh/heartbeat cycle.
/// </summary>
public sealed class RunStatus
{
    public required string RunId { get; init; }
    public string? RunName { get; init; }
    public required string State { get; init; }
    public int? ProcessId { get; init; }
    public string? TcfPath { get; init; }
    public string? CmdPath { get; init; }
    public DateTimeOffset? StartedUtc { get; init; }
    public DateTimeOffset? StopRequestedUtc { get; init; }
    /// <summary>Last time the launcher observed a new/updated .trf or .erf restart file being written.</summary>
    public DateTimeOffset? LastCheckpointUtc { get; init; }
    public string? LastCheckpointFile { get; init; }
    public int? ExitCode { get; init; }
    public string? Message { get; init; }
    public DateTimeOffset UpdatedUtc { get; init; }

    // Mirrors Heimdall.Shared.Contracts.TuflowRunStatusDto field-for-field (same names) — see that type
    // for what each one means and where it's grounded in the manual. Kept as two separate classes
    // (rather than sharing one type across the launcher and Heimdall.Agent) because the launcher is a
    // standalone project with no reference to Heimdall.Shared — see README "Why two DTOs".
    public double? PercentComplete { get; init; }
    public double? SimulationTimeHours { get; init; }
    public double? SimulationEndTimeHours { get; init; }
    public double? ClockTimeRemainingHours { get; init; }
    public int? WarningCount { get; init; }
    public double? MassErrorPercent { get; init; }
    public string? ErrorSummary { get; init; }
}

// camelCase on the wire (runId, tcfPath, ...) — this is load-bearing: Heimdall.Agent writes run-spec.json
// with an anonymous object using camelCase names and reads status.json case-insensitively, but keeping
// both sides on the same explicit policy avoids relying on that case-insensitive fallback.
[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase, WriteIndented = true)]
[JsonSerializable(typeof(RunSpec))]
[JsonSerializable(typeof(RunStatus))]
internal partial class LauncherJsonContext : JsonSerializerContext
{
}
