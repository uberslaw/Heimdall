using Heimdall.Api.Data;
using Heimdall.Shared.Contracts;
using Microsoft.EntityFrameworkCore;

namespace Heimdall.Api.Services;

/// <summary>
/// Machines-list utilisation windows: Active (non-disconnected sessions), Passive/hardware from
/// FleetMetricSnapshots when present, Free = remainder of calendar window.
/// </summary>
public class MachineUtilisationService(HeimdallDbContext db)
{
    public static readonly TimeSpan SampleInterval = TimeSpan.FromSeconds(30);

    public static IReadOnlyList<(string Key, string Label)> PeriodOptions { get; } =
    [
        ("today", "Today"),
        ("24h", "24h"),
        ("5d", "5 day"),
        ("7d", "7 day"),
        ("30d", "30 day"),
        ("all", "All time"),
    ];

    public static string NormalizePeriod(string? period)
    {
        var key = (period ?? "7d").Trim().ToLowerInvariant();
        return PeriodOptions.Any(p => p.Key == key) ? key : "7d";
    }

    public static string NextPeriod(string? period)
    {
        var key = NormalizePeriod(period);
        var idx = PeriodOptions.ToList().FindIndex(p => p.Key == key);
        return PeriodOptions[(idx + 1) % PeriodOptions.Count].Key;
    }

    /// <summary>UTC window. Today = UTC midnight → now. All time = first session/snapshot → now (caller may pass machine-specific earliest).</summary>
    public static (DateTimeOffset From, DateTimeOffset To, double WindowSeconds) ResolveWindow(string period, DateTimeOffset now)
    {
        var key = NormalizePeriod(period);
        var to = now;
        DateTimeOffset from = key switch
        {
            "today" => new DateTimeOffset(now.UtcDateTime.Date, TimeSpan.Zero),
            "24h" => now.AddHours(-24),
            "5d" => now.AddDays(-5),
            "7d" => now.AddDays(-7),
            "30d" => now.AddDays(-30),
            "all" => now.AddYears(-20),
            _ => now.AddDays(-7)
        };
        var seconds = Math.Max(1, (to - from).TotalSeconds);
        return (from, to, seconds);
    }

    public async Task<IReadOnlyDictionary<int, MachineUtilRow>> ComputeAsync(
        IReadOnlyList<int> machineIds,
        string period,
        CancellationToken ct)
    {
        if (machineIds.Count == 0)
            return new Dictionary<int, MachineUtilRow>();

        var now = DateTimeOffset.UtcNow;
        var (from, to, windowSeconds) = ResolveWindow(period, now);
        var ids = machineIds as List<int> ?? machineIds.ToList();

        var sessions = (await db.Sessions.AsNoTracking()
                .Where(s => ids.Contains(s.MachineId))
                .ToListAsync(ct))
            .Where(s => s.StartedAtUtc < to && (s.EndedAtUtc is null || s.EndedAtUtc >= from))
            .ToList();

        var snaps = await FleetSnapshotQuery.LoadForMachinesAsync(db, ids, from, to, ct);

        var byMachineSessions = sessions.GroupBy(s => s.MachineId).ToDictionary(g => g.Key, g => g.ToList());
        var byMachineSnaps = snaps.GroupBy(s => s.MachineId).ToDictionary(g => g.Key, g => g.ToList());

        var result = new Dictionary<int, MachineUtilRow>();
        foreach (var id in ids)
        {
            byMachineSessions.TryGetValue(id, out var ms);
            ms ??= [];
            byMachineSnaps.TryGetValue(id, out var mSnaps);
            mSnaps ??= [];

            var activeSec = SumActiveSessionSeconds(ms, from, to);
            var hasSamples = mSnaps.Count > 0;
            double? passiveSec = hasSamples ? EstimatePassiveSeconds(mSnaps, ms, from, to) : null;

            var passiveForFree = passiveSec ?? 0;
            var freeSec = Math.Max(0, windowSeconds - activeSec - passiveForFree);
            var activePct = Math.Clamp(activeSec / windowSeconds * 100.0, 0, 100);
            double? passivePct = passiveSec is null
                ? null
                : Math.Clamp(passiveSec.Value / windowSeconds * 100.0, 0, 100);
            var freePct = Math.Clamp(freeSec / windowSeconds * 100.0, 0, 100);

            double? gpuH = null, cpuH = null, dr = null, dw = null, ntx = null, nrx = null;
            if (hasSamples)
            {
                var hw = IntegrateHardware(mSnaps);
                gpuH = hw.GpuHours;
                cpuH = hw.CpuHours;
                dr = hw.DiskReadBytes;
                dw = hw.DiskWriteBytes;
                ntx = hw.NetOutBytes;
                nrx = hw.NetInBytes;
            }

            result[id] = new MachineUtilRow(
                NormalizePeriod(period),
                activePct,
                passivePct,
                freePct,
                gpuH,
                cpuH,
                dr,
                dw,
                ntx,
                nrx,
                hasSamples);
        }

        return result;
    }

