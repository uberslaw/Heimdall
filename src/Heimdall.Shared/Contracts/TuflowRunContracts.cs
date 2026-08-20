// NEW FILE — drop in as-is at:
//   Heimdall.Shared/Contracts/TuflowRunContracts.cs

namespace Heimdall.Shared.Contracts;

/// <summary>
/// Requested TUFLOW run for a machine. Delivered to the agent as
/// AgentConfigDto.PendingTuflowStart (stored server-side as
/// Machine.PendingTuflowStartJson) and cleared once the agent reports a
/// TuflowRunStatusDto for the same RunId via heartbeat.
/// One in-flight run per machine — matches modelling-workstation reality
/// and the existing single-value FleetProcessNames(["tuflow"]) assumption
/// already baked into the Historical Dashboard fleet sampler.
/// </summary>
public sealed class TuflowStartRequestDto
{
    /// <summary>Server-generated token; correlates this request with the status the agent reports back.</summary>
    public required string RunId { get; init; }
    /// <summary>
    /// Human-facing label for this run — the Fleet Sim Progress page's "which simulation" column.
    /// Resolved server-side in TuflowRunService.QueueStartAsync: what the person typed on the start
    /// form, else the .tcf's filename (no extension) or .cmd/.bat stem, else "Sim {N}" (N = count of
    /// prior runs on that machine + 1) as a last resort. Always non-null by the time this DTO is built.
    /// </summary>
    public required string RunName { get; init; }
    /// <summary>
    /// How the launcher should start this run. One of <see cref="TuflowLaunchModes"/>.
    /// Defaults to ExeTcf for older queued payloads that omit the field.
    /// </summary>
    public string LaunchMode { get; init; } = TuflowLaunchModes.ExeTcf;
    /// <summary>Full path to the TUFLOW executable (e.g. TUFLOW_iSP_w64.exe) on the modelling machine. Required for ExeTcf; unused for Cmd.</summary>
    public string ExePath { get; init; } = "";
    /// <summary>Full path to the .tcf control file to run. Required for ExeTcf; for Cmd may be filled later from script inspection.</summary>
    public string TcfPath { get; init; } = "";
    /// <summary>
    /// Absolute/UNC path to a ready-made .cmd or .bat that already invokes TUFLOW.
    /// Required when <see cref="LaunchMode"/> is <see cref="TuflowLaunchModes.Cmd"/>; the launcher
    /// validates and then executes this script (does not reassemble a CreateProcess of TUFLOW.exe).
    /// </summary>
    public string? CmdPath { get; init; }
    /// <summary>Optional working directory; defaults to the .tcf's (or .cmd's) folder when null.</summary>
    public string? WorkingDirectory { get; init; }
    /// <summary>Scenario tokens, passed as numbered -s1/-s2/... switches (see TuflowLauncher/Program.cs). ExeTcf only.</summary>
    public List<string> Scenarios { get; init; } = [];
    /// <summary>Event tokens, passed as numbered -e1/-e2/... switches. ExeTcf only.</summary>
    public List<string> Events { get; init; } = [];
    /// <summary>Optional results/trf folder hint — see RunSpec.ResultsFolder in TuflowLauncher for why.</summary>
    public string? ResultsFolder { get; init; }
    public DateTimeOffset RequestedUtc { get; init; }
    /// <summary>
    /// Who queued this from the Heimdall website — the Fleet Sim Progress page's "who kicked it off"
    /// column. Best-effort prefilled from the signed-in Windows identity on the start form (Negotiate
    /// is wired globally in Program.cs, but whether it actually populates HttpContext.User on these
    /// internal admin pages in your deployment isn't verified — see README), but the field stays
    /// editable so this never silently ends up blank.
    /// </summary>
    public string? RequestedBy { get; init; }

    /// <summary>
    /// When true and <see cref="WorkingDirectory"/> is empty, the agent picks a local scratch folder
    /// on the best non-C fixed drive (see host scratch policy).
    /// </summary>
    public bool UseLocalScratch { get; init; }

    /// <summary>
    /// UNC archive root for post-run verified robocopy. May include <c>{hostname}</c>.
    /// Empty = org default template. Agent appends a per-run folder under this root.
    /// </summary>
    public string? ArchiveShare { get; init; }

