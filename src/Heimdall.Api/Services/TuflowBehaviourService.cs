using System.Text.Json;
using Heimdall.Api.Data;
using Heimdall.Shared;
using Heimdall.Shared.Contracts;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Heimdall.Api.Services;

public sealed class TuflowBehaviourOptions
{
    public const string SectionName = "Heimdall:TuflowBehaviour";

    public double CpuPercentThreshold { get; set; } = TuflowBehaviourDefaults.CpuPercentThreshold;
    /// <summary>Process GPU (else machine GPU) above this counts as elevated — GPU-bound TUFLOW.</summary>
    public double GpuPercentThreshold { get; set; } = TuflowBehaviourDefaults.GpuPercentThreshold;
    public int ConfirmIntervals { get; set; } = TuflowBehaviourDefaults.ConfirmIntervals;
    public int SampleRetentionDays { get; set; } = TuflowBehaviourDefaults.SampleRetentionDays;
    /// <summary>Unused: Live Active-column stop stamps persist until the next run (see GetDisplayByMachineAsync).</summary>
    public int RecentStopDisplayHours { get; set; } = TuflowBehaviourDefaults.RecentStopDisplayHours;
    public bool Enabled { get; set; } = true;
}

/// <summary>
/// Detects TUFLOW run start/stop from fleet process CPU/GPU samples and persists behaviour analytics.
/// Start: elevated (CPU &gt; threshold or GPU &gt; threshold) for ConfirmIntervals → DetectedStartUtc = first of that pair.
/// Stop: not elevated (or process gone) for ConfirmIntervals → DetectedEndUtc = first of that low/gone pair.
/// Idle-tail Ended rows resume Active if work elevates again while tuflow.exe is still present.
/// </summary>
public sealed class TuflowBehaviourService(
    HeimdallDbContext db,
    IOptions<TuflowBehaviourOptions> options,
    ILogger<TuflowBehaviourService> logger)
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    private static readonly string[] GpuHistogramBuckets =
    [
        "0-10", "10-20", "20-30", "30-40", "40-50",
        "50-60", "60-70", "70-80", "80-90", "90-100", "100+"
    ];

    public async Task ApplySnapshotAsync(Machine machine, FleetSnapshotDto dto, CancellationToken ct)
    {
        var opts = options.Value;
        if (!opts.Enabled)
            return;

        var cpuThreshold = Math.Max(0, opts.CpuPercentThreshold);
        var gpuThreshold = Math.Max(0, opts.GpuPercentThreshold);
        var confirm = Math.Max(1, opts.ConfirmIntervals);
        var sampledAt = dto.SampledAtUtc == default ? DateTimeOffset.UtcNow : dto.SampledAtUtc;
        var intervalSec = dto.SampleIntervalSeconds is > 0
            ? dto.SampleIntervalSeconds.Value
            : (dto.TuflowRunning
                ? TuflowBehaviourDefaults.FastSampleSeconds
                : TuflowBehaviourDefaults.NormalFleetSampleSeconds);

        var open = await db.TuflowBehaviourRuns
            .FirstOrDefaultAsync(r => r.MachineId == machine.Id
                && (r.State == TuflowBehaviourStates.Watching || r.State == TuflowBehaviourStates.Active), ct);

        // Idle-tail: Ended but ProcessGoneUtc still null — keep collecting until process leaves.
        // SQLite EF cannot ORDER BY DateTimeOffset — sort in memory.
        if (open is null)
        {
            var idleTail = await db.TuflowBehaviourRuns
                .Where(r => r.MachineId == machine.Id
                    && r.State == TuflowBehaviourStates.Ended
                    && r.ProcessGoneUtc == null
                    && r.DetectedEndUtc != null)
                .ToListAsync(ct);
            open = idleTail.OrderByDescending(r => r.DetectedEndUtc).FirstOrDefault();
        }

        var processCpu = dto.TuflowRunning ? (dto.ProcessCpuPercent ?? 0) : 0;
        // Prefer process GPU; also consider machine GPU while TUFLOW is present so GPU-bound runs
        // stay elevated even when PID→engine mapping under-reports (Megatron-style: CPU ~15%, GPU ~96%).
        var processGpu = SanitizeGpu(dto.ProcessGpuPercent);
        var machineGpu = dto.TuflowRunning ? SanitizeGpu(dto.GpuPercent) : null;
        var elevatedGpu = MaxNullable(processGpu, machineGpu);
        var elevated = dto.TuflowRunning
            && (processCpu > cpuThreshold || elevatedGpu is { } eg && eg > gpuThreshold);

        if (open is null)
        {
            if (!dto.TuflowRunning)
                return;

            open = OpenWatch(machine, dto, sampledAt);
            open.ProcessFirstSeenUtc = await ResolveStreakStartUtcAsync(machine.Id, sampledAt, ct);
            db.TuflowBehaviourRuns.Add(open);
        }

        AssignBehaviourUsername(open, dto.Username);

        open.UpdatedUtc = sampledAt;
        AppendSample(open, dto, sampledAt, intervalSec, processGpu);
        MergeGpuEngines(open, dto.GpuEngineSightings);
        UpdatePeaksAndHistogram(open, processCpu, processGpu ?? elevatedGpu, intervalSec);

        // Refresh first-seen from fleet streak while still watching (recovers mid-job after API outage).
        if (open.State == TuflowBehaviourStates.Watching && open.DetectedStartUtc is null)
        {
            var streakStart = await ResolveStreakStartUtcAsync(machine.Id, sampledAt, ct);
            if (streakStart < open.ProcessFirstSeenUtc)
                open.ProcessFirstSeenUtc = streakStart;
        }

        if (open.State == TuflowBehaviourStates.Ended)
        {
            // False stop while process still busy (e.g. GPU-bound under CPU threshold): resume Active.
            if (TryResumeActiveFromIdleTail(open, elevated, dto.TuflowRunning, sampledAt, confirm))
                return;

            await HandleIdleTailAsync(open, dto, sampledAt, confirm, ct);
            return;
        }

        if (open.State == TuflowBehaviourStates.Watching)
            AdvanceWatching(open, elevated, dto.TuflowRunning, sampledAt, confirm);
        else if (open.State == TuflowBehaviourStates.Active)
            AdvanceActive(open, elevated, dto.TuflowRunning, sampledAt, confirm);

        // Soft-link Heimdall launcher run when present.
        if (open.LinkedTuflowRunId is null && !string.IsNullOrWhiteSpace(machine.TuflowRunStatusJson))
        {
            try
            {
                var status = JsonSerializer.Deserialize<TuflowRunStatusDto>(machine.TuflowRunStatusJson, JsonOptions);
                if (status is not null && TuflowRunService.IsActiveRunState(status.State))
                    open.LinkedTuflowRunId = status.RunId;
            }
            catch
            {
                // ignore malformed status json
            }
        }

        await Task.CompletedTask;
    }

    private static TuflowBehaviourRun OpenWatch(Machine machine, FleetSnapshotDto dto, DateTimeOffset sampledAt) =>
        new()
        {
            RunId = Guid.NewGuid().ToString("n"),
            MachineId = machine.Id,
            Username = PreferInitialUsername(dto.Username),
            HardwareCpu = machine.HardwareCpu,
            HardwareGpu = machine.HardwareGpu,
            HardwareRamGb = machine.HardwareRamGb,
            State = TuflowBehaviourStates.Watching,
            ProcessFirstSeenUtc = sampledAt,
            ElevatedStreak = 0,
            LowStreak = 0,
            AbsentStreak = 0,
            GpuPercentHistogramJson = "{}",
            GpuEnginesObservedJson = "[]",
            UpdatedUtc = sampledAt
        };

    /// <summary>
    /// Live USER should reflect who owns the TUFLOW work, not a transient ops.* console fixer.
    /// Freeze a non-ops name once known; allow upgrade from ops → non-ops; never the reverse.
    /// (Tuflow process owner is not collected by the agent yet.)
    /// </summary>
    private static void AssignBehaviourUsername(TuflowBehaviourRun open, string? sampleUser)
    {
        if (string.IsNullOrWhiteSpace(sampleUser))
            return;

        var trimmed = sampleUser.Trim();
        var sampleOps = SupportAccount.IsOpsSupport(trimmed);

        if (string.IsNullOrWhiteSpace(open.Username))
        {
            open.Username = trimmed;
            return;
        }

        var currentOps = SupportAccount.IsOpsSupport(open.Username);
        if (sampleOps && !currentOps)
            return;
        if (!sampleOps && currentOps)
        {
            open.Username = trimmed;
            return;
        }

        // Once Active/Ended, keep the run owner stable (still allow ops→non-ops above).
        if ((open.State is TuflowBehaviourStates.Active or TuflowBehaviourStates.Ended) && !currentOps)
            return;

        open.Username = trimmed;
    }

    private static string? PreferInitialUsername(string? sampleUser) =>
        string.IsNullOrWhiteSpace(sampleUser) ? null : sampleUser.Trim();

    /// <summary>
    /// Idle-tail Ended while tuflow.exe still present: if util elevates again for ConfirmIntervals,
    /// clear the false stop and return to Active (keeps original DetectedStartUtc).
    /// </summary>
    private bool TryResumeActiveFromIdleTail(
        TuflowBehaviourRun open,
        bool elevated,
        bool tuflowRunning,
        DateTimeOffset sampledAt,
        int confirm)
    {
        if (!tuflowRunning || !elevated)
        {
            open.ElevatedStreak = 0;
            return false;
        }

        open.ElevatedStreak++;
        open.LowStreak = 0;
        open.AbsentStreak = 0;
        if (open.ElevatedStreak < confirm)
            return true; // consumed this sample; do not advance idle-tail gone logic

        open.State = TuflowBehaviourStates.Active;
        open.DetectedEndUtc = null;
        open.CandidateEndUtc = null;
        open.ProcessGoneUtc = null;
        open.RampDownSeconds = null;
        open.ElevatedStreak = confirm;
        open.DetectedStartUtc ??= open.CandidateStartUtc ?? open.ProcessFirstSeenUtc;
        logger.LogInformation(
            "TUFLOW behaviour resumed Active {RunId} machine {MachineId} at {At} (elevated again during idle-tail)",
            open.RunId, open.MachineId, sampledAt);
        return true;
    }

    /// <summary>
    /// Walk recent fleet snapshots backward while TuflowRunning to recover the start of the current
    /// process streak (so Active stamps survive API outages that reopen a watch mid-job).
    /// </summary>
    private async Task<DateTimeOffset> ResolveStreakStartUtcAsync(
        int machineId,
        DateTimeOffset sampledAt,
        CancellationToken ct)
    {
        var lookback = sampledAt.AddHours(-36);
        // SQLite EF: avoid DateTimeOffset ORDER BY — sort in memory.
        var recent = await db.FleetMetricSnapshots.AsNoTracking()
            .Where(s => s.MachineId == machineId)
            .OrderByDescending(s => s.Id)
            .Take(8000)
            .Select(s => new { s.SampledAtUtc, s.TuflowRunning })
            .ToListAsync(ct);

        var ordered = recent
            .Where(s => s.SampledAtUtc >= lookback && s.SampledAtUtc <= sampledAt)
            .OrderByDescending(s => s.SampledAtUtc)
            .ToList();

        var streakStart = sampledAt;
        DateTimeOffset? prev = null;
        // Bridge ingest outages (hours) while Tuflow stayed up; stop on a clear not-running sample
        // or a very long empty gap that likely spans a different job day.
        const double maxSilentGapHours = 6;
        foreach (var s in ordered)
        {
            if (!s.TuflowRunning)
                break;
            if (prev is { } p && (p - s.SampledAtUtc).TotalHours > maxSilentGapHours)
                break;
            streakStart = s.SampledAtUtc;
            prev = s.SampledAtUtc;
        }

        return streakStart;
    }

    private void AdvanceWatching(
        TuflowBehaviourRun open,
        bool elevated,
        bool tuflowRunning,
        DateTimeOffset sampledAt,
        int confirm)
    {
        if (!tuflowRunning)
        {
            open.AbsentStreak++;
            open.ElevatedStreak = 0;
            open.CandidateStartUtc = null;
            if (open.AbsentStreak >= confirm)
            {
                // Never ramped — discard watch (no DetectedStartUtc).
                db.TuflowBehaviourRuns.Remove(open);
                logger.LogDebug("Discarded TUFLOW watch {RunId} on machine {MachineId} (never elevated)", open.RunId, open.MachineId);
            }
            return;
        }

        open.AbsentStreak = 0;
        if (elevated)
        {
            open.ElevatedStreak++;
            open.CandidateStartUtc ??= sampledAt;
            open.LowStreak = 0;
            if (open.ElevatedStreak >= confirm && open.CandidateStartUtc is { } start)
            {
                open.State = TuflowBehaviourStates.Active;
                open.DetectedStartUtc = start;
                open.RampUpSeconds = Math.Max(0, (start - open.ProcessFirstSeenUtc).TotalSeconds);
                open.ElevatedStreak = confirm;
                logger.LogInformation(
                    "TUFLOW behaviour start detected {RunId} machine {MachineId} at {Start} (ramp-up {Ramp:F0}s)",
                    open.RunId, open.MachineId, start, open.RampUpSeconds);
            }
        }
        else
        {
            open.ElevatedStreak = 0;
            open.CandidateStartUtc = null;
        }
    }

    private void AdvanceActive(
        TuflowBehaviourRun open,
        bool elevated,
        bool tuflowRunning,
        DateTimeOffset sampledAt,
        int confirm)
    {
        if (elevated)
        {
            open.LowStreak = 0;
            open.AbsentStreak = 0;
            open.CandidateEndUtc = null;
            return;
        }

        // Low CPU or process gone counts toward stop confirmation.
        open.LowStreak++;
        open.CandidateEndUtc ??= sampledAt;
        if (!tuflowRunning)
            open.AbsentStreak++;

        if (open.LowStreak < confirm || open.CandidateEndUtc is not { } end)
            return;

        open.State = TuflowBehaviourStates.Ended;
        open.DetectedEndUtc = end;
        open.LowStreak = confirm;

        if (!tuflowRunning)
        {
            open.ProcessGoneUtc = sampledAt;
            open.RampDownSeconds = Math.Max(0, (sampledAt - end).TotalSeconds);
        }

        logger.LogInformation(
            "TUFLOW behaviour stop detected {RunId} machine {MachineId} at {End} (ramp-down {Ramp})",
            open.RunId, open.MachineId, end,
            open.RampDownSeconds is { } r ? $"{r:F0}s" : "pending");
    }

    private async Task HandleIdleTailAsync(
        TuflowBehaviourRun open,
        FleetSnapshotDto dto,
        DateTimeOffset sampledAt,
        int confirm,
        CancellationToken ct)
    {
        if (open.ProcessGoneUtc is not null)
            return;

        if (dto.TuflowRunning)
        {
            open.AbsentStreak = 0;
            return;
        }

        open.AbsentStreak++;
        if (open.AbsentStreak < confirm)
            return;

        open.ProcessGoneUtc = sampledAt;
        if (open.DetectedEndUtc is { } end)
            open.RampDownSeconds = Math.Max(0, (sampledAt - end).TotalSeconds);

        await Task.CompletedTask;
    }

    private static void AppendSample(
        TuflowBehaviourRun open,
        FleetSnapshotDto dto,
        DateTimeOffset sampledAt,
        int intervalSec,
        double? processGpu)
    {
        open.SampleCount++;
        var enginesJson = dto.GpuEngineSightings.Count == 0
            ? "[]"
            : JsonSerializer.Serialize(dto.GpuEngineSightings, JsonOptions);

        open.Samples.Add(new TuflowBehaviourSample
        {
            SampledAtUtc = sampledAt,
            IntervalSeconds = intervalSec,
            TuflowRunning = dto.TuflowRunning,
            ProcessCpuPercent = dto.TuflowRunning ? dto.ProcessCpuPercent : null,
            ProcessGpuPercent = processGpu,
            MachineCpuPercent = dto.CpuPercent,
            MachineGpuPercent = SanitizeGpu(dto.GpuPercent),
            MachineRamUsedMb = dto.RamUsedMb,
            ProcessDiskWriteMBps = dto.TuflowRunning ? dto.ProcessDiskWriteMBps : null,
            MachineDiskWriteMBps = dto.DiskWriteMBps,
            NetworkOutMBps = dto.NetworkOutMBps,
            GpuEnginesJson = enginesJson
        });
    }

    private static void UpdatePeaksAndHistogram(
        TuflowBehaviourRun open,
        double processCpu,
        double? processGpu,
        int intervalSec)
    {
        if (processCpu > 0)
        {
            open.PeakCpuPercent = Math.Max(open.PeakCpuPercent ?? 0, processCpu);
            open.SumCpuPercent = (open.SumCpuPercent ?? 0) + processCpu;
        }

        if (processGpu is { } gpu && gpu > 0)
        {
            open.PeakGpuPercent = Math.Max(open.PeakGpuPercent ?? 0, gpu);
            open.SumGpuPercent = (open.SumGpuPercent ?? 0) + gpu;
            AddHistogramSeconds(open, gpu, intervalSec);
        }
    }

    private static void AddHistogramSeconds(TuflowBehaviourRun open, double gpuPercent, int intervalSec)
    {
        var hist = DeserializeHistogram(open.GpuPercentHistogramJson);
        var bucket = GpuBucketLabel(gpuPercent);
        hist[bucket] = hist.TryGetValue(bucket, out var sec) ? sec + intervalSec : intervalSec;
        open.GpuPercentHistogramJson = JsonSerializer.Serialize(hist, JsonOptions);
    }

    private static string GpuBucketLabel(double gpuPercent)
    {
        if (gpuPercent >= 100) return "100+";
        var idx = Math.Clamp((int)(gpuPercent / 10), 0, 9);
        return GpuHistogramBuckets[idx];
    }

    private static Dictionary<string, double> DeserializeHistogram(string? json)
    {
        if (string.IsNullOrWhiteSpace(json) || json == "{}")
            return new Dictionary<string, double>(StringComparer.Ordinal);
        try
        {
            return JsonSerializer.Deserialize<Dictionary<string, double>>(json, JsonOptions)
                   ?? new Dictionary<string, double>(StringComparer.Ordinal);
        }
        catch
        {
            return new Dictionary<string, double>(StringComparer.Ordinal);
        }
    }

    private static void MergeGpuEngines(TuflowBehaviourRun open, IReadOnlyList<GpuEngineSightingDto>? sightings)
    {
        if (sightings is null || sightings.Count == 0)
            return;

        var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            var existing = JsonSerializer.Deserialize<List<string>>(open.GpuEnginesObservedJson, JsonOptions);
            if (existing is not null)
                foreach (var e in existing)
                    if (!string.IsNullOrWhiteSpace(e))
                        set.Add(e);
        }
        catch { /* start fresh */ }

        foreach (var s in sightings)
        {
            if (!string.IsNullOrWhiteSpace(s.Label))
                set.Add(s.Label.Trim());
        }

        open.GpuEnginesObservedJson = JsonSerializer.Serialize(set.OrderBy(x => x).ToList(), JsonOptions);
    }

    private static double? SanitizeGpu(double? gpu) =>
        gpu is null or < 0 or > ResourceMetricsCollectorMaxGpu ? null : gpu;

    private static double? MaxNullable(double? a, double? b) =>
        (a, b) switch
        {
            (null, null) => null,
            (null, { } y) => y,
            ({ } x, null) => x,
            ({ } x, { } y) => Math.Max(x, y)
        };

    // Mirror agent MaxSaneGpuPercent without referencing Agent assembly.
    private const double ResourceMetricsCollectorMaxGpu = 1000.0;

    public async Task<IReadOnlyDictionary<int, BehaviourDisplay>> GetDisplayByMachineAsync(
        IReadOnlyList<int> machineIds,
        CancellationToken ct)
    {
        if (machineIds.Count == 0)
            return new Dictionary<int, BehaviourDisplay>();

        // Open Active/Watching plus Ended rows (stop stamp persists until the next run starts —
        // no RecentStopDisplayHours window). SQLite EF: filter/sort DateTimeOffset in memory.
        var candidates = await db.TuflowBehaviourRuns.AsNoTracking()
            .Where(r => machineIds.Contains(r.MachineId)
                && (r.State == TuflowBehaviourStates.Active
                    || r.State == TuflowBehaviourStates.Watching
                    || r.State == TuflowBehaviourStates.Ended))
            .ToListAsync(ct);

        var result = new Dictionary<int, BehaviourDisplay>();
        foreach (var group in candidates.GroupBy(r => r.MachineId))
        {
            // Confirmed elevated run → DetectedStartUtc stamp.
            var active = group.FirstOrDefault(r => r.State == TuflowBehaviourStates.Active);
            if (active is not null)
            {
                var start = active.DetectedStartUtc ?? active.CandidateStartUtc ?? active.ProcessFirstSeenUtc;
                result[group.Key] = new BehaviourDisplay(
                    TuflowBehaviourStates.Active,
                    start,
                    null,
                    active.RampUpSeconds,
                    active.Username);
                continue;
            }

            // Open watch (process seen, not yet elevated-confirmed) → show process-first-seen as start
            // so Active column is never stuck on literal "Active" while a run row is open.
            var watching = group.FirstOrDefault(r => r.State == TuflowBehaviourStates.Watching);
            if (watching is not null)
            {
                var start = watching.CandidateStartUtc ?? watching.ProcessFirstSeenUtc;
                result[group.Key] = new BehaviourDisplay(
                    TuflowBehaviourStates.Watching,
                    start,
                    null,
                    watching.RampUpSeconds,
                    watching.Username);
                continue;
            }

            // Most recent completed detection — keep showing until a new Watching/Active opens.
            var ended = group
                .Where(r => r.State == TuflowBehaviourStates.Ended && r.DetectedEndUtc is not null)
                .OrderByDescending(r => r.DetectedEndUtc)
                .FirstOrDefault();
            if (ended?.DetectedEndUtc is { } end)
            {
                result[group.Key] = new BehaviourDisplay(
                    TuflowBehaviourStates.Ended,
                    ended.DetectedStartUtc,
                    end,
                    ended.RampUpSeconds,
                    ended.Username);
            }
        }

        return result;
    }

    public async Task<IReadOnlyList<TuflowBehaviourListRow>> ListRecentAsync(int take, CancellationToken ct)
    {
        take = Math.Clamp(take, 1, 200);
        // SQLite DateTimeOffset ORDER BY — sort in memory.
        var rows = await db.TuflowBehaviourRuns.AsNoTracking()
            .Include(r => r.Machine)
            .Where(r => r.DetectedStartUtc != null)
            .ToListAsync(ct);

        return rows
            .OrderByDescending(r => r.DetectedStartUtc ?? r.ProcessFirstSeenUtc)
            .Take(take)
            .Select(r => new TuflowBehaviourListRow(
                r.RunId,
                r.Machine.Hostname,
                r.Machine.FriendlyName,
                r.Username,
                r.State,
                r.DetectedStartUtc,
                r.DetectedEndUtc,
                r.RampUpSeconds,
                r.RampDownSeconds,
                r.PeakCpuPercent,
                r.PeakGpuPercent,
                r.SampleCount == 0 ? null : r.SumCpuPercent / r.SampleCount,
                r.SampleCount == 0 || r.SumGpuPercent is null ? null : r.SumGpuPercent / r.SampleCount,
                r.HardwareCpu,
                r.HardwareGpu,
                r.SampleCount,
                r.LinkedTuflowRunId))
            .ToList();
    }

    /// <summary>Max per-run rows returned for the Runs card (newest confirmed starts first).</summary>
    public const int BehaviourRunsPageSize = 75;

    /// <summary>Safety cap on GPU series points per run (full sample resolution up to this; rare for 10s sampling).</summary>
    public const int GpuSeriesMaxPoints = 5000;

    /// <summary>
    /// Aggregates confirmed behaviour runs (DetectedStartUtc set) over a rolling window.
    /// Watching-only rows without a confirmed start are excluded. Open Active runs count toward
    /// starts / hour-of-day but are omitted from completed-duration averages.
    /// Does not mutate run rows — safe for Live Active stamps.
    /// </summary>
    public async Task<TuflowBehaviourAnalytics> GetBehaviourAnalyticsAsync(int days, CancellationToken ct)
    {
        days = Math.Clamp(days, 1, 365);
        var fromUtc = DateTimeOffset.UtcNow.AddDays(-days);
        var now = DateTimeOffset.UtcNow;

        // SQLite DateTimeOffset filter — load candidates then filter in memory.
        var rows = await db.TuflowBehaviourRuns.AsNoTracking()
            .Include(r => r.Machine)
            .Where(r => r.DetectedStartUtc != null)
            .ToListAsync(ct);

        var inWindow = rows
            .Where(r => r.DetectedStartUtc is { } s && s >= fromUtc)
            .ToList();

        var completed = inWindow
            .Where(r => r.DetectedEndUtc is not null
                && (r.State == TuflowBehaviourStates.Ended
                    || r.DetectedEndUtc <= now))
            .Select(r => (r, Duration: r.DetectedEndUtc!.Value - r.DetectedStartUtc!.Value))
            .Where(x => x.Duration > TimeSpan.Zero)
            .ToList();

        var openCount = inWindow.Count(r =>
            r.State is TuflowBehaviourStates.Active or TuflowBehaviourStates.Watching
            || (r.State == TuflowBehaviourStates.Ended && r.DetectedEndUtc is null));

        var startHours = new int[24];
        foreach (var r in inWindow)
        {
            var localHour = r.DetectedStartUtc!.Value.ToLocalTime().Hour;
            startHours[localHour]++;
        }

        var byUser = BuildDimensionRows(
            inWindow,
            completed,
            r => string.IsNullOrWhiteSpace(r.Username) ? "(unknown)" : r.Username.Trim(),
            r => string.IsNullOrWhiteSpace(r.Username) ? "(unknown)" : r.Username.Trim());

        var byMachine = BuildDimensionRows(
            inWindow,
            completed,
            r => r.Machine.Hostname,
            r => string.IsNullOrWhiteSpace(r.Machine.FriendlyName)
                ? r.Machine.Hostname
                : r.Machine.FriendlyName.Trim());

        var runPage = await BuildBehaviourRunRowsAsync(inWindow, now, ct);

        return new TuflowBehaviourAnalytics(
            days,
            fromUtc,
            inWindow.Count,
            completed.Count,
            openCount,
            AverageDuration(completed.Select(c => c.Duration)),
            MedianDuration(completed.Select(c => c.Duration)),
            PeakStartHour(startHours),
            startHours.Select((count, hour) => new HourBucket(hour, count)).ToList(),
            byUser,
            byMachine,
            runPage.Rows,
            runPage.TotalInWindow,
            runPage.Truncated);
    }

    private async Task<(IReadOnlyList<BehaviourRunRow> Rows, int TotalInWindow, bool Truncated)> BuildBehaviourRunRowsAsync(
        IReadOnlyList<TuflowBehaviourRun> inWindow,
        DateTimeOffset now,
        CancellationToken ct)
    {
        var total = inWindow.Count;
        if (total == 0)
            return ([], 0, false);

        var ordered = inWindow
            .OrderByDescending(r => r.DetectedStartUtc)
            .Take(BehaviourRunsPageSize)
            .ToList();
        var truncated = total > ordered.Count;

        var ids = ordered.Select(r => r.Id).ToList();
        var sampleRows = await db.TuflowBehaviourSamples.AsNoTracking()
            .Where(s => ids.Contains(s.BehaviourRunId))
            .Select(s => new
            {
                s.BehaviourRunId,
                s.SampledAtUtc,
                s.ProcessGpuPercent,
                s.MachineGpuPercent,
                s.ProcessCpuPercent,
                s.MachineCpuPercent,
                s.MachineRamUsedMb,
                s.ProcessDiskWriteMBps,
                s.MachineDiskWriteMBps,
                s.NetworkOutMBps
            })
            .ToListAsync(ct);

        var samplesByRun = sampleRows
            .GroupBy(s => s.BehaviourRunId)
            .ToDictionary(g => g.Key, g => g.ToList());

        var rows = new List<BehaviourRunRow>(ordered.Count);
        foreach (var r in ordered)
        {
            var start = r.DetectedStartUtc!.Value;
            var end = r.DetectedEndUtc;
            var isOpen = end is null
                || r.State is TuflowBehaviourStates.Active or TuflowBehaviourStates.Watching;
            var duration = (end ?? now) - start;
            if (duration < TimeSpan.Zero)
                duration = TimeSpan.Zero;

            var avgGpu = r.SampleCount == 0 || r.SumGpuPercent is null
                ? null
                : r.SumGpuPercent / r.SampleCount;
            var highGpuSeconds = SumHighGpuSeconds(r.GpuPercentHistogramJson);

            IReadOnlyList<MetricSeriesPoint> series = [];
            if (samplesByRun.TryGetValue(r.Id, out var samples))
            {
                var endClip = end ?? now;
                var ramTotalMb = r.Machine.HardwareRamGb is > 0
                    ? r.Machine.HardwareRamGb.Value * 1024.0
                    : (double?)null;
                series = BuildMetricSeries(
                    samples
                        .Where(s => s.SampledAtUtc >= start && s.SampledAtUtc <= endClip)
                        .OrderBy(s => s.SampledAtUtc)
                        .Select(s =>
                        {
                            var gpu = s.ProcessGpuPercent ?? s.MachineGpuPercent;
                            var cpu = s.ProcessCpuPercent ?? s.MachineCpuPercent;
                            double? ram = null;
                            if (ramTotalMb is { } total && s.MachineRamUsedMb is { } used && total > 0)
                                ram = Math.Clamp(100.0 * used / total, 0, 100);
                            var diskW = s.ProcessDiskWriteMBps ?? s.MachineDiskWriteMBps;
                            return new MetricSeriesPoint(
                                s.SampledAtUtc,
                                gpu,
                                cpu,
                                ram,
                                diskW,
                                s.NetworkOutMBps);
                        })
                        .Where(p => p.GpuPercent is not null || p.CpuPercent is not null || p.RamPercent is not null
                                    || p.DiskWriteMBps is not null || p.NetworkOutMBps is not null)
                        .ToList());
            }

            var machineLabel = string.IsNullOrWhiteSpace(r.Machine.FriendlyName)
                ? r.Machine.Hostname
                : r.Machine.FriendlyName.Trim();

            rows.Add(new BehaviourRunRow(
                machineLabel,
                string.IsNullOrWhiteSpace(r.Username) ? null : r.Username.Trim(),
                start,
                end,
                isOpen,
                duration,
                avgGpu,
                r.PeakGpuPercent,
                highGpuSeconds,
                series));
        }

        return (rows, total, truncated);
    }

    /// <summary>Seconds spent in GPU histogram buckets at or above 50% (process GPU).</summary>
    private static double SumHighGpuSeconds(string? histogramJson)
    {
        var hist = DeserializeHistogram(histogramJson);
        double sum = 0;
        foreach (var (bucket, seconds) in hist)
        {
            if (bucket is "50-60" or "60-70" or "70-80" or "80-90" or "90-100" or "100+")
                sum += seconds;
        }

        return sum;
    }

    /// <summary>Keep full sample resolution for zoomable charts; only thin extreme outliers.</summary>
    private static IReadOnlyList<MetricSeriesPoint> BuildMetricSeries(IReadOnlyList<MetricSeriesPoint> points)
    {
        if (points.Count == 0)
            return [];
        if (points.Count <= GpuSeriesMaxPoints)
            return points.ToList();

        var result = new MetricSeriesPoint[GpuSeriesMaxPoints];
        var last = points.Count - 1;
        for (var i = 0; i < GpuSeriesMaxPoints; i++)
        {
            var idx = (int)Math.Round((double)i * last / (GpuSeriesMaxPoints - 1));
            result[i] = points[idx];
        }

        return result;
    }

    private static IReadOnlyList<BehaviourDimensionRow> BuildDimensionRows(
        IReadOnlyList<TuflowBehaviourRun> inWindow,
        IReadOnlyList<(TuflowBehaviourRun r, TimeSpan Duration)> completed,
        Func<TuflowBehaviourRun, string> groupKey,
        Func<TuflowBehaviourRun, string> displayName)
    {
        var completedByKey = completed
            .GroupBy(c => groupKey(c.r), StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.Select(x => x.Duration).ToList(), StringComparer.OrdinalIgnoreCase);

        return inWindow
            .GroupBy(groupKey, StringComparer.OrdinalIgnoreCase)
            .Select(g =>
            {
                var label = displayName(g.First());
                var hours = new int[24];
                foreach (var r in g)
                {
                    var h = r.DetectedStartUtc!.Value.ToLocalTime().Hour;
                    hours[h]++;
                }

                completedByKey.TryGetValue(g.Key, out var durations);
                durations ??= [];
                var open = g.Count(r =>
                    r.State is TuflowBehaviourStates.Active or TuflowBehaviourStates.Watching
                    || (r.State == TuflowBehaviourStates.Ended && r.DetectedEndUtc is null));

                return new BehaviourDimensionRow(
                    label,
                    g.Count(),
                    durations.Count,
                    open,
                    AverageDuration(durations),
                    MedianDuration(durations),
                    PeakStartHour(hours));
            })
            .OrderByDescending(r => r.RunCount)
            .ThenBy(r => r.Label, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static TimeSpan? AverageDuration(IEnumerable<TimeSpan> durations)
    {
        var list = durations.ToList();
        if (list.Count == 0)
            return null;
        return TimeSpan.FromTicks((long)list.Average(d => d.Ticks));
    }

    private static TimeSpan? MedianDuration(IEnumerable<TimeSpan> durations)
    {
        var list = durations.OrderBy(d => d).ToList();
        if (list.Count == 0)
            return null;
        var mid = list.Count / 2;
        return list.Count % 2 == 0
            ? TimeSpan.FromTicks((list[mid - 1].Ticks + list[mid].Ticks) / 2)
            : list[mid];
    }

    private static int? PeakStartHour(int[] hourCounts)
    {
        var max = hourCounts.Max();
        if (max <= 0)
            return null;
        return Array.IndexOf(hourCounts, max);
    }

    public async Task<int> PurgeOlderThanAsync(int retentionDays, CancellationToken ct)
    {
        var cutoff = DateTimeOffset.UtcNow.AddDays(-Math.Max(1, retentionDays));
        // SQLite EF: DateTimeOffset inequality is not reliably translatable — filter in memory.
        var ended = await db.TuflowBehaviourRuns
            .Where(r => r.State == TuflowBehaviourStates.Ended && r.DetectedEndUtc != null)
            .ToListAsync(ct);
        var old = ended.Where(r => r.DetectedEndUtc < cutoff).ToList();
        if (old.Count == 0)
            return 0;

        db.TuflowBehaviourRuns.RemoveRange(old);
        await db.SaveChangesAsync(ct);
        return old.Count;
    }

    public sealed record BehaviourDisplay(
        string State,
        DateTimeOffset? DetectedStartUtc,
        DateTimeOffset? DetectedEndUtc,
        double? RampUpSeconds,
        string? Username = null);

    public sealed record TuflowBehaviourListRow(
        string RunId,
        string Hostname,
        string? FriendlyName,
        string? Username,
        string State,
        DateTimeOffset? DetectedStartUtc,
        DateTimeOffset? DetectedEndUtc,
        double? RampUpSeconds,
        double? RampDownSeconds,
        double? PeakCpuPercent,
        double? PeakGpuPercent,
        double? AvgCpuPercent,
        double? AvgGpuPercent,
        string? HardwareCpu,
        string? HardwareGpu,
        int SampleCount,
        string? LinkedTuflowRunId)
    {
        public TimeSpan? Duration =>
            DetectedStartUtc is { } s
                ? (DetectedEndUtc ?? DateTimeOffset.UtcNow) - s
                : null;
    }

    public sealed record TuflowBehaviourAnalytics(
        int Days,
        DateTimeOffset FromUtc,
        int ConfirmedStarts,
        int CompletedRuns,
        int OpenRuns,
        TimeSpan? AvgCompletedDuration,
        TimeSpan? MedianCompletedDuration,
        int? PeakStartHourLocal,
        IReadOnlyList<HourBucket> StartHourHistogram,
        IReadOnlyList<BehaviourDimensionRow> ByUser,
        IReadOnlyList<BehaviourDimensionRow> ByMachine,
        IReadOnlyList<BehaviourRunRow> Runs,
        int RunsTotalInWindow,
        bool RunsTruncated);

    public sealed record HourBucket(int HourLocal, int Count);

    public sealed record BehaviourDimensionRow(
        string Label,
        int RunCount,
        int CompletedCount,
        int OpenCount,
        TimeSpan? AvgCompletedDuration,
        TimeSpan? MedianCompletedDuration,
        int? PeakStartHourLocal);

    /// <summary>One confirmed behaviour run for the Runs card (with timed multi-metric samples for zoomable charts).</summary>
    public sealed record BehaviourRunRow(
        string MachineLabel,
        string? Username,
        DateTimeOffset DetectedStartUtc,
        DateTimeOffset? DetectedEndUtc,
        bool IsOpen,
        TimeSpan Duration,
        double? AvgGpuPercent,
        double? PeakGpuPercent,
        double HighGpuSeconds,
        IReadOnlyList<MetricSeriesPoint> GpuSeries);

    /// <summary>GPU/CPU/RAM% and Disk W / Net Tx at a sample time.</summary>
    public sealed record MetricSeriesPoint(
        DateTimeOffset SampledAtUtc,
        double? GpuPercent,
        double? CpuPercent = null,
        double? RamPercent = null,
        double? DiskWriteMBps = null,
        double? NetworkOutMBps = null);
}