    private static double SumActiveSessionSeconds(
        IReadOnlyList<UserSession> sessions,
        DateTimeOffset from,
        DateTimeOffset to)
    {
        double total = 0;
        foreach (var s in sessions)
        {
            if (s.State == SessionState.Disconnected)
                continue;

            // Ended or Active (non-disconnected): count overlap, but for Disconnected segments
            // we only have state at last observe — treat Disconnected rows as not active.
            var start = s.StartedAtUtc < from ? from : s.StartedAtUtc;
            var end = s.EndedAtUtc ?? to;
            if (end > to) end = to;
            if (end <= start) continue;

            // If currently Disconnected, ActiveSeconds may still reflect prior active time —
            // prefer clamping to ActiveSeconds when Ended and Disconnected history is unknown.
            var overlap = (end - start).TotalSeconds;
            if (s.State == SessionState.Ended && s.ActiveSeconds > 0)
                overlap = Math.Min(overlap, s.ActiveSeconds);
            total += Math.Max(0, overlap);
        }
        return total;
    }

    /// <summary>
    /// Passive ≈ sample intervals where machine has a disconnected session overlapping the sample
    /// and CPU or GPU &gt; 5%. Without per-app attribution in fleet samples, this is a best-effort v1.
    /// </summary>
    private static double EstimatePassiveSeconds(
        IReadOnlyList<FleetMetricSnapshot> snaps,
        IReadOnlyList<UserSession> sessions,
        DateTimeOffset from,
        DateTimeOffset to)
    {
        var disconnected = sessions.Where(s => s.State == SessionState.Disconnected
            || (s.State == SessionState.Ended && s.DisconnectedSeconds > s.ActiveSeconds)).ToList();

        double passive = 0;
        for (var i = 0; i < snaps.Count; i++)
        {
            var s = snaps[i];
            double dt = SampleInterval.TotalSeconds;
            if (i + 1 < snaps.Count)
            {
                dt = (snaps[i + 1].SampledAtUtc - s.SampledAtUtc).TotalSeconds;
                if (dt <= 0 || dt > SampleInterval.TotalSeconds * 4)
                    dt = SampleInterval.TotalSeconds;
            }

            var busy = (s.CpuPercent ?? 0) > 5 || (s.GpuPercent ?? 0) > 5;
            if (!busy) continue;

            var at = s.SampledAtUtc;
            if (at < from || at >= to) continue;
            var hasDisc = disconnected.Any(d =>
                d.StartedAtUtc <= at && (d.EndedAtUtc is null || d.EndedAtUtc >= at));
            if (hasDisc)
                passive += dt;
        }
        return passive;
    }

    private static (double GpuHours, double CpuHours, double DiskReadBytes, double DiskWriteBytes, double NetInBytes, double NetOutBytes)
        IntegrateHardware(IReadOnlyList<FleetMetricSnapshot> snaps)
    {
        double gpuH = 0, cpuH = 0, dr = 0, dw = 0, ni = 0, no = 0;
        for (var i = 0; i < snaps.Count; i++)
        {
            var s = snaps[i];
            double dtSec = SampleInterval.TotalSeconds;
            if (i + 1 < snaps.Count)
            {
                dtSec = (snaps[i + 1].SampledAtUtc - s.SampledAtUtc).TotalSeconds;
                if (dtSec <= 0 || dtSec > SampleInterval.TotalSeconds * 4)
                    dtSec = SampleInterval.TotalSeconds;
            }
            var dtH = dtSec / 3600.0;
            if (s.GpuPercent is double g) gpuH += (g / 100.0) * dtH;
            if (s.CpuPercent is double c) cpuH += (c / 100.0) * dtH;
            // MBps × seconds → MB → bytes
            if (s.DiskReadMBps is double r) dr += r * dtSec * 1024 * 1024;
            if (s.DiskWriteMBps is double w) dw += w * dtSec * 1024 * 1024;
            if (s.NetworkInMBps is double nin) ni += nin * dtSec * 1024 * 1024;
            if (s.NetworkOutMBps is double nout) no += nout * dtSec * 1024 * 1024;
        }
        return (gpuH, cpuH, dr, dw, ni, no);
    }

