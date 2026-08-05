// NEW FILE — drop in as-is at:
//   Heimdall.Api/Services/TuflowRunService.cs
//
// Mirrors RemoteMachineService's PendingCommandsJson + *ProgressJson pattern (see that file) but for
// TUFLOW start/stop. Register in DI the same way RemoteMachineService is registered.
//
// Machine scoping: per Chris's requirement — "I only want it run on Flood machines, and they need to
// see how many TUFLOW runs / licences are in use on each machine" — this deliberately reuses the
// *existing* FleetDashboardMachine enrollment (the same "Historical Dashboard (TUFLOW fleet POC)" list
// FleetDashboardService already manages) rather than inventing a second, separate machine group. One
// enrolled-machines list to maintain, not two. Manage which machines are "Flood" machines from whatever
// page already enrolls/unenrolls Historical Dashboard machines (FleetDashboardService.EnrollAsync /
// UnenrollAsync) — this file doesn't add a second enrollment UI.
//
// License visibility: FleetDashboardService.GetLiveFleetAsync() already reports per-machine
// TuflowRunning (from 30s process-detection fleet snapshots, not just Heimdall-initiated runs — it
// catches a manually-started TUFLOW too). This assumes one TUFLOW licence per running instance; if your
// licence server can issue more than one licence to a single run (e.g. certain parallel/multi-domain
// configurations) treat this as a running-instance count rather than a literal licence count.

using System.Text.Json;
using Heimdall.Api.Data;
using Heimdall.Shared.Contracts;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Heimdall.Api.Services;

