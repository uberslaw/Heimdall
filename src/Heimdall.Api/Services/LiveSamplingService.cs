using System.Text.Json;
using Heimdall.Api.Data;
using Heimdall.Shared.Contracts;
using Microsoft.EntityFrameworkCore;

namespace Heimdall.Api.Services;

/// <summary>
/// Reference-counted "fan-in" for live resource sampling. Staff pages heartbeat a per-tab ViewerId every
/// ~20s (and send an explicit leave via sendBeacon on close). A host is "active" whenever at least one
/// non-stale viewer belongs to a group that includes that host — regardless of how many tabs/people are
/// looking at it, or whether they're looking via the same group or different groups sharing a machine.
/// The agent polls IsHostnameActiveAsync (via /api/resource-sampling/{hostname}/status) on a fast,
/// independent cadence and starts/stops its own local sampling loop accordingly — the API never asks the
/// agent to sample; the agent decides each poll based on the flag, so there is always exactly one sampling
/// loop per machine no matter how many viewers are watching it (see deliverable "shared session" notes).
/// The Sessions page "Open" drill-down uses the same fan-in via a second, hostname-keyed viewer table
/// (SessionDrilldownViewer) so any machine can be sampled on demand without needing a Remote Access Group.
/// </summary>
public class LiveSamplingService(HeimdallDbContext db)
{
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true };

    /// <summary>Viewer considered gone if no heartbeat/join within this window. Heartbeat cadence is ~20s; this allows one missed beat plus network jitter.</summary>
    public static readonly TimeSpan StaleWindow = TimeSpan.FromSeconds(45);

    public async Task JoinOrHeartbeatAsync(int groupId, string viewerId, string? email, CancellationToken ct)
    {
        var now = DateTimeOffset.UtcNow;
        var viewer = await db.RemoteAccessViewers
            .FirstOrDefaultAsync(v => v.GroupId == groupId && v.ViewerId == viewerId, ct);
        if (viewer is null)
        {
            db.RemoteAccessViewers.Add(new RemoteAccessViewer
            {
                GroupId = groupId,
                ViewerId = viewerId,
                Email = email,
                LastHeartbeatUtc = now
            });
        }
        else
        {
            viewer.LastHeartbeatUtc = now;
            if (!string.IsNullOrWhiteSpace(email))
                viewer.Email = email;
        }

        await db.SaveChangesAsync(ct);
    }

    public async Task LeaveAsync(int groupId, string viewerId, CancellationToken ct)
    {
        var viewer = await db.RemoteAccessViewers
            .FirstOrDefaultAsync(v => v.GroupId == groupId && v.ViewerId == viewerId, ct);
        if (viewer is null) return;
        db.RemoteAccessViewers.Remove(viewer);
        await db.SaveChangesAsync(ct);
    }

    /// <summary>
    /// Ad-hoc counterpart to JoinOrHeartbeatAsync for the Sessions page "Open" drill-down — same fan-in
    /// idea, but keyed by hostname directly since a drilled-into machine need not belong to any Remote
    /// Access Group.
    /// </summary>
    public async Task JoinOrHeartbeatHostAsync(string hostname, string viewerId, CancellationToken ct)
    {
        var now = DateTimeOffset.UtcNow;
        var viewer = await db.SessionDrilldownViewers
            .FirstOrDefaultAsync(v => v.Hostname == hostname && v.ViewerId == viewerId, ct);
        if (viewer is null)
        {
            db.SessionDrilldownViewers.Add(new SessionDrilldownViewer
            {
                Hostname = hostname,
                ViewerId = viewerId,
                LastHeartbeatUtc = now
            });
        }
        else
        {
            viewer.LastHeartbeatUtc = now;
        }

        await db.SaveChangesAsync(ct);
    }

    public async Task LeaveHostAsync(string hostname, string viewerId, CancellationToken ct)
    {
        var viewer = await db.SessionDrilldownViewers
            .FirstOrDefaultAsync(v => v.Hostname == hostname && v.ViewerId == viewerId, ct);
        if (viewer is null) return;
        db.SessionDrilldownViewers.Remove(viewer);
        await db.SaveChangesAsync(ct);
    }

    /// <summary>
    /// Distinct hostnames that currently need live sampling (any non-stale viewer via a group, or an
    /// ad-hoc Sessions drill-down viewer). Viewer rows are pulled into memory before filtering on
    /// LastHeartbeatUtc — the SQLite provider cannot translate DateTimeOffset comparisons in SQL (same
    /// constraint StatsQueryService works around for ORDER BY), and these tables are small (one row per
    /// open tab, pruned on leave/staleness).
    /// </summary>
    public async Task<HashSet<string>> GetActiveHostnamesAsync(CancellationToken ct)
    {
        var cutoff = DateTimeOffset.UtcNow - StaleWindow;

        var activeGroupIds = (await db.RemoteAccessViewers.AsNoTracking().ToListAsync(ct))
            .Where(v => v.LastHeartbeatUtc >= cutoff)
            .Select(v => v.GroupId)
            .Distinct()
            .ToList();

        var hostnames = activeGroupIds.Count == 0
            ? []
            : await db.RemoteAccessGroupMachines.AsNoTracking()
                .Where(m => activeGroupIds.Contains(m.GroupId))
                .Select(m => m.Hostname)
                .ToListAsync(ct);

        var result = hostnames.ToHashSet(StringComparer.OrdinalIgnoreCase);

        var adHocHostnames = (await db.SessionDrilldownViewers.AsNoTracking().ToListAsync(ct))
            .Where(v => v.LastHeartbeatUtc >= cutoff)
            .Select(v => v.Hostname)
            .Distinct(StringComparer.OrdinalIgnoreCase);
        result.UnionWith(adHocHostnames);

        return result;
    }

    public async Task<bool> IsHostnameActiveAsync(string hostname, CancellationToken ct)
    {
        var cutoff = DateTimeOffset.UtcNow - StaleWindow;

        var groupIds = await db.RemoteAccessGroupMachines.AsNoTracking()
            .Where(m => m.Hostname.ToLower() == hostname.ToLower())
            .Select(m => m.GroupId)
            .ToListAsync(ct);
        if (groupIds.Count > 0)
        {
            var groupViewers = await db.RemoteAccessViewers.AsNoTracking()
                .Where(v => groupIds.Contains(v.GroupId))
                .ToListAsync(ct);
            if (groupViewers.Any(v => v.LastHeartbeatUtc >= cutoff))
                return true;
        }

        var hostViewers = await db.SessionDrilldownViewers.AsNoTracking()
            .Where(v => v.Hostname.ToLower() == hostname.ToLower())
            .ToListAsync(ct);
        return hostViewers.Any(v => v.LastHeartbeatUtc >= cutoff);
    }

    /// <summary>Union of favourite process names across every group this host belongs to (agent guarantees these are reported even outside top 3).</summary>
    public async Task<List<string>> GetFavoriteProcessNamesAsync(string hostname, CancellationToken ct)
    {
        var groupIds = await db.RemoteAccessGroupMachines
            .Where(m => m.Hostname.ToLower() == hostname.ToLower())
            .Select(m => m.GroupId)
            .ToListAsync(ct);
        if (groupIds.Count == 0) return [];

        return await db.RemoteAccessFavoriteProcesses
            .Where(f => groupIds.Contains(f.GroupId))
            .Select(f => f.ProcessName)
            .Distinct()
            .ToListAsync(ct);
    }

    public async Task<bool> ReportSampleAsync(ResourceSampleReportDto dto, CancellationToken ct)
    {
        var machine = await db.Machines.FirstOrDefaultAsync(m => m.Hostname == dto.Hostname, ct);
        if (machine is null)
            return false;

        var metric = await db.MachineResourceMetrics.FirstOrDefaultAsync(m => m.MachineId == machine.Id, ct);
        if (metric is null)
        {
            metric = new MachineResourceMetric { MachineId = machine.Id };
            db.MachineResourceMetrics.Add(metric);
        }

        metric.SampledAtUtc = dto.SampledAtUtc;
        metric.IsCalibrationAverage = dto.IsCalibrationAverage;
        metric.CpuPercent = dto.CpuPercent;
        metric.GpuPercent = dto.GpuPercent;
        metric.RamPercent = dto.RamPercent;
        metric.RamUsedGb = dto.RamUsedGb;
        metric.RamTotalGb = dto.RamTotalGb;
        metric.DiskReadBytesPerSec = dto.DiskReadBytesPerSec;
        metric.DiskWriteBytesPerSec = dto.DiskWriteBytesPerSec;
        metric.DiskReadLevel = dto.DiskReadLevel;
        metric.DiskWriteLevel = dto.DiskWriteLevel;
        metric.TopCpuProcessesJson = JsonSerializer.Serialize(dto.TopCpuProcesses, JsonOptions);
        metric.TopGpuProcessesJson = JsonSerializer.Serialize(dto.TopGpuProcesses, JsonOptions);
        metric.TopRamProcessesJson = JsonSerializer.Serialize(dto.TopRamProcesses, JsonOptions);
        metric.TopDiskReadProcessesJson = JsonSerializer.Serialize(dto.TopDiskReadProcesses, JsonOptions);
        metric.TopDiskWriteProcessesJson = JsonSerializer.Serialize(dto.TopDiskWriteProcesses, JsonOptions);
        metric.FavoriteProcessesJson = JsonSerializer.Serialize(dto.FavoriteProcesses, JsonOptions);

        await db.SaveChangesAsync(ct);
        return true;
    }

    public async Task<Dictionary<string, MachineMetricView>> GetLatestMetricsAsync(IEnumerable<string> hostnames, CancellationToken ct)
    {
        var hostSet = hostnames.ToList();
        if (hostSet.Count == 0) return [];

        var rows = await db.Machines.AsNoTracking()
            .Where(m => hostSet.Contains(m.Hostname))
            .Select(m => new { m.Hostname, m.Id })
            .ToListAsync(ct);
        var idToHost = rows.ToDictionary(r => r.Id, r => r.Hostname);
        var ids = rows.Select(r => r.Id).ToList();

        var metrics = ids.Count == 0
            ? []
            : await db.MachineResourceMetrics.AsNoTracking()
                .Where(m => ids.Contains(m.MachineId))
                .ToListAsync(ct);

        var cutoff = DateTimeOffset.UtcNow - StaleWindow;
        var activeGroupHosts = await GetActiveHostnamesAsync(ct);

        var result = new Dictionary<string, MachineMetricView>(StringComparer.OrdinalIgnoreCase);
        foreach (var metric in metrics)
        {
            if (!idToHost.TryGetValue(metric.MachineId, out var hostname)) continue;
            result[hostname] = ToView(metric, activeGroupHosts.Contains(hostname));
        }

        foreach (var hostname in hostSet)
        {
            if (!result.ContainsKey(hostname))
            {
                result[hostname] = new MachineMetricView(
                    hostname, null, false, null, null, null, null, null, null, null,
                    "Low", "Low", [], [], [], [], [], [],
                    activeGroupHosts.Contains(hostname));
            }
        }

        _ = cutoff;
        return result;
    }

    private static MachineMetricView ToView(MachineResourceMetric m, bool isActive) => new(
        Hostname: null,
        SampledAtUtc: m.SampledAtUtc,
        IsCalibrationAverage: m.IsCalibrationAverage,
        CpuPercent: m.CpuPercent,
        GpuPercent: m.GpuPercent,
        RamPercent: m.RamPercent,
        RamUsedGb: m.RamUsedGb,
        RamTotalGb: m.RamTotalGb,
        DiskReadBytesPerSec: m.DiskReadBytesPerSec,
        DiskWriteBytesPerSec: m.DiskWriteBytesPerSec,
        DiskReadLevel: m.DiskReadLevel,
        DiskWriteLevel: m.DiskWriteLevel,
        TopCpuProcesses: Deserialize(m.TopCpuProcessesJson),
        TopGpuProcesses: Deserialize(m.TopGpuProcessesJson),
        TopRamProcesses: Deserialize(m.TopRamProcessesJson),
        TopDiskReadProcesses: Deserialize(m.TopDiskReadProcessesJson),
        TopDiskWriteProcesses: Deserialize(m.TopDiskWriteProcessesJson),
        FavoriteProcesses: DeserializeFavorites(m.FavoriteProcessesJson),
        IsSamplingActive: isActive);

    private static List<TopProcessSampleDto> Deserialize(string json)
    {
        try { return JsonSerializer.Deserialize<List<TopProcessSampleDto>>(json, JsonOptions) ?? []; }
        catch { return []; }
    }

    private static List<FavoriteProcessSampleDto> DeserializeFavorites(string json)
    {
        try { return JsonSerializer.Deserialize<List<FavoriteProcessSampleDto>>(json, JsonOptions) ?? []; }
        catch { return []; }
    }

    public sealed record MachineMetricView(
        string? Hostname,
        DateTimeOffset? SampledAtUtc,
        bool IsCalibrationAverage,
        double? CpuPercent,
        double? GpuPercent,
        double? RamPercent,
        double? RamUsedGb,
        double? RamTotalGb,
        double? DiskReadBytesPerSec,
        double? DiskWriteBytesPerSec,
        string DiskReadLevel,
        string DiskWriteLevel,
        List<TopProcessSampleDto> TopCpuProcesses,
        List<TopProcessSampleDto> TopGpuProcesses,
        List<TopProcessSampleDto> TopRamProcesses,
        List<TopProcessSampleDto> TopDiskReadProcesses,
        List<TopProcessSampleDto> TopDiskWriteProcesses,
        List<FavoriteProcessSampleDto> FavoriteProcesses,
        bool IsSamplingActive);
}