    /// <summary>Compact 3-significant-digit byte volumes: 12K, 340M, 1.2G.</summary>
    public static string FormatBytesCompact(double? bytes)
    {
        if (bytes is null) return "—";
        var v = Math.Abs(bytes.Value);
        if (v < 1) return "0";
        string[] units = ["", "K", "M", "G", "T"];
        var u = 0;
        while (v >= 1000 && u < units.Length - 1)
        {
            v /= 1000;
            u++;
        }
        var text = v >= 100 ? v.ToString("0")
            : v >= 10 ? v.ToString("0.#")
            : v.ToString("0.##");
        return text + units[u];
    }

    public static string FormatHoursCompact(double? hours) =>
        hours is null ? "—" : hours.Value < 0.01 ? "0" : hours.Value.ToString("0.##");

    public static string FormatPct(double? pct) =>
        pct is null ? "—" : pct.Value.ToString("0") + "%";

    public static IReadOnlyList<(string Key, string Label)> MetricOptions { get; } =
    [
        ("active", "Active"),
        ("passive", "Passive"),
        ("free", "Free"),
        ("gpu", "GPU h"),
        ("cpu", "CPU h"),
        ("dr", "Dr"),
        ("dw", "Dw"),
        ("ntx", "NTx"),
        ("nrx", "NRx"),
    ];

    public static string NormalizeMetric(string? metric)
    {
        var key = (metric ?? "active").Trim().ToLowerInvariant();
        return MetricOptions.Any(m => m.Key == key) ? key : "active";
    }

    public static string MetricLabel(string metric) =>
        MetricOptions.First(m => m.Key == NormalizeMetric(metric)).Label;

    private static readonly System.Text.Json.JsonSerializerOptions ProcessJsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    /// <summary>Attribution for one machine × period × metric (Fleet Computers cell drill-down).</summary>
    public async Task<UtilDrilldown?> GetDrilldownAsync(
        int machineId,
        string period,
        string metric,
        CancellationToken ct)
    {
        var machine = await db.Machines.AsNoTracking().FirstOrDefaultAsync(m => m.Id == machineId, ct);
        if (machine is null)
            return null;

        period = NormalizePeriod(period);
        metric = NormalizeMetric(metric);
        var now = DateTimeOffset.UtcNow;
        var (from, to, windowSeconds) = ResolveWindow(period, now);

        var sessions = (await db.Sessions.AsNoTracking()
                .Where(s => s.MachineId == machineId)
                .ToListAsync(ct))
            .Where(s => s.StartedAtUtc < to && (s.EndedAtUtc is null || s.EndedAtUtc >= from))
            .ToList();

        var snaps = await FleetSnapshotQuery.LoadForMachinesAsync(db, [machineId], from, to, ct);
        var util = (await ComputeAsync([machineId], period, ct)).GetValueOrDefault(machineId)
            ?? new MachineUtilRow(period, 0, null, 100, null, null, null, null, null, null, false);

        var processRuns = await db.ProcessRuns.AsNoTracking()
            .Where(r => r.MachineId == machineId && r.StartedAtUtc < to && (r.EndedAtUtc == null || r.EndedAtUtc >= from))
            .ToListAsync(ct);

        return metric switch
        {
            "active" => BuildActiveDrilldown(machine, period, from, to, windowSeconds, util, sessions, processRuns),
            "passive" => BuildPassiveDrilldown(machine, period, from, to, windowSeconds, util, sessions, snaps),
            "free" => BuildFreeDrilldown(machine, period, util, sessions, snaps),
            "gpu" or "cpu" or "dr" or "dw" or "ntx" or "nrx" =>
                BuildHardwareDrilldown(machine, period, metric, from, to, util, sessions, snaps),
            _ => BuildActiveDrilldown(machine, period, from, to, windowSeconds, util, sessions, processRuns)
        };
    }