public class TuflowRunService(HeimdallDbContext db, FleetDashboardService fleetDashboard, ILogger<TuflowRunService> logger)
{
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true };

    /// <summary>Only Flood-enrolled machines (FleetDashboardMachine) are returned — see class remarks.</summary>
    public async Task<IReadOnlyList<TuflowRunRow>> ListAsync(CancellationToken ct)
    {
        var live = await fleetDashboard.GetLiveFleetAsync(ct);
        if (live.Count == 0)
            return [];

        var ids = live.Select(l => l.MachineId).ToList();
        var machines = await db.Machines.AsNoTracking()
            .Where(m => ids.Contains(m.Id))
            .ToDictionaryAsync(m => m.Id, ct);

        return live
            .OrderBy(l => l.Hostname)
            .Select(l => machines.TryGetValue(l.MachineId, out var m)
                ? ToRow(m, l)
                : new TuflowRunRow(l.Hostname, null, null, null, l.TuflowRunning, l.Status))
            .ToList();
    }

    /// <summary>Queues a new run. Fails if the machine isn't Flood-enrolled, a run is already active/queued,
    /// or TUFLOW is already running on that machine outside Heimdall's tracking (manual start).</summary>
    public async Task<(bool Ok, string? Error, string? RunId)> QueueStartAsync(
        string hostname,
        string? runName,
        string exePath,
        string tcfPath,
        string? workingDirectory,
        List<string> scenarios,
        List<string> events,
        string? resultsFolder,
        string? requestedBy,
        CancellationToken ct)
    {
        var machine = await db.Machines.FirstOrDefaultAsync(m => m.Hostname == hostname, ct);
        if (machine is null)
            return (false, $"Machine '{hostname}' not found.", null);

        if (!await IsFloodEnrolledAsync(machine.Id, ct))
            return (false, $"{hostname} is not enrolled as a Flood machine (Historical Dashboard enrollment). Enroll it there first.", null);

        var currentStatus = DeserializeStatus(machine.TuflowRunStatusJson);
        if (IsActiveRunState(currentStatus?.State))
            return (false, $"A run ({currentStatus!.RunId}, state {currentStatus.State}) is already tracked on {hostname}. Stop it first.", null);

        if (!string.IsNullOrWhiteSpace(machine.PendingTuflowStartJson))
            return (false, $"A start request is already queued on {hostname}, waiting for agent pickup.", null);

        // Defence in depth against double-booking a licence: the fleet sampler detects TUFLOW by process
        // name regardless of who/what started it, so this also catches someone having started a run by
        // hand on this machine outside Heimdall.
        var liveRow = (await fleetDashboard.GetLiveFleetAsync(ct)).FirstOrDefault(l => l.MachineId == machine.Id);
        if (liveRow?.TuflowRunning == true)
            return (false, $"TUFLOW is already running on {hostname} (detected by fleet sampling, not queued via Heimdall). Confirm it's finished before starting a new run.", null);

        var runId = Guid.NewGuid().ToString("n");
        var resolvedRunName = await ResolveRunNameAsync(machine.Id, runName, tcfPath, ct);
        var request = new TuflowStartRequestDto
        {
            RunId = runId,
            RunName = resolvedRunName,
            ExePath = exePath.Trim(),
            TcfPath = tcfPath.Trim(),
            WorkingDirectory = string.IsNullOrWhiteSpace(workingDirectory) ? null : workingDirectory.Trim(),
            Scenarios = scenarios.Where(s => !string.IsNullOrWhiteSpace(s)).Select(s => s.Trim()).ToList(),
            Events = events.Where(s => !string.IsNullOrWhiteSpace(s)).Select(s => s.Trim()).ToList(),
            ResultsFolder = string.IsNullOrWhiteSpace(resultsFolder) ? null : resultsFolder.Trim(),
            RequestedUtc = DateTimeOffset.UtcNow,
            RequestedBy = requestedBy
        };

        machine.PendingTuflowStartJson = JsonSerializer.Serialize(request, JsonOptions);
        // Optimistic placeholder so the UI shows "Starting" immediately, before the agent's first heartbeat
        // confirms pickup (same "Queued" pattern RemoteMachineService uses for RestartRdsProgress).
        machine.TuflowRunStatusJson = JsonSerializer.Serialize(new TuflowRunStatusDto
        {
            RunId = runId,
            RunName = resolvedRunName,
            State = TuflowRunStates.Starting,
            TcfPath = request.TcfPath,
            UpdatedUtc = request.RequestedUtc
        }, JsonOptions);

        // History row — this is what makes the run survive on the Machine page after a later run
        // overwrites TuflowRunStatusJson above. See Entities.cs.patch.md section 3.
        db.TuflowRunRecords.Add(new TuflowRunRecord
        {
            RunId = runId,
            RunName = resolvedRunName,
            MachineId = machine.Id,
            TcfPath = request.TcfPath,
            RequestedUtc = request.RequestedUtc,
            RequestedBy = requestedBy,
            State = TuflowRunStates.Starting,
            UpdatedUtc = request.RequestedUtc
        });

        await db.SaveChangesAsync(ct);
        logger.LogWarning("Queued TUFLOW start ({RunId}, \"{RunName}\") for {Host}: {Tcf}", runId, resolvedRunName, hostname, request.TcfPath);
        return (true, null, runId);
    }

    /// <summary>
    /// Resolution order per Chris's request ("...ask them to enter a name for the run on launch..."):
    /// 1. Whatever the user typed on the start form (trimmed, if non-blank).
    /// 2. The .tcf filename without extension (e.g. "M04_5m_001.tcf" -&gt; "M04_5m_001") — usually far more
    ///    useful than a generic label, since Chris's team already names their .tcf files meaningfully.
    /// 3. "Sim {N}" where N is a 1-based count of runs already queued on this machine (including this one),
    ///    only reached if both of the above are somehow unavailable.
    /// </summary>
    private async Task<string> ResolveRunNameAsync(int machineId, string? typedName, string tcfPath, CancellationToken ct)
    {
        if (!string.IsNullOrWhiteSpace(typedName))
            return typedName.Trim();

        var fromTcf = string.IsNullOrWhiteSpace(tcfPath) ? null : Path.GetFileNameWithoutExtension(tcfPath.Trim());
        if (!string.IsNullOrWhiteSpace(fromTcf))
            return fromTcf;

        var priorCount = await db.TuflowRunRecords.CountAsync(r => r.MachineId == machineId, ct);
        return $"Sim {priorCount + 1}";
    }

    /// <summary>Queues the zero-payload graceful-stop token. The agent maps it to whatever run it is tracking locally.</summary>
    public async Task<(bool Ok, string? Error)> QueueStopGracefulAsync(string hostname, CancellationToken ct)
    {
        var machine = await db.Machines.FirstOrDefaultAsync(m => m.Hostname == hostname, ct);
        if (machine is null)
            return (false, $"Machine '{hostname}' not found.");

        if (!await IsFloodEnrolledAsync(machine.Id, ct))
            return (false, $"{hostname} is not enrolled as a Flood machine.");

        var status = DeserializeStatus(machine.TuflowRunStatusJson);
        if (status is null || !IsActiveRunState(status.State))
            return (false, $"No active TUFLOW run tracked on {hostname}.");

        var pending = RemoteMachineService.DeserializeCommands(machine.PendingCommandsJson);
        if (!pending.Contains(RemoteMachineCommands.TuflowStopGraceful, StringComparer.OrdinalIgnoreCase))
            pending.Add(RemoteMachineCommands.TuflowStopGraceful);
        machine.PendingCommandsJson = JsonSerializer.Serialize(pending.Distinct(StringComparer.OrdinalIgnoreCase), JsonOptions);

        var now = DateTimeOffset.UtcNow;
        status.State = TuflowRunStates.StopRequested;
        status.StopRequestedUtc = now;
        status.UpdatedUtc = now;
        machine.TuflowRunStatusJson = JsonSerializer.Serialize(status, JsonOptions);

        await db.SaveChangesAsync(ct);
        logger.LogWarning("Queued TuflowStopGraceful for {Host} (run {RunId})", hostname, status.RunId);
        return (true, null);
    }

    /// <summary>
    /// Folds the agent's reported run status and command acks into Machine state, and keeps that run's
    /// TuflowRunRecord history row in sync (see Entities.cs.patch.md section 3 for why this is a
    /// separate call from the live-status update above it). Call from IngestService.UpsertMachineAsync
    /// alongside RemoteMachineService.ApplyHeartbeat — see IngestService.cs.patch.md. Async now (the
    /// original version was synchronous) because updating the history row needs a DB round trip to find
    /// the existing row by RunId; IngestService's call site needs an `await` added — see that patch.
    /// </summary>
    public async Task ApplyHeartbeatAsync(Machine machine, HeartbeatDto heartbeat, CancellationToken ct)
    {
        if (heartbeat.TuflowRunStatus is { } reported)
        {
            machine.TuflowRunStatusJson = JsonSerializer.Serialize(reported, JsonOptions);

            // Once the agent confirms it picked up the start request for this RunId, clear the pending
            // copy so a stale request is never re-sent on a later config refresh.
            var pendingStart = DeserializeStartRequest(machine.PendingTuflowStartJson);
            if (pendingStart is not null && string.Equals(pendingStart.RunId, reported.RunId, StringComparison.OrdinalIgnoreCase))
                machine.PendingTuflowStartJson = null;

            await UpsertHistoryAsync(machine.Id, reported, ct);
        }

        if (heartbeat.AcknowledgedCommands.Count == 0)
            return;

        var ackedStop = heartbeat.AcknowledgedCommands.Any(c =>
            string.Equals(c, RemoteMachineCommands.TuflowStopGraceful, StringComparison.OrdinalIgnoreCase));
        if (!ackedStop)
            return;

        var pending = RemoteMachineService.DeserializeCommands(machine.PendingCommandsJson);
        pending.RemoveAll(c => string.Equals(c, RemoteMachineCommands.TuflowStopGraceful, StringComparison.OrdinalIgnoreCase));
        machine.PendingCommandsJson = pending.Count == 0 ? null : JsonSerializer.Serialize(pending, JsonOptions);
    }

    /// <summary>
    /// Applies a live TuflowRunStatusDto to that run's TuflowRunRecord row (created by QueueStartAsync).
    /// Defensively creates the row if it's somehow missing (e.g. a run started before this table existed,
    /// or a manual DB edit) rather than dropping the update — better an incomplete history row than none.
    /// </summary>
    private async Task UpsertHistoryAsync(int machineId, TuflowRunStatusDto status, CancellationToken ct)
    {
        var record = await db.TuflowRunRecords.FirstOrDefaultAsync(r => r.RunId == status.RunId, ct);
        if (record is null)
        {
            record = new TuflowRunRecord
            {
                RunId = status.RunId,
                // Falls back to the RunId itself only in the defensive "row didn't already exist" branch —
                // QueueStartAsync always creates the row with a resolved RunName first, so in practice this
                // only fires for pre-existing runs from before RunName existed, or a manual DB edit.
                RunName = status.RunName ?? status.RunId,
                MachineId = machineId,
                TcfPath = status.TcfPath ?? "(unknown)",
                RequestedUtc = status.StartedUtc ?? status.UpdatedUtc,
                State = status.State,
                UpdatedUtc = status.UpdatedUtc
            };
            db.TuflowRunRecords.Add(record);
        }

        record.RunName = status.RunName ?? record.RunName;
        record.State = status.State;
        record.StartedUtc ??= status.StartedUtc;
        record.PercentComplete = status.PercentComplete ?? record.PercentComplete;
        record.SimulationTimeHours = status.SimulationTimeHours ?? record.SimulationTimeHours;
        record.SimulationEndTimeHours = status.SimulationEndTimeHours ?? record.SimulationEndTimeHours;
        record.ClockTimeRemainingHours = status.ClockTimeRemainingHours ?? record.ClockTimeRemainingHours;
        record.WarningCount = status.WarningCount ?? record.WarningCount;
        record.MassErrorPercent = status.MassErrorPercent ?? record.MassErrorPercent;
        record.LastCheckpointFile = status.LastCheckpointFile ?? record.LastCheckpointFile;
        record.UpdatedUtc = status.UpdatedUtc;

        if (!IsActiveRunState(status.State) && record.EndedUtc is null)
        {
            // First time we see this run in a terminal state — lock in the outcome. Guarded by
            // EndedUtc-is-null so a stale/duplicate heartbeat replaying an old terminal status can't
            // clobber a later run's fields (RunId is unique per run, but defensive either way).
            record.EndedUtc = status.UpdatedUtc;
            record.ExitCode = status.ExitCode;
            record.ErrorSummary = status.ErrorSummary;
        }
    }

    /// <summary>Recent run history for one machine (newest first), for the Machine page. See Machine.cshtml patch.</summary>
    public async Task<IReadOnlyList<TuflowRunHistoryEntry>> GetHistoryAsync(string hostname, int take, CancellationToken ct)
    {
        var machine = await db.Machines.AsNoTracking().FirstOrDefaultAsync(m => m.Hostname == hostname, ct);
        if (machine is null)
            return [];

        var rows = await db.TuflowRunRecords.AsNoTracking()
            .Where(r => r.MachineId == machine.Id)
            .OrderByDescending(r => r.RequestedUtc)
            .Take(Math.Clamp(take, 1, 200))
            .ToListAsync(ct);

        return rows.Select(r => new TuflowRunHistoryEntry(
            r.RunId, r.RunName, r.TcfPath, r.RequestedUtc, r.RequestedBy, r.StartedUtc, r.EndedUtc, r.State,
            r.PercentComplete, r.SimulationTimeHours, r.SimulationEndTimeHours, r.WarningCount,
            r.MassErrorPercent, r.ExitCode, r.ErrorSummary, r.LastCheckpointFile)).ToList();
    }

    /// <summary>Everything Machine.cshtml needs for its TUFLOW panel in one call: whether this machine is
    /// Flood-enrolled at all (panel is hidden entirely if not), its live status, and recent history.</summary>
    public async Task<TuflowMachineView> GetMachineViewAsync(string hostname, CancellationToken ct)
    {
        var machine = await db.Machines.AsNoTracking().FirstOrDefaultAsync(m => m.Hostname == hostname, ct);
        if (machine is null)
            return new TuflowMachineView(false, null, []);

        var enrolled = await IsFloodEnrolledAsync(machine.Id, ct);
        if (!enrolled)
            return new TuflowMachineView(false, null, []);

        var current = DeserializeStatus(machine.TuflowRunStatusJson);
        var history = await GetHistoryAsync(hostname, take: 20, ct);
        return new TuflowMachineView(true, current, history);
    }

    public static bool IsActiveRunState(string? state) => state switch
    {
        TuflowRunStates.Starting or TuflowRunStates.Running or TuflowRunStates.StopRequested => true,
        _ => false
    };

    /// <summary>
    /// Backs the fast poll endpoint (GET /api/tuflow/{hostname}/pending). Deliberately a two-column
    /// projection straight off Machines — no FleetDashboardService/enrollment join, no ConfigService
    /// pipeline — so it stays cheap enough to call every 15-30s per machine. See Worker.cs.patch.md's
    /// RunTuflowPollTickAsync for the caller.
    /// </summary>
    public async Task<TuflowPendingDto> GetPendingAsync(string hostname, CancellationToken ct)
    {
        var row = await db.Machines.AsNoTracking()
            .Where(m => m.Hostname == hostname)
            .Select(m => new { m.PendingTuflowStartJson, m.PendingCommandsJson })
            .FirstOrDefaultAsync(ct);

        if (row is null)
            return new TuflowPendingDto();

        var stopRequested = RemoteMachineService.DeserializeCommands(row.PendingCommandsJson)
            .Contains(RemoteMachineCommands.TuflowStopGraceful, StringComparer.OrdinalIgnoreCase);

        return new TuflowPendingDto
        {
            PendingTuflowStart = DeserializeStartRequest(row.PendingTuflowStartJson),
            StopRequested = stopRequested
        };
    }

    /// <summary>Fleet-wide "how many licences are in use right now" summary across Flood-enrolled machines,
    /// for a header strip on the Runs page. Based on the same TuflowRunning detection GetLiveFleetAsync uses.</summary>
    public async Task<(int EnrolledCount, int RunningCount)> GetLicenseSummaryAsync(CancellationToken ct)
    {
        var live = await fleetDashboard.GetLiveFleetAsync(ct);
        return (live.Count, live.Count(l => l.TuflowRunning));
    }

    /// <summary>
    /// Fleet-wide "who's running what, where, for how long, and how hard" view for the Fleet Sim Progress
    /// page. One row per currently-active (Starting/Running/StopRequested) TuflowRunRecord across all
    /// Flood-enrolled machines — "active" here means Heimdall-tracked, so a run that fails outright
    /// (State flips to Failed on the next heartbeat) drops off this list on its own; check the Machine
    /// page's history panel for anything that's finished, crashed, or been stopped.
    ///
    /// GPU/CPU/Disk are averaged from FleetMetricSnapshot rows sampled since the run's StartedUtc
    /// (RequestedUtc if it hasn't confirmed started yet) — both a per-run figure (ProcessCpuPercent etc.,
    /// i.e. "separate for each TUFLOW exe" so far as Heimdall's one-run-per-machine model allows: it's
    /// really "this machine's TUFLOW process(es)", which is the same thing under that model) and a
    /// whole-machine aggregate, since Chris asked for both ("separate ... plus an aggregate"). Network has
    /// no process-specific figure available at all (see Entities.cs.patch.md section 6 for why), so it's
    /// reported as the whole-machine aggregate only — the "otherwise just the total" fallback Chris's own
    /// wording already allowed for.
    ///
    /// On "5 min": the actual fleet sampling cadence is 30s (FleetSampleInterval in Worker.cs), not 5
    /// minutes — ConfigRefreshSeconds (default 300s) is a different cadence for a different thing (pulling
    /// AgentConfigDto), easy to conflate with fleet sampling since both live in the same Worker loop. This
    /// method averages over each run's whole lifetime-so-far using the real 30s samples, not a rolling
    /// 5-minute window. If a rolling window turns out to be what was actually meant, that's a small change
    /// here (filter snaps to `now.AddMinutes(-5)` instead of `since`) — flagging it rather than guessing.
    /// </summary>
    public async Task<IReadOnlyList<FleetSimProgressRow>> GetFleetProgressAsync(CancellationToken ct)
    {
        var activeStates = new[] { TuflowRunStates.Starting, TuflowRunStates.Running, TuflowRunStates.StopRequested };
        var activeRuns = await db.TuflowRunRecords.AsNoTracking()
            .Where(r => activeStates.Contains(r.State))
            .Include(r => r.Machine)
            .ToListAsync(ct);

        if (activeRuns.Count == 0)
            return [];

        var machineIds = activeRuns.Select(r => r.MachineId).Distinct().ToList();

        // SQLite EF DateTimeOffset filters are unreliable — load by machine id, filter in memory. Same
        // pattern as FleetDashboardService.LoadSnapshotsForMachinesAsync; not reused directly since that
        // method is private to that class and this needs a per-run "since" cutoff, not one shared cutoff.
        var allSnaps = await db.FleetMetricSnapshots.AsNoTracking()
            .Where(s => machineIds.Contains(s.MachineId))
            .ToListAsync(ct);

        var now = DateTimeOffset.UtcNow;
        var rows = new List<FleetSimProgressRow>();

        foreach (var run in activeRuns.OrderBy(r => r.Machine.Hostname))
        {
            var since = run.StartedUtc ?? run.RequestedUtc;
            var snaps = allSnaps
                .Where(s => s.MachineId == run.MachineId && s.SampledAtUtc >= since)
                .OrderBy(s => s.SampledAtUtc)
                .ToList();

            rows.Add(new FleetSimProgressRow(
                run.Machine.Hostname,
                run.Machine.FriendlyName,
                run.RunName,
                run.RequestedBy,
                run.State,
                since,
                now - since,
                run.ClockTimeRemainingHours, // TUFLOW's own .tsf estimate — null until TUFLOW has reported one
                run.PercentComplete,
                run.SimulationTimeHours,
                run.SimulationEndTimeHours,
                AvgNonNull(snaps, s => s.ProcessCpuPercent),
                AvgNonNull(snaps, s => s.ProcessGpuPercent),
                AvgNonNull(snaps, s => s.ProcessDiskReadMBps),
                AvgNonNull(snaps, s => s.ProcessDiskWriteMBps),
                AvgNonNull(snaps, s => s.CpuPercent),
                AvgNonNull(snaps, s => s.GpuPercent),
                AvgNonNull(snaps, s => s.DiskReadMBps),
                AvgNonNull(snaps, s => s.DiskWriteMBps),
                AvgNonNull(snaps, s => s.NetworkInMBps),
                AvgNonNull(snaps, s => s.NetworkOutMBps),
                snaps.Count));
        }

        return rows;
    }

    private static double? AvgNonNull(List<FleetMetricSnapshot> snaps, Func<FleetMetricSnapshot, double?> selector)
    {
        var vals = snaps.Select(selector).Where(v => v.HasValue).Select(v => v!.Value).ToList();
        return vals.Count == 0 ? null : vals.Average();
    }

    private async Task<bool> IsFloodEnrolledAsync(int machineId, CancellationToken ct) =>
        await db.FleetDashboardMachines.AsNoTracking().AnyAsync(f => f.MachineId == machineId, ct);

    /// <summary>internal-visibility twin of RemoteMachineService.DeserializeCommands — used by ConfigService
    /// to populate AgentConfigDto.PendingTuflowStart. See ConfigService.cs.patch.md.</summary>
    internal static TuflowStartRequestDto? DeserializeStartRequest(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
            return null;
        try
        {
            return JsonSerializer.Deserialize<TuflowStartRequestDto>(json, JsonOptions);
        }
        catch
        {
            return null;
        }
    }

    private static TuflowRunStatusDto? DeserializeStatus(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
            return null;
        try
        {
            return JsonSerializer.Deserialize<TuflowRunStatusDto>(json, JsonOptions);
        }
        catch
        {
            return null;
        }
    }

    private static TuflowRunRow ToRow(Machine m, FleetDashboardService.LiveFleetRow live) => new(
        m.Hostname,
        m.FriendlyName,
        DeserializeStartRequest(m.PendingTuflowStartJson),
        DeserializeStatus(m.TuflowRunStatusJson),
        live.TuflowRunning,
        live.Status);
}

