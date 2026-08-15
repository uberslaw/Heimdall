using System.Text.Json;
using Heimdall.Api.Data;
using Heimdall.Shared.Contracts;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Heimdall.Api.Services;

public sealed class TuflowBehaviourOptions
{
    public const string SectionName = "Heimdall:TuflowBehaviour";

    public double CpuPercentThreshold { get; set; } = TuflowBehaviourDefaults.CpuPercentThreshold;
    public int ConfirmIntervals { get; set; } = TuflowBehaviourDefaults.ConfirmIntervals;
    public int SampleRetentionDays { get; set; } = TuflowBehaviourDefaults.SampleRetentionDays;
    public int RecentStopDisplayHours { get; set; } = TuflowBehaviourDefaults.RecentStopDisplayHours;
    public bool Enabled { get; set; } = true;
}

/// <summary>
/// Detects TUFLOW run start/stop from fleet process CPU samples and persists behaviour analytics.
/// Start: process CPU &gt; threshold for ConfirmIntervals consecutive samples → DetectedStartUtc = first of that pair.
/// Stop: CPU ≤ threshold or process gone for ConfirmIntervals → DetectedEndUtc = first of that low/gone pair.
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

        var threshold = Math.Max(0, opts.CpuPercentThreshold);
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
        var elevated = dto.TuflowRunning && processCpu > threshold;
        var processGpu = SanitizeGpu(dto.ProcessGpuPercent);

        if (open is null)
        {
            if (!dto.TuflowRunning)
                return;

            open = OpenWatch(machine, dto, sampledAt);
            open.ProcessFirstSeenUtc = await ResolveStreakStartUtcAsync(machine.Id, sampledAt, ct);
            db.TuflowBehaviourRuns.Add(open);
        }

        if (!string.IsNullOrWhiteSpace(dto.Username))
            open.Username = dto.Username.Trim();

        open.UpdatedUtc = sampledAt;
        AppendSample(open, dto, sampledAt, intervalSec, processGpu);
        MergeGpuEngines(open, dto.GpuEngineSightings);
        UpdatePeaksAndHistogram(open, processCpu, processGpu, intervalSec);

        // Refresh first-seen from fleet streak while still watching (recovers mid-job after API outage).
        if (open.State == TuflowBehaviourStates.Watching && open.DetectedStartUtc is null)
        {
            var streakStart = await ResolveStreakStartUtcAsync(machine.Id, sampledAt, ct);
            if (streakStart < open.ProcessFirstSeenUtc)
                open.ProcessFirstSeenUtc = streakStart;
        }

        if (open.State == TuflowBehaviourStates.Ended)
        {
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
            Username = string.IsNullOrWhiteSpace(dto.Username) ? null : dto.Username.Trim(),
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

    // Mirror agent MaxSaneGpuPercent without referencing Agent assembly.
    private const double ResourceMetricsCollectorMaxGpu = 1000.0;

    public async Task<IReadOnlyDictionary<int, BehaviourDisplay>> GetDisplayByMachineAsync(
        IReadOnlyList<int> machineIds,
        CancellationToken ct)
    {
        if (machineIds.Count == 0)
            return new Dictionary<int, BehaviourDisplay>();

        var opts = options.Value;
        var recentCutoff = DateTimeOffset.UtcNow.AddHours(-Math.Max(1, opts.RecentStopDisplayHours));

        // SQLite EF cannot translate DateTimeOffset comparisons in complex OR filters — filter Ended in memory.
        var candidates = await db.TuflowBehaviourRuns.AsNoTracking()
            .Where(r => machineIds.Contains(r.MachineId)
                && (r.State == TuflowBehaviourStates.Active
                    || r.State == TuflowBehaviourStates.Watching
                    || r.State == TuflowBehaviourStates.Ended))
            .ToListAsync(ct);

        var openOrRecent = candidates
            .Where(r => r.State != TuflowBehaviourStates.Ended
                || (r.DetectedEndUtc is { } end && end >= recentCutoff))
            .ToList();

        var result = new Dictionary<int, BehaviourDisplay>();
        foreach (var group in openOrRecent.GroupBy(r => r.MachineId))
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
                    active.RampUpSeconds);
                continue;
            }

            // Open watch (process seen, not yet CPU-confirmed) → show process-first-seen as start
            // so Active column is never stuck on literal "Active" while a run row is open.
            var watching = group.FirstOrDefault(r => r.State == TuflowBehaviourStates.Watching);
            if (watching is not null)
            {
                var start = watching.CandidateStartUtc ?? watching.ProcessFirstSeenUtc;
                result[group.Key] = new BehaviourDisplay(
                    TuflowBehaviourStates.Watching,
                    start,
                    null,
                    watching.RampUpSeconds);
                continue;
            }

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
                    ended.RampUpSeconds);
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
        double? RampUpSeconds);

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
}