    private static UtilDrilldown BuildActiveDrilldown(
        Machine machine,
        string period,
        DateTimeOffset from,
        DateTimeOffset to,
        double windowSeconds,
        MachineUtilRow util,
        IReadOnlyList<UserSession> sessions,
        IReadOnlyList<ProcessRun> processRuns)
    {
        var byPerson = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
        var byDay = new Dictionary<DateOnly, double>();
        var sessionRows = new List<DrillSessionRow>();
        var processWeights = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);

        foreach (var s in sessions)
        {
            if (s.State == SessionState.Disconnected)
                continue;

            var start = s.StartedAtUtc < from ? from : s.StartedAtUtc;
            var end = s.EndedAtUtc ?? to;
            if (end > to) end = to;
            if (end <= start) continue;

            var overlap = (end - start).TotalSeconds;
            if (s.State == SessionState.Ended && s.ActiveSeconds > 0)
                overlap = Math.Min(overlap, s.ActiveSeconds);
            if (overlap <= 0) continue;

            var user = DisplayUser(s.Username);
            byPerson[user] = byPerson.GetValueOrDefault(user) + overlap;
            AccumulateByDay(byDay, start, end, overlap);

            var apps = processRuns
                .Where(r => UsersMatch(r.Username, s.Username)
                            && r.StartedAtUtc < end
                            && (r.EndedAtUtc is null || r.EndedAtUtc > start))
                .Select(r => r.ProcessName)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(n => n, StringComparer.OrdinalIgnoreCase)
                .Take(12)
                .ToList();

            foreach (var app in apps)
                processWeights[app] = processWeights.GetValueOrDefault(app) + overlap;

            sessionRows.Add(new DrillSessionRow(
                user,
                s.State.ToString(),
                start,
                s.EndedAtUtc,
                overlap,
                apps));
        }