/// <summary>
/// TuflowRunningNow / FleetStatus come from FleetDashboardService's existing process-detection fleet
/// sampler (30s cadence), independent of PendingStart/Status which are Heimdall's own tracked-run state.
/// The two usually agree, but TuflowRunningNow catches TUFLOW instances started outside Heimdall too —
/// that's the "how many licences are actually in use" signal, not just "how many did Heimdall start".
/// </summary>
public sealed record TuflowRunRow(
    string Hostname,
    string? FriendlyName,
    TuflowStartRequestDto? PendingStart,
    TuflowRunStatusDto? Status,
    bool TuflowRunningNow,
    FleetDashboardService.FleetStatus FleetStatus);

/// <summary>One past-or-current TUFLOW run on a single machine — a flattened read model over
/// TuflowRunRecord, for Machine.cshtml's history table.</summary>
public sealed record TuflowRunHistoryEntry(
    string RunId,
    string RunName,
    string TcfPath,
    DateTimeOffset RequestedUtc,
    string? RequestedBy,
    DateTimeOffset? StartedUtc,
    DateTimeOffset? EndedUtc,
    string State,
    double? PercentComplete,
    double? SimulationTimeHours,
    double? SimulationEndTimeHours,
    int? WarningCount,
    double? MassErrorPercent,
    int? ExitCode,
    string? ErrorSummary,
    string? LastCheckpointFile)
{
    public bool IsActive => TuflowRunService.IsActiveRunState(State);
    public bool IsFailure => State == TuflowRunStates.Failed;
}

