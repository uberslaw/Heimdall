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

        // SQLite cannot filter DateTimeOffset in SQL — load by machine, filter in memory.
        var snaps = (await db.FleetMetricSnapshots.AsNoTracking()
                .Where(s => ids.Contains(s.MachineId))
                .ToListAsync(ct))
            .Where(s => s.SampledAtUtc >= from && s.SampledAtUtc < to)
            .OrderBy(s => s.SampledAtUtc)
            .ToList();

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
}
