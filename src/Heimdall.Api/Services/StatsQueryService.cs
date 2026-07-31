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

public sealed class StatsQueryService(HeimdallDbContext db, ConfigService config)
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

    public async Task<MachineDetailSnapshot?> QueryMachineDetailAsync(
        string hostname,
        DateTimeOffset fromUtc,
        DateTimeOffset toUtc,
        IReadOnlyList<string>? selectedApps = null,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(hostname))
            return null;

        var host = hostname.Trim();
        var machines = await db.Machines.AsNoTracking().ToListAsync(ct);
        var machine = machines.FirstOrDefault(m =>
            string.Equals(m.Hostname, host, StringComparison.OrdinalIgnoreCase));
        if (machine is null)
            return null;

        var snapshot = await QueryAsync(StatsScopeKind.Machine, host, fromUtc, toUtc, ct: ct);
        var agentConfig = await config.ResolveForHostAsync(host, ct);
        var trackedSet = agentConfig.IncludeProcesses.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var displayNames = await BuildDisplayNameMapAsync(ct);

        var seenApps = snapshot.AppFilterOptions.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var optionNames = trackedSet
            .Union(seenApps, StringComparer.OrdinalIgnoreCase)
            .OrderBy(n => n, StringComparer.OrdinalIgnoreCase)
            .ToList();

        var appOptions = optionNames
            .Select(n => new AppFilterOption(
                n,
                displayNames.GetValueOrDefault(n, n),
                trackedSet.Contains(n),
                seenApps.Contains(n)))
            .ToList();

        var filterSet = selectedApps is { Count: > 0 }
            ? selectedApps.ToHashSet(StringComparer.OrdinalIgnoreCase)
            : null;

        var filteredApps = filterSet is null
            ? snapshot.AppStats
            : snapshot.AppStats.Where(a => filterSet.Contains(a.ProcessName)).ToList();

        var sessions = (await db.Sessions.AsNoTracking().ToListAsync(ct))
            .Where(s => s.MachineId == machine.Id
                        && s.StartedAtUtc < toUtc
                        && (s.EndedAtUtc ?? s.LastObservedUtc) >= fromUtc)
            .ToList();

        var now = DateTimeOffset.UtcNow;
        var windowSeconds = Math.Max(1, (toUtc - fromUtc).TotalSeconds);
        var occupied = sessions.Sum(s => SessionOverlapSeconds(s, fromUtc, toUtc, now));
        var utilPct = Math.Clamp(occupied / windowSeconds * 100.0, 0, 100);

        var lastSession = sessions.OrderByDescending(s => s.LastObservedUtc).FirstOrDefault();
        var onlineCutoff = now.AddMinutes(-5);

        return new MachineDetailSnapshot(
            Hostname: machine.Hostname,
            Group: machine.MachineGroup,
            IsOnline: machine.LastSeenUtc >= onlineCutoff,
            IsInUse: machine.IsInUse,
            LastSeenUtc: machine.LastSeenUtc,
            LastUser: lastSession is null
                ? null
                : NormalizeUser(lastSession.Username, lastSession.Domain),
            LastSessionType: lastSession?.SessionType,
            UtilisationPct: utilPct,
            Sessions: new MachineSessionSummary(
                sessions.Count,
                sessions.Count(s => s.SessionType == SessionType.Local),
                sessions.Count(s => s.SessionType == SessionType.Rdp),
                sessions.Count(s => s.State != SessionState.Ended)),
            AppOptions: appOptions,
            Apps: filteredApps,
            FromUtc: fromUtc,
            ToUtc: toUtc);
    }

    public async Task<ApplicationDetailSnapshot> QueryApplicationDetailAsync(
        string processName,
        DateTimeOffset fromUtc,
        DateTimeOffset toUtc,
        string? hostnameFilter = null,
        CancellationToken ct = default)
    {
        var process = processName.Trim();
        var machines = await db.Machines.AsNoTracking().ToListAsync(ct);
        var machineById = machines.ToDictionary(m => m.Id);
        var displayNames = await BuildDisplayNameMapAsync(ct);

        var allRuns = FilterRunsInWindow(
                await db.ProcessRuns.AsNoTracking().ToListAsync(ct),
                fromUtc,
                toUtc)
            .Where(r => string.Equals(r.ProcessName, process, StringComparison.OrdinalIgnoreCase))
            .ToList();

        var machineFilterOptions = allRuns
            .Select(r => machineById.GetValueOrDefault(r.MachineId)?.Hostname)
            .Where(h => !string.IsNullOrWhiteSpace(h))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(h => h, StringComparer.OrdinalIgnoreCase)
            .Cast<string>()
            .ToList();

        var runs = allRuns;
        if (!string.IsNullOrWhiteSpace(hostnameFilter))
        {
            var host = hostnameFilter.Trim();
            var machineId = machines
                .FirstOrDefault(m => string.Equals(m.Hostname, host, StringComparison.OrdinalIgnoreCase))
                ?.Id;
            runs = machineId is null
                ? []
                : allRuns.Where(r => r.MachineId == machineId.Value).ToList();
        }

        var runCount = runs.Count;
        var totalSeconds = runs.Sum(RunDurationSeconds);
        var avgRunSeconds = runCount == 0 ? 0 : totalSeconds / runCount;
        var lastUsed = runs.Count == 0
            ? (DateTimeOffset?)null
            : runs.Max(r => r.LastSeenAtUtc);

        var machineRows = runs
            .GroupBy(r => r.MachineId)
            .Select(g =>
            {
                var hostname = machineById.GetValueOrDefault(g.Key)?.Hostname ?? $"#{g.Key}";
                var seconds = g.Sum(RunDurationSeconds);
                var count = g.Count();
                return new AppMachineUsageRow(
                    hostname,
                    count,
                    seconds,
                    count == 0 ? 0 : seconds / count,
                    g.Max(r => r.LastSeenAtUtc));
            })
            .OrderByDescending(r => r.TotalOpenSeconds)
            .ToList();

        var userRows = runs
            .GroupBy(r => NormalizeUser(r.Username, null), StringComparer.OrdinalIgnoreCase)
            .Select(g =>
            {
                var seconds = g.Sum(RunDurationSeconds);
                var count = g.Count();
                return new AppUserUsageRow(
                    g.Key,
                    count,
                    seconds,
                    count == 0 ? 0 : seconds / count,
                    g.Max(r => r.LastSeenAtUtc));
            })
            .OrderByDescending(r => r.TotalOpenSeconds)
            .ToList();

        return new ApplicationDetailSnapshot(
            ProcessName: process,
            DisplayName: displayNames.GetValueOrDefault(process, process),
            RunCount: runCount,
            TotalOpenSeconds: totalSeconds,
            UniqueUsers: userRows.Count,
            UniqueMachines: machineRows.Count,
            AvgRunSeconds: avgRunSeconds,
            LastUsedUtc: lastUsed,
            Machines: machineRows,
            Users: userRows,
            MachineFilterOptions: machineFilterOptions,
            FromUtc: fromUtc,
            ToUtc: toUtc);
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
                    .Where(s => s.SessionType == SessionType.Rdp
                                || !string.IsNullOrWhiteSpace(s.ClientName)
                                || (!string.IsNullOrWhiteSpace(s.ClientAddress)
                                    && s.ClientAddress is not ("0.0.0.0" or "::" or "::1")))
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

    private static double SessionOverlapSeconds(
        UserSession s, DateTimeOffset fromUtc, DateTimeOffset toUtc, DateTimeOffset now)
    {
        var start = s.StartedAtUtc < fromUtc ? fromUtc : s.StartedAtUtc;
        var end = s.EndedAtUtc ?? now;
        if (end > toUtc) end = toUtc;
        if (end < fromUtc) return 0;
        return Math.Max(0, (end - start).TotalSeconds);
    }

    private static List<ProcessRun> FilterRunsInWindow(
        List<ProcessRun> runs, DateTimeOffset fromUtc, DateTimeOffset toUtc) =>
        runs.Where(r => r.StartedAtUtc < toUtc && (r.EndedAtUtc ?? r.LastSeenAtUtc) >= fromUtc).ToList();

    private async Task<Dictionary<string, string>> BuildDisplayNameMapAsync(CancellationToken ct)
    {
        var known = await db.KnownApps.AsNoTracking().ToListAsync(ct);
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var app in known)
            map[app.ProcessName] = app.DisplayName;
        return map;
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

public sealed record MachineDetailSnapshot(
    string Hostname,
    string? Group,
    bool IsOnline,
    bool IsInUse,
    DateTimeOffset LastSeenUtc,
    string? LastUser,
    SessionType? LastSessionType,
    double UtilisationPct,
    MachineSessionSummary Sessions,
    IReadOnlyList<AppFilterOption> AppOptions,
    IReadOnlyList<AppStatRow> Apps,
    DateTimeOffset FromUtc,
    DateTimeOffset ToUtc);

public sealed record MachineSessionSummary(
    int TotalCount,
    int LocalCount,
    int RdpCount,
    int OpenCount);

public sealed record AppFilterOption(
    string ProcessName,
    string DisplayName,
    bool IsTracked,
    bool HasData);

public sealed record ApplicationDetailSnapshot(
    string ProcessName,
    string DisplayName,
    int RunCount,
    double TotalOpenSeconds,
    int UniqueUsers,
    int UniqueMachines,
    double AvgRunSeconds,
    DateTimeOffset? LastUsedUtc,
    IReadOnlyList<AppMachineUsageRow> Machines,
    IReadOnlyList<AppUserUsageRow> Users,
    IReadOnlyList<string> MachineFilterOptions,
    DateTimeOffset FromUtc,
    DateTimeOffset ToUtc);

public sealed record AppMachineUsageRow(
    string Hostname,
    int RunCount,
    double TotalOpenSeconds,
    double AvgRunSeconds,
    DateTimeOffset LastUsedUtc);

public sealed record AppUserUsageRow(
    string Username,
    int RunCount,
    double TotalOpenSeconds,
    double AvgRunSeconds,
    DateTimeOffset LastUsedUtc);