    /// <summary>After verified offload, delete local scratch results. Default false (safe).</summary>
    public bool AutoCleanAfterVerify { get; init; }

    /// <summary>Host policy: prefer local scratch when UseLocalScratch is true (agent-side defaults).</summary>
    public double ScratchMinFreeGb { get; init; } = 50;

    /// <summary>Host policy: allow scratch on C: when no other drive qualifies.</summary>
    public bool AllowScratchOnC { get; init; }
}

/// <summary>TuflowStartRequestDto.LaunchMode / RunSpec.LaunchMode values.</summary>
public static class TuflowLaunchModes
{
    /// <summary>Heimdall builds CreateProcess(TUFLOW.exe … .tcf) from form fields.</summary>
    public const string ExeTcf = "ExeTcf";
    /// <summary>Heimdall validates then runs an existing .cmd/.bat via cmd.exe /c.</summary>
    public const string Cmd = "Cmd";
}

/// <summary>
/// Latest known state of a TUFLOW run on a machine, reported by the agent on every heartbeat while a
/// launcher-managed run exists (started, stopping, or just finished). Stored server-side as
/// Machine.TuflowRunStatusJson. The State string vocabulary is defined once here (TuflowRunStates) and
/// used verbatim by TuflowLauncher's status.json — the agent passes it through unmodified rather than
/// re-mapping it, to avoid the kind of casing/mapping drift bug that's easy to introduce across three
/// processes (launcher, agent, Api).
/// </summary>
public sealed class TuflowRunStatusDto
{
    public required string RunId { get; init; }
    /// <summary>Carried through from TuflowStartRequestDto.RunName on every status update (see RunSpec.RunName
    /// in TuflowLauncher) so it survives Machine.TuflowRunStatusJson being wholesale-replaced each heartbeat.</summary>
    public string? RunName { get; init; }
    public required string State { get; init; } // one of TuflowRunStates.*
    public int? ProcessId { get; init; }
    public string? TcfPath { get; init; }
    /// <summary>Present when this run was started from a ready-made .cmd/.bat (TuflowLaunchModes.Cmd).</summary>
    public string? CmdPath { get; init; }
    public DateTimeOffset? StartedUtc { get; init; }
    public DateTimeOffset? StopRequestedUtc { get; init; }
    public DateTimeOffset? LastCheckpointUtc { get; init; }
    /// <summary>Filename of the most recent .trf/.erf restart file the launcher observed being written.</summary>
    public string? LastCheckpointFile { get; init; }
    public int? ExitCode { get; init; }
    public string? Message { get; init; }
    public DateTimeOffset UpdatedUtc { get; init; }

    // --- Progress, parsed from TUFLOW's own .tsf (TUFLOW Summary File) — TUFLOW computes and writes
    // these itself while running (manual Section 14.4.2), so the launcher just reads them rather than
    // estimating anything. All null until the launcher finds and successfully parses a .tsf. ---

    /// <summary>"Percentage Complete (%)" from the .tsf — TUFLOW's own progress figure, not estimated.</summary>
    public double? PercentComplete { get; init; }
    /// <summary>"Simulation Time (h)" — how far into the model's simulated time the run has reached.</summary>
    public double? SimulationTimeHours { get; init; }
    /// <summary>"Simulation End Time (h)" — the model's total simulated duration.</summary>
    public double? SimulationEndTimeHours { get; init; }
    /// <summary>"Approximate Clock Time Remaining (h)" — TUFLOW's own wall-clock ETA.</summary>
    public double? ClockTimeRemainingHours { get; init; }
    /// <summary>Sum of "WARNINGs Prior to Simulation" + "WARNINGs During Simulation" from the .tsf.</summary>
    public int? WarningCount { get; init; }
    /// <summary>"Cumulative Mass Error [ME] (%)" — a model-health figure worth surfacing alongside progress.</summary>
    public double? MassErrorPercent { get; init; }

    // --- Crash/error detail, parsed from the .tlf's tail when the process exits non-zero and a stop
    // wasn't requested (see TuflowLauncher/Program.cs). Null on a clean Completed/Stopped exit. ---

