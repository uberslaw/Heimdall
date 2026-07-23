using Heimdall.Api.Data;
using Heimdall.Shared.Contracts;
using Microsoft.EntityFrameworkCore;

namespace Heimdall.Api.Services;

public enum StatsScopeKind
{
    All,
    Region,
    Country,
    Office,
    Group,
    Machine
}

public sealed class StatsQueryService(HeimdallDbContext db)
{
    public async Task<StatsSnapshot> QueryAsync(
        StatsScopeKind scopeKind,
        string? scopeValue,
        DateTimeOffset fromUtc,
        DateTimeOffset toUtc,
        string? patternAppFilter = null,
        double? minRuntimeMinutes = null,
        double? maxRuntimeMinutes = null,
        CancellationToken ct = default)
    {
        var machines = await db.Machines.AsNoTracking().ToListAsync(ct);
        foreach (var m in machines)
            MachineHierarchy.EnsureDefaults(m);

        var scoped = FilterMachines(machines, scopeKind, scopeValue);
        var machineIds = scoped.Select(m => m.Id).ToHashSet();

        // SQLite EF DateTimeOffset filters/orderings are unreliable — load then filter in memory.
        var sessions = (await db.Sessions.AsNoTracking().ToListAsync(ct))
            .Where(s => machineIds.Contains(s.MachineId)
                        && s.StartedAtUtc < toUtc
                        && (s.EndedAtUtc ?? s.LastObservedUtc) >= fromUtc)
            .ToList();

        var runs = (await db.ProcessRuns.AsNoTracking().ToListAsync(ct))
            .Where(r => machineIds.Contains(r.MachineId)
                        && r.StartedAtUtc < toUtc
                        && (r.EndedAtUtc ?? r.LastSeenAtUtc) >= fromUtc)
            .ToList();

        var userRows = BuildUserRows(sessions);
        var appRows = BuildAppRows(runs);
        var patternRows = BuildPatternRows(sessions, runs, patternAppFilter, minRuntimeMinutes, maxRuntimeMinutes);
        var appOptions = runs
            .Select(r => r.ProcessName)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(n => n, StringComparer.OrdinalIgnoreCase)
            .ToList();

        var hasGpu = runs.Any(r => r.PeakGpuPercent is > 0);
        var hasDisk = runs.Any(r => (r.DiskReadBytes ?? 0) > 0 || (r.DiskWriteBytes ?? 0) > 0);

        return new StatsSnapshot(
            ScopeKind: scopeKind,
            ScopeValue: scopeValue,
            FromUtc: fromUtc,
            ToUtc: toUtc,
            MachineCount: scoped.Count,
            MachineHostnames: scoped.Select(m => m.Hostname).OrderBy(h => h).ToList(),
            UserStats: userRows,
            AppStats: appRows,
            UsagePatterns: patternRows,
            AppFilterOptions: appOptions,
            GpuDiskSamplesPresent: hasGpu || hasDisk,
            HasAnyGpuData: hasGpu,
            HasAnyDiskData: hasDisk
        );
    }

    public async Task<StatsScopeOptions> GetScopeOptionsAsync(CancellationToken ct = default)
    {
        var machines = await db.Machines.AsNoTracking().ToListAsync(ct);
        foreach (var m in machines)
            MachineHierarchy.EnsureDefaults(m);

        return new StatsScopeOptions(
            Regions: machines.Select(m => m.Region!).Where(s => !string.IsNullOrWhiteSpace(s))
                .Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(s => s).ToList(),
            Countries: machines.Select(m => m.Country!).Where(s => !string.IsNullOrWhiteSpace(s))
                .Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(s => s).ToList(),
            Offices: machines
                .Select(m => $"{m.Region}/{m.Office}")
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(s => s)
                .ToList(),
            Groups: machines.Select(m => m.MachineGroup ?? "")
                .Where(s => !string.IsNullOrWhiteSpace(s))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(s => s)
                .ToList(),
            Machines: machines.Select(m => m.Hostname)
                .OrderBy(s => s, StringComparer.OrdinalIgnoreCase)
                .ToList(),
            Tree: MachineHierarchy.BuildTree(machines)
        );
    }

