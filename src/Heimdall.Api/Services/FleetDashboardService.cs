using Heimdall.Api.Data;
using Heimdall.Shared.Contracts;
using Microsoft.EntityFrameworkCore;

namespace Heimdall.Api.Services;

/// <summary>
/// Flood allowlist (FleetDashboardMachines) enrollment, estate-wide fleet snapshot ingest,
/// and derived analytics (runtime / GPU·CPU·RAM hours / disk·network GB) over FleetMetricSnapshot rows.
/// Sampling is always-on for every known Machine; FleetDashboardMachines gates TUFLOW / Flood sims only.
/// </summary>
public class FleetDashboardService(HeimdallDbContext db)
{
    /// <summary>Default nominal sample interval used when bridging consecutive snapshots.</summary>
    public static readonly TimeSpan SampleInterval = TimeSpan.FromSeconds(30);

    /// <summary>Default retention for raw 30s samples (Help / purge hosted service).</summary>
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
            e.Machine.FriendlyName,
            e.Machine.LastIp,
            e.AddedUtc,
            e.Notes,
            e.Machine.LastSeenUtc)).ToList();
    }

    /// <summary>All known machines for the Enrollment picker (hostname / friendly / IP).</summary>
    public async Task<IReadOnlyList<MachineSearchHit>> ListMachinesForEnrollmentPickerAsync(CancellationToken ct)
    {
        var enrolledIds = await db.FleetDashboardMachines.AsNoTracking()
            .Select(e => e.MachineId)
            .ToListAsync(ct);
        var enrolled = enrolledIds.ToHashSet();

        var machines = await db.Machines.AsNoTracking()
            .OrderBy(m => m.Hostname)
            .ToListAsync(ct);

        return machines.Select(m => new MachineSearchHit(
            m.Id,
            m.Hostname,
            m.FriendlyName,
            m.LastIp,
            m.LastSeenUtc,
            enrolled.Contains(m.Id))).ToList();
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

        // Load then filter in memory — SQLite EF often mishandles ToLower()/Contains on nullable IP text,
        // and LastIp may include whitespace or IPv4 with unexpected formatting from the agent.
        var like = q.ToLowerInvariant();
        var ipNeedle = NormalizeIpForSearch(q);

        var machines = await db.Machines.AsNoTracking()
            .OrderBy(m => m.Hostname)
            .ToListAsync(ct);

        var hits = machines
            .Where(m =>
            {
                if (m.Hostname.Contains(like, StringComparison.OrdinalIgnoreCase))
                    return true;
                if (!string.IsNullOrWhiteSpace(m.FriendlyName)
                    && m.FriendlyName.Contains(like, StringComparison.OrdinalIgnoreCase))
                    return true;
                if (string.IsNullOrWhiteSpace(m.LastIp))
                    return false;
                var ip = m.LastIp.Trim();
                if (ip.Contains(like, StringComparison.OrdinalIgnoreCase))
                    return true;
                if (ipNeedle.Length > 0 && NormalizeIpForSearch(ip).Contains(ipNeedle, StringComparison.Ordinal))
                    return true;
                return false;
            })
            .Take(take)
            .ToList();

        return hits.Select(m => new MachineSearchHit(
            m.Id,
            m.Hostname,
            m.FriendlyName,
            string.IsNullOrWhiteSpace(m.LastIp) ? null : m.LastIp.Trim(),
            m.LastSeenUtc,
            enrolled.Contains(m.Id))).ToList();
    }

    private static string NormalizeIpForSearch(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return "";
        // Strip brackets / zone ids; keep digits and dots for IPv4 substring match (e.g. "10.34" or "10.34.68.8").
        var s = value.Trim();
        var cut = s.IndexOf('%');
        if (cut >= 0)
            s = s[..cut];
        if (s.StartsWith('[') && s.EndsWith(']'))
            s = s[1..^1];
        return s.ToLowerInvariant();
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
        return (true, $"Enrolled {machine.Hostname} for Flood / TUFLOW. Util sampling is already on for all clients.");
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
        return (true, $"Removed {hostname} from the Flood allowlist. Existing snapshots are kept; util sampling continues.");
    }

    public async Task<bool> IngestSnapshotAsync(FleetSnapshotDto dto, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(dto.Hostname))
            return false;

        var machine = await db.Machines.FirstOrDefaultAsync(m => m.Hostname == dto.Hostname, ct);
        if (machine is null)
            return false;

        // Prefer process-specific util for Active/Idle; fall back to system gauges for older agents.
        var gpuPercent = SanitizeGpuPercent(dto.GpuPercent);
        var processGpuPercent = SanitizeGpuPercent(dto.ProcessGpuPercent);
        var isActive = FleetActiveThresholds.ComputeIsActive(
            dto.TuflowRunning,
            dto.ProcessCpuPercent ?? dto.CpuPercent,
            processGpuPercent ?? gpuPercent,
            dto.ProcessDiskReadMBps ?? dto.DiskReadMBps,
            dto.ProcessDiskWriteMBps ?? dto.DiskWriteMBps);

        db.FleetMetricSnapshots.Add(new FleetMetricSnapshot
        {
            SampledAtUtc = dto.SampledAtUtc == default ? DateTimeOffset.UtcNow : dto.SampledAtUtc,
            MachineId = machine.Id,
            Username = string.IsNullOrWhiteSpace(dto.Username) ? null : dto.Username.Trim(),
            TuflowRunning = dto.TuflowRunning,
            CpuPercent = dto.CpuPercent,
            GpuPercent = gpuPercent,
            GpuMemoryUsedMb = dto.GpuMemoryUsedMb,
            RamUsedMb = dto.RamUsedMb,
            DiskReadMBps = dto.DiskReadMBps,
            DiskWriteMBps = dto.DiskWriteMBps,
            NetworkInMBps = dto.NetworkInMBps,
            NetworkOutMBps = dto.NetworkOutMBps,
            ProcessCpuPercent = dto.ProcessCpuPercent,
            ProcessGpuPercent = processGpuPercent,
            ProcessDiskReadMBps = dto.ProcessDiskReadMBps,
            ProcessDiskWriteMBps = dto.ProcessDiskWriteMBps,
            IsActive = isActive,
            TopCpuProcessesJson = SerializeTopProcesses(dto.TopCpuProcesses),
            TopGpuProcessesJson = SerializeTopProcesses(dto.TopGpuProcesses),
            TopDiskReadProcessesJson = SerializeTopProcesses(dto.TopDiskReadProcesses),
            TopDiskWriteProcessesJson = SerializeTopProcesses(dto.TopDiskWriteProcesses)
        });
        await db.SaveChangesAsync(ct);
        return true;
    }

    private static readonly System.Text.Json.JsonSerializerOptions TopProcessJsonOptions = new()
    {
        PropertyNamingPolicy = System.Text.Json.JsonNamingPolicy.CamelCase
    };

    private static string SerializeTopProcesses(IReadOnlyList<TopProcessSampleDto>? list)
    {
        if (list is null || list.Count == 0)
            return "[]";
        // Cap at 5 to bound SQLite row size.
        var capped = list.Count <= 5 ? list : list.Take(5).ToList();
        return System.Text.Json.JsonSerializer.Serialize(capped, TopProcessJsonOptions);
    }

    /// <summary>Live view for Flood-enrolled machines only (TUFLOW / Historical under Flood).</summary>
    public Task<IReadOnlyList<LiveFleetRow>> GetLiveFleetAsync(CancellationToken ct) =>
        GetLiveFleetAsync(enrolledOnly: true, ct);

    /// <summary>
    /// Live util view. When <paramref name="enrolledOnly"/> is true, Flood allowlist only;
    /// otherwise every known Machine (Fleet → Live estate view).
    /// </summary>
    public async Task<IReadOnlyList<LiveFleetRow>> GetLiveFleetAsync(bool enrolledOnly, CancellationToken ct)
    {
        List<(int MachineId, string Hostname, string? FriendlyName, string? LastIp, DateTimeOffset LastSeenUtc, int? TeamId, string? TeamName)> machines;
        if (enrolledOnly)
        {
            var enrolled = await db.FleetDashboardMachines.AsNoTracking()
                .Include(e => e.Machine).ThenInclude(m => m.Team)
                .ToListAsync(ct);
            if (enrolled.Count == 0)
                return [];
            machines = enrolled
                .OrderBy(e => e.Machine.Hostname)
                .Select(e => (
                    e.MachineId,
                    e.Machine.Hostname,
                    e.Machine.FriendlyName,
                    e.Machine.LastIp,
                    e.Machine.LastSeenUtc,
                    e.Machine.TeamId,
                    e.Machine.Team?.Name))
                .ToList();
        }
        else
        {
            var all = await db.Machines.AsNoTracking()
                .Include(m => m.Team)
                .OrderBy(m => m.Hostname)
                .ToListAsync(ct);
            if (all.Count == 0)
                return [];
            machines = all
                .Select(m => (m.Id, m.Hostname, m.FriendlyName, m.LastIp, m.LastSeenUtc, m.TeamId, m.Team?.Name))
                .ToList();
        }

        var machineIds = machines.Select(m => m.MachineId).ToList();
        var todayStart = DateTimeOffset.UtcNow.Date;
        var todayStartOffset = new DateTimeOffset(todayStart, TimeSpan.Zero);
        var recentFrom = todayStartOffset.AddDays(-1);

        var recent = await LoadSnapshotsForMachinesAsync(machineIds, recentFrom, toUtc: null, ct);

        var byMachine = recent.GroupBy(s => s.MachineId).ToDictionary(g => g.Key, g => g.ToList());
        var rows = new List<LiveFleetRow>();

        foreach (var m in machines)
        {
            byMachine.TryGetValue(m.MachineId, out var snaps);
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
                m.MachineId,
                m.Hostname,
                string.IsNullOrWhiteSpace(m.FriendlyName) ? null : m.FriendlyName.Trim(),
                m.LastIp,
                latest?.Username,
                latest?.TuflowRunning ?? false,
                status,
                latest?.CpuPercent,
                SanitizeGpuPercent(latest?.GpuPercent),
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
                m.LastSeenUtc,
                m.TeamId,
                m.TeamName));
        }

        return rows;
    }

    public Task<int> PurgeSnapshotsOlderThanAsync(int retentionDays, CancellationToken ct)
    {
        var days = Math.Max(1, retentionDays);
        var cutoff = DateTimeOffset.UtcNow.AddDays(-days);
        return FleetSnapshotQuery.PurgeOlderThanAsync(db, cutoff, ct);
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

        var snaps = await LoadSnapshotsForMachinesAsync(enrolledIds, fromUtc, toUtc, ct);
        return AggregateInternal(snaps);
    }

    public async Task<FleetMetrics> AggregateMachineAsync(int machineId, DateTimeOffset fromUtc, DateTimeOffset? toUtc, CancellationToken ct)
    {
        var snaps = await LoadSnapshotsForMachinesAsync([machineId], fromUtc, toUtc, ct);
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
        var snaps = await LoadSnapshotsForMachinesAsync(ids, fromUtc, toUtc, ct);
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
        var snaps = await LoadSnapshotsForMachinesAsync([machineId], fromUtc, toUtc, ct);
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

    /// <summary>Load fleet snapshots with date bounds applied in SQL (see <see cref="FleetSnapshotQuery"/>).</summary>
    internal Task<List<FleetMetricSnapshot>> LoadSnapshotsForMachinesAsync(
        IReadOnlyCollection<int> machineIds,
        DateTimeOffset fromUtc,
        DateTimeOffset? toUtc,
        CancellationToken ct) =>
        FleetSnapshotQuery.LoadForMachinesAsync(db, machineIds, fromUtc, toUtc, ct);

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
            {
                var sane = SanitizeGpuPercent(gpu);
                if (sane is double g)
                    gpuHours += (g / 100.0) * dtHours;
            }
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

    /// <summary>
    /// Drop GPU Engine counter glitches (seen as 1e13+% on some NVIDIA hosts). Multi-GPU can exceed 100%;
    /// 1000% is a generous ceiling (~10× full engines).
    /// </summary>
    public const double MaxSaneGpuPercent = 1000.0;

    public static double? SanitizeGpuPercent(double? gpu)
    {
        if (gpu is null) return null;
        if (double.IsNaN(gpu.Value) || double.IsInfinity(gpu.Value) || gpu.Value < 0)
            return null;
        if (gpu.Value > MaxSaneGpuPercent)
            return null;
        return gpu.Value;
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
        string? FriendlyName,
        string? LastIp,
        DateTimeOffset AddedUtc,
        string? Notes,
        DateTimeOffset LastSeenUtc);

    public sealed record MachineSearchHit(
        int MachineId,
        string Hostname,
        string? FriendlyName,
        string? LastIp,
        DateTimeOffset LastSeenUtc,
        bool AlreadyEnrolled);

    public sealed record LiveFleetRow(
        int MachineId,
        string Hostname,
        string? FriendlyName,
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
        DateTimeOffset LastSeenUtc,
        int? TeamId = null,
        string? TeamName = null)
    {
        public string DisplayName =>
            string.IsNullOrWhiteSpace(FriendlyName) ? Hostname : FriendlyName!;
    }

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
