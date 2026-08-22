using System.Diagnostics;
using System.ServiceProcess;
using Heimdall.Api.Data;
using Heimdall.Shared;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Heimdall.Api.Services;

public sealed class ApiHealthService(
    HeimdallDbContext db,
    IOptions<ApiHealthOptions> options)
{
    private static readonly DateTimeOffset ProcessStartedUtc = DateTimeOffset.UtcNow;
    public static DateTimeOffset ProcessStartedUtcForHealth => ProcessStartedUtc;
    private static int _openIncidentId;
    private static readonly object IncidentGate = new();

    public async Task RecordProbeAsync(bool ok, int? dbLatencyMs, string? detail, CancellationToken ct)
    {
        var now = DateTimeOffset.UtcNow;
        db.ApiHealthSamples.Add(new ApiHealthSample
        {
            SampledAtUtc = now,
            Ok = ok,
            DbLatencyMs = dbLatencyMs,
            Detail = detail
        });
        await db.SaveChangesAsync(ct);
        await UpdateIncidentStateAsync(ok, "probe", detail, now, ct);
    }

    public async Task RecordHealEventAsync(string action, string? source, string? detail, CancellationToken ct)
    {
        var now = DateTimeOffset.UtcNow;
        var src = string.IsNullOrWhiteSpace(source) ? "api-heal" : source.Trim();
        var text = $"{action}{(string.IsNullOrWhiteSpace(detail) ? "" : ": " + detail)}";

        if (action.Contains("begin", StringComparison.OrdinalIgnoreCase))
        {
            lock (IncidentGate)
            {
                if (_openIncidentId <= 0)
                {
                    var row = new ApiHealthIncident
                    {
                        StartedUtc = now,
                        Source = src,
                        Detail = text
                    };
                    db.ApiHealthIncidents.Add(row);
                    db.SaveChanges();
                    _openIncidentId = (int)row.Id;
                }
            }
            OpsFileLog.Write("api-heal", text, src);
            return;
        }

        if (action.Contains("ok", StringComparison.OrdinalIgnoreCase))
        {
            await CloseOpenIncidentAsync(now, text, ct);
            OpsFileLog.Write("api-heal", text, src);
            return;
        }

        OpsFileLog.Write("api-heal", text, src);
        await db.SaveChangesAsync(ct);
    }

    public async Task CloseOrphanIncidentsOnStartupAsync(CancellationToken ct)
    {
        var now = DateTimeOffset.UtcNow;
        var open = await db.ApiHealthIncidents
            .Where(i => i.EndedUtc == null)
            .ToListAsync(ct);
        foreach (var row in open)
        {
            row.EndedUtc = now;
            row.Detail = AppendDetail(row.Detail, "closed on API startup");
        }

        await db.SaveChangesAsync(ct);
        lock (IncidentGate) { _openIncidentId = 0; }
        OpsFileLog.Write("api-startup", $"process started pid {Environment.ProcessId}");
    }

    public async Task PurgeOldAsync(CancellationToken ct)
    {
        var opts = options.Value;
        var sampleCutoff = DateTimeOffset.UtcNow.AddDays(-Math.Max(1, opts.SampleRetentionDays));
        var incidentCutoff = DateTimeOffset.UtcNow.AddDays(-Math.Max(7, opts.IncidentRetentionDays));
        await db.ApiHealthSamples.Where(s => s.SampledAtUtc < sampleCutoff).ExecuteDeleteAsync(ct);
        await db.ApiHealthIncidents.Where(i => i.StartedUtc < incidentCutoff).ExecuteDeleteAsync(ct);
    }

    public async Task<ApiHealthDashboard> BuildDashboardAsync(CancellationToken ct)
    {
        var opts = options.Value;
        var now = DateTimeOffset.UtcNow;
        var lookback7 = now.AddDays(-7);
        var lookbackGap = now.AddHours(-Math.Max(1, opts.GapLookbackHours));
        var gapThreshold = TimeSpan.FromMinutes(Math.Max(2, opts.FleetGapAlertMinutes));

        var samples7 = await db.ApiHealthSamples.AsNoTracking()
            .Where(s => s.SampledAtUtc >= lookback7)
            .Select(s => new { s.Ok, s.SampledAtUtc, s.DbLatencyMs })
            .ToListAsync(ct);

        var uptime7 = samples7.Count == 0
            ? 100.0
            : 100.0 * samples7.Count(s => s.Ok) / samples7.Count;

        var lastFleetIngest = await db.FleetMetricSnapshots.AsNoTracking()
            .MaxAsync(s => (DateTimeOffset?)s.SampledAtUtc, ct);

        var lastIngest = await db.Machines.AsNoTracking()
            .MaxAsync(m => (DateTimeOffset?)m.LastSeenUtc, ct);

        var incidents = await db.ApiHealthIncidents.AsNoTracking()
            .OrderByDescending(i => i.StartedUtc)
            .Take(25)
            .ToListAsync(ct);

        var machines = await db.Machines.AsNoTracking()
            .Select(m => new { m.Id, m.Hostname, m.FriendlyName, m.LastSeenUtc })
            .ToListAsync(ct);

        var onlineCutoff = now.Add(-RemoteMachineService.OnlineWindow);
        var recentMachines = machines.Where(m => m.LastSeenUtc >= lookbackGap).ToList();
        var machineIds = recentMachines.Select(m => m.Id).ToList();

        var snapshots = machineIds.Count == 0
            ? []
            : await db.FleetMetricSnapshots.AsNoTracking()
                .Where(s => machineIds.Contains(s.MachineId) && s.SampledAtUtc >= lookbackGap)
                .OrderBy(s => s.MachineId)
                .ThenBy(s => s.SampledAtUtc)
                .Select(s => new { s.MachineId, s.SampledAtUtc, s.TuflowRunning })
                .ToListAsync(ct);

        var snapsByMachine = snapshots.GroupBy(s => s.MachineId).ToDictionary(g => g.Key, g => g.ToList());

        var fleetGaps = new List<FleetGapRow>();
        foreach (var m in recentMachines)
        {
            if (!snapsByMachine.TryGetValue(m.Id, out var snaps) || snaps.Count < 2)
            {
                if (m.LastSeenUtc >= onlineCutoff)
                {
                    fleetGaps.Add(new FleetGapRow(
                        m.Hostname,
                        m.FriendlyName,
                        m.LastSeenUtc,
                        null,
                        null,
                        "No fleet snapshots in lookback"));
                }
                continue;
            }

            var maxGap = TimeSpan.Zero;
            DateTimeOffset? gapAt = null;
            for (var i = 1; i < snaps.Count; i++)
            {
                var gap = snaps[i].SampledAtUtc - snaps[i - 1].SampledAtUtc;
                if (gap > maxGap)
                {
                    maxGap = gap;
                    gapAt = snaps[i].SampledAtUtc;
                }
            }

            if (maxGap >= gapThreshold)
            {
                fleetGaps.Add(new FleetGapRow(
                    m.Hostname,
                    m.FriendlyName,
                    m.LastSeenUtc,
                    maxGap,
                    gapAt,
                    null));
            }
        }

        fleetGaps = fleetGaps
            .OrderByDescending(r => r.MaxGap ?? TimeSpan.MaxValue)
            .Take(50)
            .ToList();

        var tuflowRunningHosts = snapshots
            .Where(s => s.TuflowRunning)
            .Select(s => s.MachineId)
            .Distinct()
            .ToHashSet();

        var behaviourRuns = machineIds.Count == 0
            ? []
            : await db.TuflowBehaviourRuns.AsNoTracking()
                .Where(r => machineIds.Contains(r.MachineId) && r.ProcessFirstSeenUtc >= lookbackGap)
                .Select(r => new { r.MachineId, r.State, r.DetectedStartUtc, r.DetectedEndUtc, r.SampleCount })
                .ToListAsync(ct);

        var runsByMachine = behaviourRuns.GroupBy(r => r.MachineId).ToDictionary(g => g.Key, g => g.ToList());

        var tuflowGaps = new List<TuflowGapRow>();
        foreach (var machineId in tuflowRunningHosts)
        {
            var m = recentMachines.FirstOrDefault(x => x.Id == machineId);
            if (m is null) continue;

            runsByMachine.TryGetValue(machineId, out var runs);
            var runningSnaps = snapsByMachine.GetValueOrDefault(machineId, [])
                .Where(s => s.TuflowRunning)
                .ToList();
            if (runningSnaps.Count == 0) continue;

            var snapMinutes = EstimateTuflowMinutes(runningSnaps.Count);
            var runMinutes = runs?.Sum(r => BehaviourRunMinutes(r.DetectedStartUtc, r.DetectedEndUtc, now)) ?? 0;

            if (runs is null || runs.Count == 0 || snapMinutes > runMinutes + 30)
            {
                tuflowGaps.Add(new TuflowGapRow(
                    m.Hostname,
                    m.FriendlyName,
                    snapMinutes,
                    runMinutes,
                    runs?.Count ?? 0,
                    runs is null || runs.Count == 0
                        ? "TuflowRunning snapshots but no behaviour runs"
                        : "Behaviour run duration shorter than fleet TUFLOW time"));
            }
        }

        tuflowGaps = tuflowGaps.OrderByDescending(r => r.FleetTuflowMinutes - r.BehaviourMinutes).Take(40).ToList();

        var avgDbMs = samples7.Where(s => s.DbLatencyMs is > 0).Select(s => s.DbLatencyMs!.Value).DefaultIfEmpty(0);
        var dbLatencyAvg = avgDbMs.Any() ? (int)avgDbMs.Average() : (int?)null;

        return new ApiHealthDashboard(
            Environment.MachineName,
            ProcessStartedUtc,
            now - ProcessStartedUtc,
            uptime7,
            samples7.Count,
            dbLatencyAvg,
            lastFleetIngest,
            lastIngest,
            now - (lastFleetIngest ?? DateTimeOffset.MinValue),
            GetWindowsServiceStatus(),
            IsApiHealTaskRegistered(),
            ReadRecentHealLogLines(12),
            incidents,
            fleetGaps,
            tuflowGaps,
            opts.FleetGapAlertMinutes,
            opts.GapLookbackHours,
            HeimdallLogPaths.ApiHealLogsDir);
    }

    private async Task UpdateIncidentStateAsync(bool ok, string source, string? detail, DateTimeOffset now, CancellationToken ct)
    {
        if (!ok)
        {
            lock (IncidentGate)
            {
                if (_openIncidentId > 0) return;
                var row = new ApiHealthIncident
                {
                    StartedUtc = now,
                    Source = source,
                    Detail = detail
                };
                db.ApiHealthIncidents.Add(row);
                db.SaveChanges();
                _openIncidentId = (int)row.Id;
            }
            return;
        }

        await CloseOpenIncidentAsync(now, detail, ct);
    }

    private async Task CloseOpenIncidentAsync(DateTimeOffset endedUtc, string? detail, CancellationToken ct)
    {
        int id;
        lock (IncidentGate)
        {
            id = _openIncidentId;
            _openIncidentId = 0;
        }

        if (id <= 0)
        {
            var open = await db.ApiHealthIncidents
                .Where(i => i.EndedUtc == null)
                .OrderByDescending(i => i.StartedUtc)
                .FirstOrDefaultAsync(ct);
            if (open is null) return;
            open.EndedUtc = endedUtc;
            open.Detail = AppendDetail(open.Detail, detail);
            await db.SaveChangesAsync(ct);
            return;
        }

        var row = await db.ApiHealthIncidents.FirstOrDefaultAsync(i => i.Id == id, ct);
        if (row is null) return;
        row.EndedUtc = endedUtc;
        row.Detail = AppendDetail(row.Detail, detail);
        await db.SaveChangesAsync(ct);
    }

    private static string? AppendDetail(string? existing, string? extra)
    {
        if (string.IsNullOrWhiteSpace(extra)) return existing;
        return string.IsNullOrWhiteSpace(existing) ? extra : existing + " | " + extra;
    }

    private static double EstimateTuflowMinutes(int runningSnapCount) =>
        runningSnapCount * 0.5;

    private static double BehaviourRunMinutes(DateTimeOffset? start, DateTimeOffset? end, DateTimeOffset now)
    {
        if (start is null) return 0;
        var finish = end ?? now;
        return Math.Max(0, (finish - start.Value).TotalMinutes);
    }

    private static string? GetWindowsServiceStatus()
    {
        if (!OperatingSystem.IsWindows()) return "n/a (not Windows)";
        try
        {
            using var svc = new ServiceController("HeimdallApi");
            return svc.Status.ToString();
        }
        catch (Exception ex)
        {
            return "unknown: " + ex.Message;
        }
    }

    private static bool? IsApiHealTaskRegistered()
    {
        if (!OperatingSystem.IsWindows()) return null;
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = "schtasks.exe",
                Arguments = "/Query /TN HeimdallApiHeal",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };
            using var proc = Process.Start(psi);
            if (proc is null) return false;
            proc.WaitForExit(5000);
            return proc.ExitCode == 0;
        }
        catch
        {
            return false;
        }
    }

    private static IReadOnlyList<string> ReadRecentHealLogLines(int maxLines)
    {
        try
        {
            var dir = HeimdallLogPaths.ApiHealLogsDir;
            if (!Directory.Exists(dir)) return [];
            var file = Directory.GetFiles(dir, "api-heal-*.log")
                .OrderByDescending(File.GetLastWriteTimeUtc)
                .FirstOrDefault();
            if (file is null) return [];
            var lines = File.ReadAllLines(file);
            return lines.Length <= maxLines ? lines : lines[^maxLines..];
        }
        catch
        {
            return [];
        }
    }

    public sealed record ApiHealthDashboard(
        string Hostname,
        DateTimeOffset ProcessStartedUtc,
        TimeSpan ProcessUptime,
        double UptimePercent7d,
        int SampleCount7d,
        int? AvgDbLatencyMs,
        DateTimeOffset? LastFleetIngestUtc,
        DateTimeOffset? LastAgentContactUtc,
        TimeSpan FleetIngestLag,
        string? WindowsServiceStatus,
        bool? ApiHealTaskRegistered,
        IReadOnlyList<string> RecentHealLogLines,
        IReadOnlyList<ApiHealthIncident> RecentIncidents,
        IReadOnlyList<FleetGapRow> FleetGaps,
        IReadOnlyList<TuflowGapRow> TuflowGaps,
        int FleetGapAlertMinutes,
        int GapLookbackHours,
        string ApiHealLogsDir);

    public sealed record FleetGapRow(
        string Hostname,
        string? FriendlyName,
        DateTimeOffset LastSeenUtc,
        TimeSpan? MaxGap,
        DateTimeOffset? MaxGapAtUtc,
        string? Note);

    public sealed record TuflowGapRow(
        string Hostname,
        string? FriendlyName,
        double FleetTuflowMinutes,
        double BehaviourMinutes,
        int BehaviourRunCount,
        string Reason);
}

public sealed class ApiHealthHealEventDto
{
    public string? Action { get; set; }
    public string? Source { get; set; }
    public string? Detail { get; set; }
    public DateTimeOffset? Utc { get; set; }
}