        var hasProcessSamples = processWeights.Count > 0;
        return new UtilDrilldown(
            machine.Id,
            machine.Hostname,
            string.IsNullOrWhiteSpace(machine.FriendlyName) ? machine.Hostname : machine.FriendlyName!,
            period,
            PeriodOptions.First(p => p.Key == period).Label,
            "active",
            MetricLabel("active"),
            FormatPct(util.ActivePct),
            "Non-disconnected session time over the selected window. Processes listed are tracked app runs that overlapped each session (may not cover every process).",
            hasProcessSamples,
            hasProcessSamples
                ? null
                : "No overlapping tracked ProcessRun rows in this window — sessions show people and dates; process names appear once apps are tracked.",
            ToShareRows(byPerson, windowSeconds, isPercentOfWindow: true),
            ToShareRows(byDay.ToDictionary(kv => kv.Key.ToString("yyyy-MM-dd"), kv => kv.Value), windowSeconds, isPercentOfWindow: true),
            ToShareRows(processWeights, processWeights.Values.Sum(), isPercentOfWindow: false),
            sessionRows.OrderByDescending(r => r.SecondsInWindow).ToList());
    }

    private static UtilDrilldown BuildPassiveDrilldown(
        Machine machine,
        string period,
        DateTimeOffset from,
        DateTimeOffset to,
        double windowSeconds,
        MachineUtilRow util,
        IReadOnlyList<UserSession> sessions,
        IReadOnlyList<FleetMetricSnapshot> snaps)
    {
        var disconnected = sessions.Where(s => s.State == SessionState.Disconnected
            || (s.State == SessionState.Ended && s.DisconnectedSeconds > s.ActiveSeconds)).ToList();

        var byPerson = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
        var byDay = new Dictionary<DateOnly, double>();
        var processWeights = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
        var hasProcessJson = false;
        double passiveSec = 0;

        for (var i = 0; i < snaps.Count; i++)
        {
            var s = snaps[i];
            var dt = SampleDtSeconds(snaps, i);
            var busy = (s.CpuPercent ?? 0) > 5 || (s.GpuPercent ?? 0) > 5;
            if (!busy) continue;
            var at = s.SampledAtUtc;
            if (at < from || at >= to) continue;
            var disc = disconnected.FirstOrDefault(d =>
                d.StartedAtUtc <= at && (d.EndedAtUtc is null || d.EndedAtUtc >= at));
            if (disc is null) continue;

            passiveSec += dt;
            var user = DisplayUser(s.Username ?? disc.Username);
            byPerson[user] = byPerson.GetValueOrDefault(user) + dt;
            var day = DateOnly.FromDateTime(at.UtcDateTime);
            byDay[day] = byDay.GetValueOrDefault(day) + dt;

            var tops = DeserializeTops(s.TopCpuProcessesJson)
                .Concat(DeserializeTops(s.TopGpuProcessesJson))
                .GroupBy(p => p.ProcessName, StringComparer.OrdinalIgnoreCase)
                .Select(g => g.First());
            foreach (var p in tops)
            {
                hasProcessJson = true;
                processWeights[p.ProcessName] = processWeights.GetValueOrDefault(p.ProcessName) + dt * Math.Max(0.01, p.Value);
            }
        }

        return new UtilDrilldown(
            machine.Id,
            machine.Hostname,
            string.IsNullOrWhiteSpace(machine.FriendlyName) ? machine.Hostname : machine.FriendlyName!,
            period,
            PeriodOptions.First(p => p.Key == period).Label,
            "passive",
            MetricLabel("passive"),
            FormatPct(util.PassivePct),
            "Sample intervals (~30s) where a disconnected session overlaps and machine CPU or GPU > 5%. Top processes come from enriched fleet samples on those ticks.",
            hasProcessJson,
            hasProcessJson
                ? null
                : "No process samples in this period (upgrade client). People/dates still reflect disconnected sessions on busy ticks.",
            ToShareRows(byPerson, Math.Max(1, passiveSec), isPercentOfWindow: false),
            ToShareRows(byDay.ToDictionary(kv => kv.Key.ToString("yyyy-MM-dd"), kv => kv.Value), Math.Max(1, passiveSec), isPercentOfWindow: false),
            ToShareRows(processWeights, processWeights.Values.Sum(), isPercentOfWindow: false),
            []);
    }

    private static UtilDrilldown BuildFreeDrilldown(
        Machine machine,
        string period,
        MachineUtilRow util,
        IReadOnlyList<UserSession> sessions,
        IReadOnlyList<FleetMetricSnapshot> snaps)
    {
        var activeUsers = sessions
            .Where(s => s.State != SessionState.Disconnected)
            .Select(s => DisplayUser(s.Username))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(u => u, StringComparer.OrdinalIgnoreCase)
            .ToList();

        return new UtilDrilldown(
            machine.Id,
            machine.Hostname,
            string.IsNullOrWhiteSpace(machine.FriendlyName) ? machine.Hostname : machine.FriendlyName!,
            period,
            PeriodOptions.First(p => p.Key == period).Label,
            "free",
            MetricLabel("free"),
            FormatPct(util.FreePct),
            "Remainder of the calendar window after Active and Passive (Passive treated as 0% if unknown). Free is not process-attributed — open Active or Passive for people and processes.",
            false,
            snaps.Count == 0
                ? "No fleet samples in this window yet; Free is mostly the complement of Active session time."
                : null,
            activeUsers.Select(u => new DrillShareRow(u, 0, null)).ToList(),
            [],
            [],
            []);
    }

    private static UtilDrilldown BuildHardwareDrilldown(
        Machine machine,
        string period,
        string metric,
        DateTimeOffset from,
        DateTimeOffset to,
        MachineUtilRow util,
        IReadOnlyList<UserSession> sessions,
        IReadOnlyList<FleetMetricSnapshot> snaps)
    {
        var byPerson = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
        var byDay = new Dictionary<DateOnly, double>();
        var processWeights = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
        double total = 0;
        var hasProcessJson = false;

        for (var i = 0; i < snaps.Count; i++)
        {
            var s = snaps[i];
            if (s.SampledAtUtc < from || s.SampledAtUtc >= to) continue;
            var dtSec = SampleDtSeconds(snaps, i);
            var dtH = dtSec / 3600.0;

            double sampleContrib = metric switch
            {
                "gpu" => (s.GpuPercent ?? 0) / 100.0 * dtH,
                "cpu" => (s.CpuPercent ?? 0) / 100.0 * dtH,
                "dr" => (s.DiskReadMBps ?? 0) * dtSec * 1024 * 1024,
                "dw" => (s.DiskWriteMBps ?? 0) * dtSec * 1024 * 1024,
                "ntx" => (s.NetworkOutMBps ?? 0) * dtSec * 1024 * 1024,
                "nrx" => (s.NetworkInMBps ?? 0) * dtSec * 1024 * 1024,
                _ => 0
            };
            if (sampleContrib <= 0) continue;
            total += sampleContrib;

            var user = DisplayUser(s.Username
                ?? sessions.FirstOrDefault(sess =>
                    sess.StartedAtUtc <= s.SampledAtUtc
                    && (sess.EndedAtUtc is null || sess.EndedAtUtc >= s.SampledAtUtc))?.Username
                ?? "—");
            byPerson[user] = byPerson.GetValueOrDefault(user) + sampleContrib;
            var day = DateOnly.FromDateTime(s.SampledAtUtc.UtcDateTime);
            byDay[day] = byDay.GetValueOrDefault(day) + sampleContrib;

            if (metric is "ntx" or "nrx")
                continue; // no per-process network tops

            var tops = metric switch
            {
                "gpu" => DeserializeTops(s.TopGpuProcessesJson),
                "cpu" => DeserializeTops(s.TopCpuProcessesJson),
                "dr" => DeserializeTops(s.TopDiskReadProcessesJson),
                "dw" => DeserializeTops(s.TopDiskWriteProcessesJson),
                _ => []
            };
            if (tops.Count == 0) continue;
            hasProcessJson = true;

            double sampleAttributed = 0;
            foreach (var p in tops)
            {
                double piece = metric switch
                {
                    "gpu" or "cpu" => Math.Max(0, p.Value) / 100.0 * dtH,
                    "dr" or "dw" => Math.Max(0, p.Value) * dtSec, // Value is bytes/sec
                    _ => 0
                };
                if (piece <= 0) continue;
                processWeights[p.ProcessName] = processWeights.GetValueOrDefault(p.ProcessName) + piece;
                sampleAttributed += piece;
            }

            // If top-N sum exceeds machine gauge for this tick, scale this tick's process pieces.
            if (sampleAttributed > sampleContrib && sampleAttributed > 0)
            {
                var scale = sampleContrib / sampleAttributed;
                foreach (var p in tops)
                {
                    double piece = metric switch
                    {
                        "gpu" or "cpu" => Math.Max(0, p.Value) / 100.0 * dtH,
                        "dr" or "dw" => Math.Max(0, p.Value) * dtSec,
                        _ => 0
                    };
                    if (piece <= 0) continue;
                    processWeights[p.ProcessName] -= piece * (1 - scale);
                }
            }
        }

        if (total > 0 && metric is not ("ntx" or "nrx"))
        {
            var processSum = processWeights.Values.Sum();
            var other = Math.Max(0, total - Math.Min(processSum, total));
            if (other > total * 0.001)
                processWeights["Other / unattributed"] = other;
        }

        var totalDisplay = metric switch
        {
            "gpu" => FormatHoursCompact(util.GpuHours),
            "cpu" => FormatHoursCompact(util.CpuHours),
            "dr" => FormatBytesCompact(util.DiskReadBytes),
            "dw" => FormatBytesCompact(util.DiskWriteBytes),
            "ntx" => FormatBytesCompact(util.NetTxBytes),
            "nrx" => FormatBytesCompact(util.NetRxBytes),
            _ => "—"
        };

        var note = metric is "ntx" or "nrx"
            ? "Network volume is machine-level only — no per-process counters. Breakdown is by interactive user at sample time and by day."
            : "Totals match the list cell (machine gauges). Process shares weight top-N sample values × interval; remainder is Other / unattributed.";

        return new UtilDrilldown(
            machine.Id,
            machine.Hostname,
            string.IsNullOrWhiteSpace(machine.FriendlyName) ? machine.Hostname : machine.FriendlyName!,
            period,
            PeriodOptions.First(p => p.Key == period).Label,
            metric,
            MetricLabel(metric),
            totalDisplay,
            note,
            hasProcessJson || metric is "ntx" or "nrx",
            hasProcessJson || metric is "ntx" or "nrx"
                ? null
                : "No process samples in this period (upgrade client). Day and person rows still use snapshot username / sessions.",
            ToShareRows(byPerson, Math.Max(1e-9, total), isPercentOfWindow: false),
            ToShareRows(byDay.ToDictionary(kv => kv.Key.ToString("yyyy-MM-dd"), kv => kv.Value), Math.Max(1e-9, total), isPercentOfWindow: false),
            ToShareRows(processWeights, Math.Max(1e-9, total), isPercentOfWindow: false),
            []);
    }

    private static double SampleDtSeconds(IReadOnlyList<FleetMetricSnapshot> snaps, int i)
    {
        double dt = SampleInterval.TotalSeconds;
        if (i + 1 < snaps.Count)
        {
            dt = (snaps[i + 1].SampledAtUtc - snaps[i].SampledAtUtc).TotalSeconds;
            if (dt <= 0 || dt > SampleInterval.TotalSeconds * 4)
                dt = SampleInterval.TotalSeconds;
        }
        return dt;
    }

    private static void AccumulateByDay(Dictionary<DateOnly, double> byDay, DateTimeOffset start, DateTimeOffset end, double totalSeconds)
    {
        if (totalSeconds <= 0 || end <= start) return;
        var cursor = start;
        var remaining = totalSeconds;
        while (cursor < end && remaining > 0)
        {
            var day = DateOnly.FromDateTime(cursor.UtcDateTime);
            var dayEnd = new DateTimeOffset(day.AddDays(1).ToDateTime(TimeOnly.MinValue), TimeSpan.Zero);
            if (dayEnd > end) dayEnd = end;
            var slice = Math.Min(remaining, (dayEnd - cursor).TotalSeconds);
            if (slice <= 0) break;
            byDay[day] = byDay.GetValueOrDefault(day) + slice;
            remaining -= slice;
            cursor = dayEnd;
        }
    }

    private static List<DrillShareRow> ToShareRows(
        IReadOnlyDictionary<string, double> weights,
        double denominator,
        bool isPercentOfWindow)
    {
        if (weights.Count == 0 || denominator <= 0)
            return [];
        return weights
            .OrderByDescending(kv => kv.Value)
            .Select(kv =>
            {
                var pct = kv.Value / denominator * 100.0;
                string? detail = isPercentOfWindow
                    ? $"{FormatDuration(kv.Value)} · {pct:0.#}% of window"
                    : FormatDurationOrVolume(kv.Value);
                return new DrillShareRow(kv.Key, pct, detail);
            })
            .ToList();
    }

    private static string FormatDuration(double seconds)
    {
        if (seconds < 60) return $"{seconds:0}s";
        if (seconds < 3600) return $"{seconds / 60:0.#}m";
        return $"{seconds / 3600:0.##}h";
    }

    private static string FormatDurationOrVolume(double value)
    {
        // Heuristic: values that look like bytes (>= 1e5) format as bytes; else as hours if < 1e4 else duration seconds.
        if (value >= 100_000)
            return FormatBytesCompact(value);
        if (value < 500)
            return FormatHoursCompact(value) + " h";
        return FormatDuration(value);
    }

    private static List<TopProcessSampleDto> DeserializeTops(string? json)
    {
        if (string.IsNullOrWhiteSpace(json) || json == "[]")
            return [];
        try
        {
            return System.Text.Json.JsonSerializer.Deserialize<List<TopProcessSampleDto>>(json, ProcessJsonOptions) ?? [];
        }
        catch
        {
            return [];
        }
    }

    private static string DisplayUser(string? username)
    {
        if (string.IsNullOrWhiteSpace(username))
            return "—";
        var u = username.Trim();
        var slash = u.IndexOf('\\');
        if (slash >= 0 && slash < u.Length - 1)
            u = u[(slash + 1)..];
        return u;
    }

    private static bool UsersMatch(string? a, string? b) =>
        string.Equals(DisplayUser(a), DisplayUser(b), StringComparison.OrdinalIgnoreCase);

    public sealed record MachineUtilRow(
        string PeriodKey,
        double ActivePct,
        double? PassivePct,
        double FreePct,
        double? GpuHours,
        double? CpuHours,
        double? DiskReadBytes,
        double? DiskWriteBytes,
        double? NetTxBytes,
        double? NetRxBytes,
        bool HasHardwareSamples);

    public sealed record DrillShareRow(string Label, double SharePct, string? Detail);

    public sealed record DrillSessionRow(
        string Username,
        string State,
        DateTimeOffset StartedUtc,
        DateTimeOffset? EndedUtc,
        double SecondsInWindow,
        IReadOnlyList<string> Processes);

    public sealed record UtilDrilldown(
        int MachineId,
        string Hostname,
        string DisplayName,
        string PeriodKey,
        string PeriodLabel,
        string MetricKey,
        string MetricLabel,
        string TotalDisplay,
        string Explanation,
        bool HasProcessAttribution,
        string? ProcessGapNote,
        IReadOnlyList<DrillShareRow> ByPerson,
        IReadOnlyList<DrillShareRow> ByDay,
        IReadOnlyList<DrillShareRow> ByProcess,
        IReadOnlyList<DrillSessionRow> Sessions);
}