    private static List<Machine> FilterMachines(List<Machine> machines, StatsScopeKind kind, string? value)
    {
        if (kind == StatsScopeKind.All || string.IsNullOrWhiteSpace(value))
            return kind == StatsScopeKind.All ? machines : [];

        var v = value.Trim();
        return kind switch
        {
            StatsScopeKind.Region => machines
                .Where(m => string.Equals(m.Region, v, StringComparison.OrdinalIgnoreCase)).ToList(),
            StatsScopeKind.Country => machines
                .Where(m => string.Equals(m.Country, v, StringComparison.OrdinalIgnoreCase)).ToList(),
            StatsScopeKind.Office => machines.Where(m =>
            {
                var key = $"{m.Region}/{m.Office}";
                return string.Equals(key, v, StringComparison.OrdinalIgnoreCase)
                       || string.Equals(m.Office, v, StringComparison.OrdinalIgnoreCase);
            }).ToList(),
            StatsScopeKind.Group => machines
                .Where(m => string.Equals(m.MachineGroup, v, StringComparison.OrdinalIgnoreCase)).ToList(),
            StatsScopeKind.Machine => machines
                .Where(m => string.Equals(m.Hostname, v, StringComparison.OrdinalIgnoreCase)).ToList(),
            _ => machines
        };
    }

    private static List<UserStatRow> BuildUserRows(List<UserSession> sessions)
    {
        return sessions
            .GroupBy(s => NormalizeUser(s.Username, s.Domain), StringComparer.OrdinalIgnoreCase)
            .Select(g =>
            {
                var logonCount = g.Count();
                var active = g.Sum(s => s.ActiveSeconds);
                var disconnected = g.Sum(s => s.DisconnectedSeconds);
                var rdpDisconnected = g
                    .Where(s => s.SessionType == SessionType.Rdp)
                    .Sum(s => s.DisconnectedSeconds);
                var total = active + disconnected;
                var avgSession = logonCount == 0 ? 0 : (double)active / logonCount;

                // Avg use per day = active seconds / distinct calendar days the user had a session
                // (not full calendar span of the filter — better for sparse usage).
                var distinctDays = g
                    .Select(s => s.StartedAtUtc.UtcDateTime.Date)
                    .Distinct()
                    .Count();
                var avgPerDay = distinctDays == 0 ? 0 : (double)active / distinctDays;

                return new UserStatRow(
                    Username: g.Key,
                    LogonCount: logonCount,
                    ActiveSeconds: active,
                    TotalSeconds: total,
                    DisconnectedSeconds: disconnected,
                    RdpDisconnectedSeconds: rdpDisconnected,
                    AvgSessionActiveSeconds: avgSession,
                    AvgActivePerDaySeconds: avgPerDay,
                    DistinctDays: distinctDays
                );
            })
            .OrderByDescending(u => u.ActiveSeconds)
            .ToList();
    }

    private static List<AppStatRow> BuildAppRows(List<ProcessRun> runs)
    {
        return runs
            .GroupBy(r => r.ProcessName, StringComparer.OrdinalIgnoreCase)
            .Select(g =>
            {
                var seconds = g.Sum(RunDurationSeconds);
                var cpuPeaks = g.Where(r => r.PeakCpuPercent.HasValue).Select(r => r.PeakCpuPercent!.Value).ToList();
                var gpuPeaks = g.Where(r => r.PeakGpuPercent.HasValue).Select(r => r.PeakGpuPercent!.Value).ToList();
                var diskRead = g.Sum(r => r.DiskReadBytes ?? 0);
                var diskWrite = g.Sum(r => r.DiskWriteBytes ?? 0);
                var anyDisk = g.Any(r => r.DiskReadBytes.HasValue || r.DiskWriteBytes.HasValue);

                return new AppStatRow(
                    ProcessName: g.Key,
                    UniqueUsers: g.Select(x => x.Username).Distinct(StringComparer.OrdinalIgnoreCase).Count(),
                    RunCount: g.Count(),
                    TotalOpenSeconds: seconds,
                    PeakCpuPercent: cpuPeaks.Count == 0 ? null : cpuPeaks.Max(),
                    PeakGpuPercent: gpuPeaks.Count == 0 ? null : gpuPeaks.Max(),
                    DiskReadBytes: anyDisk ? diskRead : null,
                    DiskWriteBytes: anyDisk ? diskWrite : null
                );
            })
            .OrderByDescending(a => a.TotalOpenSeconds)
            .ToList();
    }

