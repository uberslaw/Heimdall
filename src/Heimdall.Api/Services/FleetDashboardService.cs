using Heimdall.Api.Data;
using Heimdall.Shared.Contracts;
using Microsoft.EntityFrameworkCore;

namespace Heimdall.Api.Services;

/// <summary>
/// Historical Dashboard enrollment, fleet snapshot ingest, and derived analytics
/// (runtime / GPU·CPU·RAM hours / disk·network GB) over FleetMetricSnapshot rows.
/// </summary>
public class FleetDashboardService(HeimdallDbContext db)
{
    /// <summary>Default nominal sample interval used when bridging consecutive snapshots.</summary>
    public static readonly TimeSpan SampleInterval = TimeSpan.FromSeconds(30);

    /// <summary>POC retention hint for Help / UI (raw 30s samples).</summary>
    public const int RetentionDaysDefault = 90;

    public async Task<IReadOnlyList<EnrolledMachineRow>> ListEnrolledAsync(CancellationToken ct)
    {
        var rows = await db.FleetDashboardMachines.AsNoTracking()
            .Include(e => e.Machine)
            .OrderBy(e => e.Machine.Hostname)
            .ToListAsync(ct);

        return rows.Select(e => new EnrolledMachineRow(
            e.Id,
            e.MachineId,
            e.Machine.Hostname,
            e.Machine.LastIp,
            e.AddedUtc,
            e.Notes,
            e.Machine.LastSeenUtc)).ToList();
    }

    public async Task<IReadOnlyList<MachineSearchHit>> SearchMachinesAsync(string query, int take, CancellationToken ct)
    {
        var q = (query ?? "").Trim();
        if (q.Length == 0)
            return [];

        take = Math.Clamp(take, 1, 50);
        var enrolledIds = await db.FleetDashboardMachines.AsNoTracking()
            .Select(e => e.MachineId)
            .ToListAsync(ct);
        var enrolled = enrolledIds.ToHashSet();

        var like = q.ToLowerInvariant();
        var machines = await db.Machines.AsNoTracking()
            .Where(m =>
                m.Hostname.ToLower().Contains(like)
                || (m.LastIp != null && m.LastIp.ToLower().Contains(like)))
            .OrderBy(m => m.Hostname)
            .Take(take)
            .ToListAsync(ct);

        return machines.Select(m => new MachineSearchHit(
            m.Id,
            m.Hostname,
            m.LastIp,
            m.LastSeenUtc,
            enrolled.Contains(m.Id))).ToList();
    }

    public async Task<(bool Ok, string Message)> EnrollAsync(int machineId, string? notes, CancellationToken ct)
    {
        var machine = await db.Machines.FirstOrDefaultAsync(m => m.Id == machineId, ct);
        if (machine is null)
            return (false, "Machine not found.");

        if (await db.FleetDashboardMachines.AnyAsync(e => e.MachineId == machineId, ct))
            return (false, $"{machine.Hostname} is already enrolled.");

        db.FleetDashboardMachines.Add(new FleetDashboardMachine
        {
            MachineId = machineId,
            AddedUtc = DateTimeOffset.UtcNow,
            Notes = string.IsNullOrWhiteSpace(notes) ? null : notes.Trim()
        });
        await db.SaveChangesAsync(ct);
        return (true, $"Enrolled {machine.Hostname}. Fleet sampling starts after the agent next refreshes config.");
    }

    public async Task<(bool Ok, string Message)> UnenrollAsync(int enrollmentId, CancellationToken ct)
    {
        var row = await db.FleetDashboardMachines
            .Include(e => e.Machine)
            .FirstOrDefaultAsync(e => e.Id == enrollmentId, ct);
        if (row is null)
            return (false, "Enrollment not found.");

        var hostname = row.Machine.Hostname;
        db.FleetDashboardMachines.Remove(row);
        await db.SaveChangesAsync(ct);
        return (true, $"Removed {hostname} from the Historical Dashboard. Existing snapshots are kept.");
    }

    public async Task<bool> IngestSnapshotAsync(FleetSnapshotDto dto, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(dto.Hostname))
            return false;

        var machine = await db.Machines.FirstOrDefaultAsync(m => m.Hostname == dto.Hostname, ct);
        if (machine is null)
            return false;

        var enrolled = await db.FleetDashboardMachines.AnyAsync(e => e.MachineId == machine.Id, ct);
        if (!enrolled)
            return false;

        // Prefer process-specific util for Active/Idle; fall back to system gauges for older agents.
        var isActive = FleetActiveThresholds.ComputeIsActive(
            dto.TuflowRunning,
            dto.ProcessCpuPercent ?? dto.CpuPercent,
            dto.ProcessGpuPercent ?? dto.GpuPercent,
            dto.ProcessDiskReadMBps ?? dto.DiskReadMBps,
            dto.ProcessDiskWriteMBps ?? dto.DiskWriteMBps);