    /// <summary>First few "ERROR"-prefixed lines found in the .tlf (or stderr as fallback) on a crash.</summary>
    public string? ErrorSummary { get; init; }

    // --- Local scratch + post-run archive transfer (agent-filled) ---

    /// <summary>Drive letter chosen for scratch, e.g. <c>D:</c>.</summary>
    public string? ScratchDrive { get; init; }
    /// <summary>Local folder used as WorkingDirectory / results root under scratch.</summary>
    public string? LocalResultsPath { get; init; }
    /// <summary>Final UNC destination for verified robocopy.</summary>
    public string? ArchivePath { get; init; }
    /// <summary>One of <see cref="TuflowTransferStates"/>.*</summary>
    public string? TransferState { get; init; }
    public string? TransferDetail { get; init; }
    public int? TransferLocalFileCount { get; init; }
    public int? TransferDestFileCount { get; init; }
    public long? TransferLocalBytes { get; init; }
    public long? TransferDestBytes { get; init; }
}

/// <summary>TuflowRunStatusDto.TransferState values for post-run archive.</summary>
public static class TuflowTransferStates
{
    public const string Pending = "Pending";
    public const string Copying = "Copying";
    public const string Verified = "Verified";
    public const string Failed = "Failed";
    public const string Skipped = "Skipped";
}

/// <summary>TuflowRunStatusDto.State values. Mirrors TuflowLauncher.RunState — see RunStateWire.ToWireState().</summary>
public static class TuflowRunStates
{
    public const string Starting = "Starting";
    public const string Running = "Running";
    public const string StopRequested = "StopRequested";
    public const string Stopped = "Stopped";
    public const string Completed = "Completed";
    public const string Failed = "Failed";
}

/// <summary>
/// Response for the fast, independent poll endpoint (GET /api/tuflow/{hostname}/pending) — deliberately
/// tiny (a two-column projection, not the full ConfigService pipeline) so it's cheap to poll every
/// 15-30s per machine, the same way GetResourceSamplingStatusAsync/ResourceSamplingStatusDto already do
/// for the live-sampling on/off flag. See Heimdall.Agent.Worker's TuflowPollInterval tick.
/// </summary>
public sealed class TuflowPendingDto
{
    public TuflowStartRequestDto? PendingTuflowStart { get; init; }
    /// <summary>True when RemoteMachineCommands.TuflowStopGraceful is in this machine's PendingCommands.</summary>
    public bool StopRequested { get; init; }
    /// <summary>Specific RunIds to stop (multi-sim). Empty + StopRequested = stop all tracked runs.</summary>
    public List<string> StopRunIds { get; init; } = [];
    /// <summary>Host cap for concurrent Heimdall-launched TUFLOW processes.</summary>
    public int MaxConcurrentRuns { get; init; } = 1;
}

/// <summary>TuflowQueueItem.State values.</summary>
public static class TuflowQueueItemStates
{
    public const string Queued = "Queued";
    public const string Dispatching = "Dispatching";
    public const string Running = "Running";
    public const string Completed = "Completed";
    public const string Failed = "Failed";
    public const string Stopped = "Stopped";
    public const string Cancelled = "Cancelled";
}

/// <summary>Heimdall interchange JSON for queue import/export (not TUFLOW Runner's undocumented native file).</summary>
public sealed class TuflowQueueFileDto
{
    public string Format { get; init; } = "heimdall-tuflow-queue-v1";
    public List<TuflowQueueFileItemDto> Items { get; init; } = [];
}

public sealed class TuflowQueueFileItemDto
{
    public string? RunName { get; init; }
    public string LaunchMode { get; init; } = TuflowLaunchModes.ExeTcf;
    public string ExePath { get; init; } = "";
    public string TcfPath { get; init; } = "";
    public string? CmdPath { get; init; }
    public string? WorkingDirectory { get; init; }
    public List<string> Scenarios { get; init; } = [];
    public List<string> Events { get; init; } = [];
    public string? ResultsFolder { get; init; }
    public bool UseLocalScratch { get; init; }
    public string? ArchiveShare { get; init; }
    public bool AutoCleanAfterVerify { get; init; }
}