    private static List<UsagePatternRow> BuildPatternRows(
        List<UserSession> sessions,
        List<ProcessRun> runs,
        string? appFilter,
        double? minRuntimeMinutes,
        double? maxRuntimeMinutes)
    {
        IEnumerable<ProcessRun> filteredRuns = runs;
        if (!string.IsNullOrWhiteSpace(appFilter))
            filteredRuns = filteredRuns.Where(r =>
                string.Equals(r.ProcessName, appFilter.Trim(), StringComparison.OrdinalIgnoreCase));

        if (minRuntimeMinutes is double minMin)
        {
            var minSec = minMin * 60;
            filteredRuns = filteredRuns.Where(r => RunDurationSeconds(r) >= minSec);
        }

        if (maxRuntimeMinutes is double maxMin)
        {
            var maxSec = maxMin * 60;
            filteredRuns = filteredRuns.Where(r => RunDurationSeconds(r) <= maxSec);
        }

        var runList = filteredRuns.ToList();
        var useAppFilter = !string.IsNullOrWhiteSpace(appFilter) || minRuntimeMinutes.HasValue || maxRuntimeMinutes.HasValue;

        // When app/runtime filters apply, attribute activity via matching process runs' users × day.
        // Otherwise use session ActiveSeconds rolled up by user × day-of-week.
        if (useAppFilter)
        {
            return runList
                .GroupBy(r => (
                    User: NormalizeUser(r.Username, null),
                    Dow: r.StartedAtUtc.DayOfWeek
                ))
                .Select(g => new UsagePatternRow(
                    Username: g.Key.User,
                    DayOfWeek: g.Key.Dow,
                    ActiveMinutes: g.Sum(r => RunDurationSeconds(r)) / 60.0,
                    SessionCount: g.Count(),
                    Source: "process"
                ))
                .OrderBy(r => r.Username, StringComparer.OrdinalIgnoreCase)
                .ThenBy(r => r.DayOfWeek)
                .ToList();
        }

        return sessions
            .GroupBy(s => (
                User: NormalizeUser(s.Username, s.Domain),
                Dow: s.StartedAtUtc.DayOfWeek
            ))
            .Select(g => new UsagePatternRow(
                Username: g.Key.User,
                DayOfWeek: g.Key.Dow,
                ActiveMinutes: g.Sum(s => s.ActiveSeconds) / 60.0,
                SessionCount: g.Count(),
                Source: "session"
            ))
            .OrderBy(r => r.Username, StringComparer.OrdinalIgnoreCase)
            .ThenBy(r => r.DayOfWeek)
            .ToList();
    }

    private static double RunDurationSeconds(ProcessRun r)
    {
        var end = r.EndedAtUtc ?? r.LastSeenAtUtc;
        return Math.Max(0, (end - r.StartedAtUtc).TotalSeconds);
    }

    private static string NormalizeUser(string username, string? domain)
    {
        if (string.IsNullOrWhiteSpace(username))
            return "unknown";
        if (username.Contains('\\') || string.IsNullOrWhiteSpace(domain))
            return username.Trim();
        return $"{domain.Trim()}\\{username.Trim()}";
    }
}

public sealed record StatsScopeOptions(
    IReadOnlyList<string> Regions,
    IReadOnlyList<string> Countries,
    IReadOnlyList<string> Offices,
    IReadOnlyList<string> Groups,
    IReadOnlyList<string> Machines,
    IReadOnlyList<MachineHierarchy.RegionNode> Tree);

public sealed record StatsSnapshot(
    StatsScopeKind ScopeKind,
    string? ScopeValue,
    DateTimeOffset FromUtc,
    DateTimeOffset ToUtc,
    int MachineCount,
    IReadOnlyList<string> MachineHostnames,
    IReadOnlyList<UserStatRow> UserStats,
    IReadOnlyList<AppStatRow> AppStats,
    IReadOnlyList<UsagePatternRow> UsagePatterns,
    IReadOnlyList<string> AppFilterOptions,
    bool GpuDiskSamplesPresent,
    bool HasAnyGpuData,
    bool HasAnyDiskData);

public sealed record UserStatRow(
    string Username,
    int LogonCount,
    long ActiveSeconds,
    long TotalSeconds,
    long DisconnectedSeconds,
    long RdpDisconnectedSeconds,
    double AvgSessionActiveSeconds,
    double AvgActivePerDaySeconds,
    int DistinctDays);

public sealed record AppStatRow(
    string ProcessName,
    int UniqueUsers,
    int RunCount,
    double TotalOpenSeconds,
    double? PeakCpuPercent,
    double? PeakGpuPercent,
    long? DiskReadBytes,
    long? DiskWriteBytes);

public sealed record UsagePatternRow(
    string Username,
    DayOfWeek DayOfWeek,
    double ActiveMinutes,
    int SessionCount,
    string Source);