/// <summary>Everything the Machine page's TUFLOW panel needs, from TuflowRunService.GetMachineViewAsync.</summary>
public sealed record TuflowMachineView(
    bool FloodEnrolled,
    TuflowRunStatusDto? Current,
    IReadOnlyList<TuflowRunHistoryEntry> History);

/// <summary>
/// One active TUFLOW run for the Fleet Sim Progress page — see TuflowRunService.GetFleetProgressAsync for
/// how each field is computed. Process*Avg figures are TUFLOW-executable-specific (as close to "separate
/// for each TUFLOW exe" as Heimdall's one-run-per-machine model allows); Machine*Avg figures are the same
/// whole-machine aggregate used everywhere else in the fleet dashboard. There is no MachineNetwork-style
/// per-process split for Network — see the GetFleetProgressAsync doc comment for why.
/// </summary>
public sealed record FleetSimProgressRow(
    string Hostname,
    string? FriendlyName,
    string RunName,
    string? RequestedBy,
    string State,
    DateTimeOffset StartedUtc,
    TimeSpan Elapsed,
    // TUFLOW's own "Approximate Clock Time Remaining (h)" from the .tsf. Null until TUFLOW has written at
    // least one .tsf update for this run (typically within the first minute or so).
    double? EstRemainingHours,
    double? PercentComplete,
    double? SimulationTimeHours,
    double? SimulationEndTimeHours,
    double? ProcessCpuPercentAvg,
    double? ProcessGpuPercentAvg,
    double? ProcessDiskReadMBpsAvg,
    double? ProcessDiskWriteMBpsAvg,
    double? MachineCpuPercentAvg,
    double? MachineGpuPercentAvg,
    double? MachineDiskReadMBpsAvg,
    double? MachineDiskWriteMBpsAvg,
    double? MachineNetworkInMBpsAvg,
    double? MachineNetworkOutMBpsAvg,
    // How many FleetMetricSnapshot rows (30s cadence) the averages above are built from — shown on the
    // page as a rough "confidence" indicator (e.g. 2 samples right after a run starts vs. 200+ for an
    // hours-long run).
    int SampleCount);