        db.FleetMetricSnapshots.Add(new FleetMetricSnapshot
        {
            SampledAtUtc = dto.SampledAtUtc == default ? DateTimeOffset.UtcNow : dto.SampledAtUtc,
            MachineId = machine.Id,
            Username = string.IsNullOrWhiteSpace(dto.Username) ? null : dto.Username.Trim(),
            TuflowRunning = dto.TuflowRunning,
            CpuPercent = dto.CpuPercent,
            GpuPercent = dto.GpuPercent,
            GpuMemoryUsedMb = dto.GpuMemoryUsedMb,
            RamUsedMb = dto.RamUsedMb,
            DiskReadMBps = dto.DiskReadMBps,
            DiskWriteMBps = dto.DiskWriteMBps,
            NetworkInMBps = dto.NetworkInMBps,
            NetworkOutMBps = dto.NetworkOutMBps,
            IsActive = isActive
        });
        await db.SaveChangesAsync(ct);
        return true;
    }

    public async Task<IReadOnlyList<LiveFleetRow>> GetLiveFleetAsync(CancellationToken ct)
    {
        var enrolled = await db.FleetDashboardMachines.AsNoTracking()
            .Include(e => e.Machine)
            .ToListAsync(ct);
        if (enrolled.Count == 0)
            return [];

        var machineIds = enrolled.Select(e => e.MachineId).ToList();
        var todayStart = DateTimeOffset.UtcNow.Date;
        var todayStartOffset = new DateTimeOffset(todayStart, TimeSpan.Zero);

        var recent = await db.FleetMetricSnapshots.AsNoTracking()
            .Where(s => machineIds.Contains(s.MachineId) && s.SampledAtUtc >= todayStartOffset.AddDays(-1))
            .OrderBy(s => s.SampledAtUtc)
            .ToListAsync(ct);

        var byMachine = recent.GroupBy(s => s.MachineId).ToDictionary(g => g.Key, g => g.ToList());
        var rows = new List<LiveFleetRow>();

        foreach (var e in enrolled.OrderBy(x => x.Machine.Hostname))
        {
            byMachine.TryGetValue(e.MachineId, out var snaps);
            snaps ??= [];
            var latest = snaps.Count == 0 ? null : snaps[^1];
            var todaySnaps = snaps.Where(s => s.SampledAtUtc >= todayStartOffset).ToList();
            var todayAgg = Aggregate(todaySnaps);

            var status = latest is null
                ? FleetStatus.Unknown
                : !latest.TuflowRunning
                    ? FleetStatus.NotRunning
                    : latest.IsActive
                        ? FleetStatus.Active
                        : FleetStatus.Idle;

            rows.Add(new LiveFleetRow(
                e.MachineId,
                e.Machine.Hostname,
                e.Machine.LastIp,
                latest?.Username,
                latest?.TuflowRunning ?? false,
                status,
                latest?.CpuPercent,
                latest?.GpuPercent,
                latest?.GpuMemoryUsedMb,
                latest?.RamUsedMb,
                latest?.DiskReadMBps,
                latest?.DiskWriteMBps,
                latest?.NetworkInMBps,
                latest?.NetworkOutMBps,
                todayAgg.RuntimeHours,
                todayAgg.ActiveRuntimeHours,
                todayAgg.GpuHours,
                latest?.SampledAtUtc,
                e.Machine.LastSeenUtc));
        }

        return rows;
    }

    public FleetMetrics Aggregate(IReadOnlyList<FleetMetricSnapshot> snapshots) =>
        AggregateInternal(snapshots);

    public async Task<FleetMetrics> AggregateFleetAsync(DateTimeOffset fromUtc, DateTimeOffset? toUtc, CancellationToken ct)
    {
        var enrolledIds = await db.FleetDashboardMachines.AsNoTracking()
            .Select(e => e.MachineId)
            .ToListAsync(ct);
        if (enrolledIds.Count == 0)
            return FleetMetrics.Empty;

        var query = db.FleetMetricSnapshots.AsNoTracking()
            .Where(s => enrolledIds.Contains(s.MachineId) && s.SampledAtUtc >= fromUtc);
        if (toUtc is not null)
            query = query.Where(s => s.SampledAtUtc < toUtc.Value);

        var snaps = await query.OrderBy(s => s.SampledAtUtc).ToListAsync(ct);
        return AggregateInternal(snaps);
    }

    public async Task<FleetMetrics> AggregateMachineAsync(int machineId, DateTimeOffset fromUtc, DateTimeOffset? toUtc, CancellationToken ct)
    {
        var query = db.FleetMetricSnapshots.AsNoTracking()
            .Where(s => s.MachineId == machineId && s.SampledAtUtc >= fromUtc);
        if (toUtc is not null)
            query = query.Where(s => s.SampledAtUtc < toUtc.Value);

        var snaps = await query.OrderBy(s => s.SampledAtUtc).ToListAsync(ct);
        return AggregateInternal(snaps);
    }

    public async Task<IReadOnlyList<(int MachineId, string Hostname, FleetMetrics Metrics)>> AggregatePerMachineAsync(
        DateTimeOffset fromUtc, DateTimeOffset? toUtc, CancellationToken ct)
    {
        var enrolled = await db.FleetDashboardMachines.AsNoTracking()
            .Include(e => e.Machine)
            .ToListAsync(ct);
        if (enrolled.Count == 0)
            return [];

        var ids = enrolled.Select(e => e.MachineId).ToList();
        var query = db.FleetMetricSnapshots.AsNoTracking()
            .Where(s => ids.Contains(s.MachineId) && s.SampledAtUtc >= fromUtc);
        if (toUtc is not null)
            query = query.Where(s => s.SampledAtUtc < toUtc.Value);

        var snaps = await query.OrderBy(s => s.SampledAtUtc).ToListAsync(ct);
        var byMachine = snaps.GroupBy(s => s.MachineId).ToDictionary(g => g.Key, g => g.ToList());

        return enrolled
            .OrderBy(e => e.Machine.Hostname)
            .Select(e =>
            {
                byMachine.TryGetValue(e.MachineId, out var list);
                return (e.MachineId, e.Machine.Hostname, AggregateInternal(list ?? []));
            })
            .ToList();
    }

    public async Task<IReadOnlyList<TimeSeriesPoint>> GetTimeSeriesAsync(
        int machineId, DateTimeOffset fromUtc, DateTimeOffset? toUtc, TimeSpan bucket, CancellationToken ct)
    {
        var query = db.FleetMetricSnapshots.AsNoTracking()
            .Where(s => s.MachineId == machineId && s.SampledAtUtc >= fromUtc);
        if (toUtc is not null)
            query = query.Where(s => s.SampledAtUtc < toUtc.Value);

        var snaps = await query.OrderBy(s => s.SampledAtUtc).ToListAsync(ct);
        if (snaps.Count == 0)
            return [];

        var bucketSeconds = Math.Max(30, bucket.TotalSeconds);
        var groups = snaps.GroupBy(s =>
        {
            var epoch = s.SampledAtUtc.ToUnixTimeSeconds();
            var aligned = epoch - (epoch % (long)bucketSeconds);
            return DateTimeOffset.FromUnixTimeSeconds(aligned);
        });

        return groups
            .OrderBy(g => g.Key)
            .Select(g =>
            {
                var list = g.ToList();
                return new TimeSeriesPoint(
                    g.Key,
                    Avg(list, s => s.CpuPercent),
                    Avg(list, s => s.GpuPercent),
                    Avg(list, s => s.RamUsedMb),
                    Avg(list, s => s.DiskReadMBps),
                    Avg(list, s => s.DiskWriteMBps),
                    Avg(list, s => s.NetworkInMBps),
                    Avg(list, s => s.NetworkOutMBps),
                    list.Count(s => s.TuflowRunning),
                    list.Count(s => s.IsActive));
            })
            .ToList();
    }

    public static (DateTimeOffset From, DateTimeOffset? To) ResolvePeriod(string period)
    {
        var now = DateTimeOffset.UtcNow;
        var today = new DateTimeOffset(now.Date, TimeSpan.Zero);
        return (period ?? "").Trim().ToLowerInvariant() switch
        {
            "today" => (today, null),
            "week" or "7d" => (today.AddDays(-6), null),
            "month" => (new DateTimeOffset(now.Year, now.Month, 1, 0, 0, 0, TimeSpan.Zero), null),
            "30d" => (now.AddDays(-30), null),
            "3m" or "90d" => (now.AddDays(-90), null),
            "6m" => (now.AddDays(-180), null),
            "year" or "365d" => (now.AddDays(-365), null),
            "24h" => (now.AddHours(-24), null),
            "all" or "all-time" => (DateTimeOffset.MinValue.AddYears(1), null),
            "daily" => (today.AddDays(-1), today),
            _ => (today, null)
        };
    }

    public static TimeSpan ResolveChartBucket(string range) =>
        (range ?? "").Trim().ToLowerInvariant() switch
        {
            "24h" => TimeSpan.FromMinutes(5),
            "7d" => TimeSpan.FromHours(1),
            "30d" => TimeSpan.FromHours(3),
            "90d" => TimeSpan.FromHours(6),
            "365d" => TimeSpan.FromDays(1),
            _ => TimeSpan.FromHours(1)
        };

    private static FleetMetrics AggregateInternal(IReadOnlyList<FleetMetricSnapshot> snapshots)
    {
        if (snapshots.Count == 0)
            return FleetMetrics.Empty;

        double runtimeSec = 0, activeSec = 0, idleSec = 0;
        double gpuHours = 0, cpuHours = 0, ramGbHours = 0;
        double readGb = 0, writeGb = 0, netInGb = 0, netOutGb = 0;
        var sampleCount = snapshots.Count;

        for (var i = 0; i < snapshots.Count; i++)
        {
            var s = snapshots[i];
            double dtSec;
            if (i + 1 < snapshots.Count)
            {
                dtSec = (snapshots[i + 1].SampledAtUtc - s.SampledAtUtc).TotalSeconds;
                if (dtSec <= 0 || dtSec > SampleInterval.TotalSeconds * 4)
                    dtSec = SampleInterval.TotalSeconds;
            }
            else
            {
                dtSec = SampleInterval.TotalSeconds;
            }

            var dtHours = dtSec / 3600.0;

            if (s.TuflowRunning)
            {
                runtimeSec += dtSec;
                if (s.IsActive) activeSec += dtSec;
                else idleSec += dtSec;
            }

            if (s.GpuPercent is double gpu)
                gpuHours += (gpu / 100.0) * dtHours;
            if (s.CpuPercent is double cpu)
                cpuHours += (cpu / 100.0) * dtHours;
            if (s.RamUsedMb is double ramMb)
                ramGbHours += (ramMb / 1024.0) * dtHours;

            if (s.DiskReadMBps is double r)
                readGb += r * dtSec / 1024.0;
            if (s.DiskWriteMBps is double w)
                writeGb += w * dtSec / 1024.0;
            if (s.NetworkInMBps is double ni)
                netInGb += ni * dtSec / 1024.0;
            if (s.NetworkOutMBps is double no)
                netOutGb += no * dtSec / 1024.0;
        }

        return new FleetMetrics(
            sampleCount,
            runtimeSec / 3600.0,
            activeSec / 3600.0,
            idleSec / 3600.0,
            gpuHours,
            cpuHours,
            ramGbHours,
            readGb,
            writeGb,
            netInGb,
            netOutGb);
    }

    private static double? Avg(IReadOnlyList<FleetMetricSnapshot> list, Func<FleetMetricSnapshot, double?> select)
    {
        var values = list.Select(select).Where(v => v is not null).Select(v => v!.Value).ToList();
        return values.Count == 0 ? null : values.Average();
    }

    public sealed record EnrolledMachineRow(
        int EnrollmentId,
        int MachineId,
        string Hostname,
        string? LastIp,
        DateTimeOffset AddedUtc,
        string? Notes,
        DateTimeOffset LastSeenUtc);

    public sealed record MachineSearchHit(
        int MachineId,
        string Hostname,
        string? LastIp,
        DateTimeOffset LastSeenUtc,
        bool AlreadyEnrolled);

    public sealed record LiveFleetRow(
        int MachineId,
        string Hostname,
        string? LastIp,
        string? Username,
        bool TuflowRunning,
        FleetStatus Status,
        double? CpuPercent,
        double? GpuPercent,
        double? GpuMemoryUsedMb,
        double? RamUsedMb,
        double? DiskReadMBps,
        double? DiskWriteMBps,
        double? NetworkInMBps,
        double? NetworkOutMBps,
        double TodayRuntimeHours,
        double TodayActiveHours,
        double TodayGpuHours,
        DateTimeOffset? LastSampleUtc,
        DateTimeOffset LastSeenUtc);

    public sealed record FleetMetrics(
        int SampleCount,
        double RuntimeHours,
        double ActiveRuntimeHours,
        double IdleRuntimeHours,
        double GpuHours,
        double CpuHours,
        double RamGbHours,
        double DiskReadGb,
        double DiskWriteGb,
        double NetworkInGb,
        double NetworkOutGb)
    {
        public static FleetMetrics Empty { get; } = new(0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0);

        public FleetMetrics DivideBy(double divisor)
        {
            if (divisor <= 0) return Empty;
            return new FleetMetrics(
                SampleCount,
                RuntimeHours / divisor,
                ActiveRuntimeHours / divisor,
                IdleRuntimeHours / divisor,
                GpuHours / divisor,
                CpuHours / divisor,
                RamGbHours / divisor,
                DiskReadGb / divisor,
                DiskWriteGb / divisor,
                NetworkInGb / divisor,
                NetworkOutGb / divisor);
        }
    }

    public sealed record TimeSeriesPoint(
        DateTimeOffset BucketUtc,
        double? CpuPercent,
        double? GpuPercent,
        double? RamUsedMb,
        double? DiskReadMBps,
        double? DiskWriteMBps,
        double? NetworkInMBps,
        double? NetworkOutMBps,
        int TuflowRunningCount,
        int ActiveCount);

    public enum FleetStatus
    {
        Unknown,
        NotRunning,
        Idle,
        Active
    }
}
