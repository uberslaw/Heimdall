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
        var appRows = BuildAppRows(runs, fromUtc, toUtc);
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

    /// <summary>Session-occupied % of wall clock for one host in [fromUtc, toUtc].</summary>
    public async Task<double> QueryMachineUtilisationPctAsync(
        string hostname,
        DateTimeOffset fromUtc,
        DateTimeOffset toUtc,
        CancellationToken ct = default)
    {
        var host = hostname.Trim();
        var machine = await db.Machines.AsNoTracking()
            .FirstOrDefaultAsync(m => m.Hostname == host, ct);
        if (machine is null)
            return 0;

        var now = DateTimeOffset.UtcNow;
        var sessions = (await db.Sessions.AsNoTracking().ToListAsync(ct))
            .Where(s => s.MachineId == machine.Id
                        && s.StartedAtUtc < toUtc
                        && (s.EndedAtUtc ?? s.LastObservedUtc) >= fromUtc)
            .ToList();

        var windowSeconds = Math.Max(1, (toUtc - fromUtc).TotalSeconds);
        var occupied = sessions.Sum(s => SessionOverlapSeconds(s, fromUtc, toUtc, now));
        return Math.Clamp(occupied / windowSeconds * 100.0, 0, 100);
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

        var allSessions = (await db.Sessions.AsNoTracking().ToListAsync(ct))
            .Where(s => s.MachineId == machine.Id)
            .ToList();

        var now = DateTimeOffset.UtcNow;
        var windowSeconds = Math.Max(1, (toUtc - fromUtc).TotalSeconds);
        var occupied = sessions.Sum(s => SessionOverlapSeconds(s, fromUtc, toUtc, now));
        var utilPct = Math.Clamp(occupied / windowSeconds * 100.0, 0, 100);

        var lastSession = allSessions.OrderByDescending(s => s.LastObservedUtc).FirstOrDefault();
        var (lastUserDepartedUtc, lastUserStillLoggedIn) = ResolveLastUserDeparture(lastSession);
        var onlineCutoff = now.AddMinutes(-5);

        var runs = FilterRunsInWindow(
            (await db.ProcessRuns.AsNoTracking().ToListAsync(ct))
                .Where(r => r.MachineId == machine.Id)
                .ToList(),
            fromUtc,
            toUtc);
        var processPaths = runs
            .Where(r => !string.IsNullOrWhiteSpace(r.ExecutablePath))
            .GroupBy(r => r.ProcessName, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(
                g => g.Key,
                g => g.OrderByDescending(r => r.LastSeenAtUtc).First().ExecutablePath,
                StringComparer.OrdinalIgnoreCase);

        return new MachineDetailSnapshot(
            Hostname: machine.Hostname,
            Group: machine.MachineGroup,
            IsOnline: machine.LastSeenUtc >= onlineCutoff,
            IsInUse: machine.IsInUse,
            LastSeenUtc: machine.LastSeenUtc,
            LastUser: lastSession is null
                ? null
                : NormalizeUser(lastSession.Username, lastSession.Domain),
            LastUserDepartedUtc: lastUserDepartedUtc,
            LastUserStillLoggedIn: lastUserStillLoggedIn,
            LastSessionType: lastSession?.SessionType,
            LastSessionState: lastSession?.State,
            UtilisationPct: utilPct,
            Sessions: new MachineSessionSummary(
                sessions.Count,
                sessions.Count(s => s.SessionType == SessionType.Local),
                sessions.Count(s => s.SessionType == SessionType.Rdp),
                sessions.Count(s => s.State != SessionState.Ended)),
            AppOptions: appOptions,
            Apps: filteredApps,
            ProcessPaths: processPaths,
            FromUtc: fromUtc,
            ToUtc: toUtc);
    }

    private static (DateTimeOffset? DepartedUtc, bool StillLoggedIn) ResolveLastUserDeparture(UserSession? session)
    {
        if (session is null)
            return (null, false);

        return session.State switch
        {
            SessionState.Active => (null, true),
            SessionState.Disconnected => (session.LastObservedUtc, false),
            SessionState.Ended => (session.EndedAtUtc ?? session.LastObservedUtc, false),
            _ => (session.EndedAtUtc ?? session.LastObservedUtc, false)
        };
    }

    public async Task<SessionsPageSnapshot> QuerySessionsPageAsync(
        DateTimeOffset fromUtc,
        DateTimeOffset toUtc,
        IReadOnlyList<string>? selectedHostnames = null,
        bool hideSystemProcesses = true,
        bool onlyDisconnectedWithApps = false,
        CancellationToken ct = default)
    {
        var now = DateTimeOffset.UtcNow;
        var machines = await db.Machines.AsNoTracking().ToListAsync(ct);
        var hostnameById = machines.ToDictionary(m => m.Id, m => m.Hostname);

        var sessions = FilterSessionsInWindow(
                await db.Sessions.AsNoTracking().ToListAsync(ct),
                fromUtc,
                toUtc)
            .ToList();

        var runs = FilterRunsInWindow(
                await db.ProcessRuns.AsNoTracking().ToListAsync(ct),
                fromUtc,
                toUtc)
            .ToList();

        HashSet<string>? noise = null;
        if (hideSystemProcesses)
        {
            var soe = await db.SoeApps.AsNoTracking().Select(s => s.ProcessName).ToListAsync(ct);
            noise = ProcessNoiseFilter.BuildExcludeSet(soeProcessNames: soe);
            runs = runs.Where(r => !ProcessNoiseFilter.IsExcluded(r.ProcessName, noise)).ToList();
        }

        var runsByMachine = runs.GroupBy(r => r.MachineId).ToDictionary(g => g.Key, g => g.ToList());

        var hostFilter = selectedHostnames is { Count: > 0 }
            ? selectedHostnames.ToHashSet(StringComparer.OrdinalIgnoreCase)
            : null;

        var machineSummaries = sessions
            .GroupBy(s => s.MachineId)
            .Select(g =>
            {
                var hostname = hostnameById.GetValueOrDefault(g.Key) ?? $"#{g.Key}";
                long active = 0, disconnected = 0;
                foreach (var s in g)
                {
                    var (a, d) = SessionMetricsInWindow(s, fromUtc, toUtc, now);
                    active += a;
                    disconnected += d;
                }

                return new SessionMachineSummaryRow(
                    Hostname: hostname,
                    SessionCount: g.Count(),
                    ActiveSeconds: active,
                    DisconnectedSeconds: disconnected,
                    LocalCount: g.Count(s => s.SessionType == SessionType.Local),
                    RdpCount: g.Count(s => s.SessionType == SessionType.Rdp),
                    OpenCount: g.Count(s => s.State != SessionState.Ended));
            })
            .OrderBy(m => m.Hostname, StringComparer.OrdinalIgnoreCase)
            .ToList();

        IEnumerable<UserSession> detailSessions = sessions;
        if (hostFilter is not null)
        {
            detailSessions = sessions.Where(s =>
            {
                var host = hostnameById.GetValueOrDefault(s.MachineId);
                return host is not null && hostFilter.Contains(host);
            });
        }

        var detailRows = new List<SessionDetailRow>();
        foreach (var s in detailSessions.OrderByDescending(s => s.LastObservedUtc))
        {
            var hostname = hostnameById.GetValueOrDefault(s.MachineId) ?? $"#{s.MachineId}";
            var machineRuns = runsByMachine.GetValueOrDefault(s.MachineId) ?? [];
            var matchingRuns = machineRuns
                .Where(r => UsersMatch(s, r) && RunOverlapsSession(r, s, now))
                .ToList();

            var appProcesses = matchingRuns
                .Select(r => r.ProcessName)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(n => n, StringComparer.OrdinalIgnoreCase)
                .ToList();

            var hadAppsWhileDisconnected = s.DisconnectedSeconds > 0 && appProcesses.Count > 0;

            if (onlyDisconnectedWithApps && !hadAppsWhileDisconnected)
                continue;

            detailRows.Add(new SessionDetailRow(
                Hostname: hostname,
                Username: s.Username,
                Domain: s.Domain,
                SessionType: s.SessionType,
                State: s.State,
                StartedAtUtc: s.StartedAtUtc,
                EndedAtUtc: s.EndedAtUtc,
                LastObservedUtc: s.LastObservedUtc,
                ActiveSeconds: s.ActiveSeconds,
                DisconnectedSeconds: s.DisconnectedSeconds,
                ClientName: s.ClientName,
                ClientAddress: s.ClientAddress,
                HadAppActivityWhileDisconnected: hadAppsWhileDisconnected,
                AppProcesses: appProcesses));
        }

        var scopedForTotals = hostFilter is null
            ? sessions
            : sessions.Where(s =>
            {
                var host = hostnameById.GetValueOrDefault(s.MachineId);
                return host is not null && hostFilter.Contains(host);
            }).ToList();

        long totalActive = 0, totalDisconnected = 0;
        foreach (var s in scopedForTotals)
        {
            var (a, d) = SessionMetricsInWindow(s, fromUtc, toUtc, now);
            totalActive += a;
            totalDisconnected += d;
        }

        var totals = new SessionsTotals(
            SessionCount: scopedForTotals.Count,
            ActiveSeconds: totalActive,
            DisconnectedSeconds: totalDisconnected,
            LocalCount: scopedForTotals.Count(s => s.SessionType == SessionType.Local),
            RdpCount: scopedForTotals.Count(s => s.SessionType == SessionType.Rdp),
            DisconnectedWithAppCount: detailRows.Count(r => r.HadAppActivityWhileDisconnected));

        return new SessionsPageSnapshot(
            FromUtc: fromUtc,
            ToUtc: toUtc,
            Totals: totals,
            MachineSummaries: machineSummaries,
            Sessions: detailRows,
            ShowSessionDetails: hostFilter is not null);
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
        var totalRunSeconds = ProcessRunMetrics.SumDurationSeconds(runs, fromUtc, toUtc);
        var totalSeconds = ProcessRunMetrics.UnionDurationSeconds(runs, fromUtc, toUtc);
        var avgConcurrent = ProcessRunMetrics.AvgConcurrentProcesses(runs, fromUtc, toUtc);
        var avgRunSeconds = runCount == 0 ? 0 : totalRunSeconds / runCount;
        var lastUsed = runs.Count == 0
            ? (DateTimeOffset?)null
            : runs.Max(r => r.LastSeenAtUtc);

        var machineRows = runs
            .GroupBy(r => r.MachineId)
            .Select(g =>
            {
                var groupRuns = g.ToList();
                var hostname = machineById.GetValueOrDefault(g.Key)?.Hostname ?? $"#{g.Key}";
                var unionSeconds = ProcessRunMetrics.UnionDurationSeconds(groupRuns, fromUtc, toUtc);
                var sumSeconds = ProcessRunMetrics.SumDurationSeconds(groupRuns, fromUtc, toUtc);
                var count = groupRuns.Count;
                return new AppMachineUsageRow(
                    hostname,
                    count,
                    unionSeconds,
                    ProcessRunMetrics.AvgConcurrentProcesses(groupRuns, fromUtc, toUtc),
                    count == 0 ? 0 : sumSeconds / count,
                    groupRuns.Max(r => r.LastSeenAtUtc));
            })
            .OrderByDescending(r => r.TotalOpenSeconds)
            .ToList();

        var userRows = runs
            .GroupBy(r => NormalizeUser(r.Username, null), StringComparer.OrdinalIgnoreCase)
            .Select(g =>
            {
                var groupRuns = g.ToList();
                var unionSeconds = ProcessRunMetrics.UnionDurationSeconds(groupRuns, fromUtc, toUtc);
                var sumSeconds = ProcessRunMetrics.SumDurationSeconds(groupRuns, fromUtc, toUtc);
                var count = groupRuns.Count;
                return new AppUserUsageRow(
                    g.Key,
                    count,
                    unionSeconds,
                    ProcessRunMetrics.AvgConcurrentProcesses(groupRuns, fromUtc, toUtc),
                    count == 0 ? 0 : sumSeconds / count,
                    groupRuns.Max(r => r.LastSeenAtUtc));
            })
            .OrderByDescending(r => r.TotalOpenSeconds)
            .ToList();

        return new ApplicationDetailSnapshot(
            ProcessName: process,
            DisplayName: displayNames.GetValueOrDefault(process, process),
            RunCount: runCount,
            TotalOpenSeconds: totalSeconds,
            AvgConcurrentProcesses: avgConcurrent,
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

    private static List<AppStatRow> BuildAppRows(
        List<ProcessRun> runs,
        DateTimeOffset fromUtc,
        DateTimeOffset toUtc)
    {
        return runs
            .GroupBy(r => r.ProcessName, StringComparer.OrdinalIgnoreCase)
            .Select(g =>
            {
                var groupRuns = g.ToList();
                var seconds = ProcessRunMetrics.UnionDurationSeconds(groupRuns, fromUtc, toUtc);
                var avgConcurrent = ProcessRunMetrics.AvgConcurrentProcesses(groupRuns, fromUtc, toUtc);
                var cpuPeaks = groupRuns.Where(r => r.PeakCpuPercent.HasValue).Select(r => r.PeakCpuPercent!.Value).ToList();
                var gpuPeaks = groupRuns.Where(r => r.PeakGpuPercent.HasValue).Select(r => r.PeakGpuPercent!.Value).ToList();
                var diskRead = groupRuns.Sum(r => r.DiskReadBytes ?? 0);
                var diskWrite = groupRuns.Sum(r => r.DiskWriteBytes ?? 0);
                var anyDisk = groupRuns.Any(r => r.DiskReadBytes.HasValue || r.DiskWriteBytes.HasValue);

                return new AppStatRow(
                    ProcessName: g.Key,
                    UniqueUsers: groupRuns.Select(x => x.Username).Distinct(StringComparer.OrdinalIgnoreCase).Count(),
                    RunCount: groupRuns.Count,
                    AvgConcurrentProcesses: avgConcurrent,
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

    private static double RunDurationSeconds(ProcessRun r) =>
        ProcessRunMetrics.RunDurationSeconds(r);

    private static double SessionOverlapSeconds(
        UserSession s, DateTimeOffset fromUtc, DateTimeOffset toUtc, DateTimeOffset now)
    {
        var start = s.StartedAtUtc < fromUtc ? fromUtc : s.StartedAtUtc;
        var end = s.EndedAtUtc ?? now;
        if (end > toUtc) end = toUtc;
        if (end < fromUtc) return 0;
        return Math.Max(0, (end - start).TotalSeconds);
    }

    private static List<UserSession> FilterSessionsInWindow(
        List<UserSession> sessions, DateTimeOffset fromUtc, DateTimeOffset toUtc) =>
        sessions.Where(s => s.StartedAtUtc < toUtc && (s.EndedAtUtc ?? s.LastObservedUtc) >= fromUtc).ToList();

    private static (long Active, long Disconnected) SessionMetricsInWindow(
        UserSession s, DateTimeOffset fromUtc, DateTimeOffset toUtc, DateTimeOffset now)
    {
        var overlap = SessionOverlapSeconds(s, fromUtc, toUtc, now);
        if (overlap <= 0)
            return (0, 0);

        var total = s.ActiveSeconds + s.DisconnectedSeconds;
        if (total <= 0)
            return ((long)overlap, 0);

        var ratio = overlap / total;
        return ((long)(s.ActiveSeconds * ratio), (long)(s.DisconnectedSeconds * ratio));
    }

    private static bool RunOverlapsSession(ProcessRun run, UserSession session, DateTimeOffset now)
    {
        var sessionEnd = session.EndedAtUtc ?? session.LastObservedUtc;
        if (sessionEnd < now)
            sessionEnd = session.LastObservedUtc;

        var runEnd = run.EndedAtUtc ?? run.LastSeenAtUtc;
        return run.StartedAtUtc < sessionEnd && runEnd > session.StartedAtUtc;
    }

    private static bool UsersMatch(UserSession session, ProcessRun run)
    {
        var sessionKeys = UserMatchKeys(session.Username, session.Domain).ToHashSet(StringComparer.OrdinalIgnoreCase);
        return UserMatchKeys(run.Username, null).Any(sessionKeys.Contains);
    }

    private static IEnumerable<string> UserMatchKeys(string username, string? domain)
    {
        var normalized = NormalizeUser(username, domain);
        if (string.IsNullOrWhiteSpace(normalized))
            yield break;

        yield return normalized;
        var slash = normalized.IndexOf('\\');
        if (slash >= 0 && slash < normalized.Length - 1)
            yield return normalized[(slash + 1)..];
        else
            yield return normalized;
    }

    private static List<ProcessRun> FilterRunsInWindow(
        List<ProcessRun> runs, DateTimeOffset fromUtc, DateTimeOffset toUtc) =>
        runs.Where(r => r.StartedAtUtc < toUtc && (r.EndedAtUtc ?? r.LastSeenAtUtc) >= fromUtc).ToList();

    private async Task<Dictionary<string, string>> BuildDisplayNameMapAsync(CancellationToken ct)
    {
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        var catalog = await db.ProcessCatalogEntries.AsNoTracking()
            .Where(c => c.DisplayName != null && c.DisplayName != "")
            .Select(c => new { c.ProcessName, c.DisplayName, c.LastSeenUtc })
            .ToListAsync(ct);
        foreach (var row in catalog
                     .GroupBy(c => c.ProcessName, StringComparer.OrdinalIgnoreCase)
                     .Select(g => g.OrderByDescending(x => x.LastSeenUtc).First()))
        {
            if (!string.IsNullOrWhiteSpace(row.DisplayName))
                map[row.ProcessName] = row.DisplayName!;
        }

        var listEntries = await db.AppListEntries.AsNoTracking()
            .Where(e => e.DisplayName != null && e.DisplayName != "")
            .Select(e => new { e.ProcessName, e.DisplayName })
            .ToListAsync(ct);
        foreach (var e in listEntries)
        {
            if (string.IsNullOrWhiteSpace(e.DisplayName))
                continue;
            // Catalog wins when already set; otherwise App list entry.
            if (!map.ContainsKey(e.ProcessName))
                map[e.ProcessName] = e.DisplayName!;
        }

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

    /// <summary>
    /// Period cards on Machine page: session mix, top users, top CPU/GPU apps, top disk apps + machine net.
    /// </summary>
    public async Task<MachinePeriodStatsCards?> QueryMachinePeriodStatsAsync(
        string hostname,
        DateTimeOffset fromUtc,
        DateTimeOffset toUtc,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(hostname))
            return null;

        var host = hostname.Trim();
        var machine = await db.Machines.AsNoTracking()
            .FirstOrDefaultAsync(m => m.Hostname == host, ct);
        if (machine is null)
            return null;

        var sessions = (await db.Sessions.AsNoTracking()
                .Where(s => s.MachineId == machine.Id)
                .ToListAsync(ct))
            .Where(s => s.StartedAtUtc < toUtc && (s.EndedAtUtc ?? s.LastObservedUtc) >= fromUtc)
            .ToList();

        var sessionSummary = new MachineSessionSummary(
            sessions.Count,
            sessions.Count(s => s.SessionType == SessionType.Local),
            sessions.Count(s => s.SessionType == SessionType.Rdp),
            sessions.Count(s => s.State != SessionState.Ended));

        var topUsers = sessions
            .GroupBy(s => NormalizeUser(s.Username, s.Domain), StringComparer.OrdinalIgnoreCase)
            .Select(g => new MachineStatsRankRow(
                Label: Heimdall.Shared.UsernameDisplay.Format(g.Key),
                Detail: g.Count() == 1 ? "1 session" : $"{g.Count()} sessions",
                SortValue: g.Count()))
            .OrderByDescending(r => r.SortValue)
            .ThenBy(r => r.Label, StringComparer.OrdinalIgnoreCase)
            .Take(3)
            .ToList();

        var displayNames = await BuildDisplayNameMapAsync(ct);
        var snaps = await FleetSnapshotQuery.LoadForMachinesAsync(db, [machine.Id], fromUtc, toUtc, ct);
        snaps = snaps.OrderBy(s => s.SampledAtUtc).ToList();

        var cpuGpuWeights = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
        var diskWeights = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
        double netRxBytes = 0;
        double netTxBytes = 0;
        var hasFleetProcess = false;

        for (var i = 0; i < snaps.Count; i++)
        {
            var s = snaps[i];
            if (s.SampledAtUtc < fromUtc || s.SampledAtUtc >= toUtc)
                continue;

            var dtSec = FleetSampleDtSeconds(snaps, i);
            var dtH = dtSec / 3600.0;
            netRxBytes += (s.NetworkInMBps ?? 0) * dtSec * 1024 * 1024;
            netTxBytes += (s.NetworkOutMBps ?? 0) * dtSec * 1024 * 1024;

            foreach (var p in DeserializeTopProcesses(s.TopCpuProcessesJson))
            {
                var piece = Math.Max(0, p.Value) / 100.0 * dtH;
                if (piece <= 0) continue;
                hasFleetProcess = true;
                cpuGpuWeights[p.ProcessName] = cpuGpuWeights.GetValueOrDefault(p.ProcessName) + piece;
            }

            foreach (var p in DeserializeTopProcesses(s.TopGpuProcessesJson))
            {
                var piece = Math.Max(0, p.Value) / 100.0 * dtH;
                if (piece <= 0) continue;
                hasFleetProcess = true;
                cpuGpuWeights[p.ProcessName] = cpuGpuWeights.GetValueOrDefault(p.ProcessName) + piece;
            }

            foreach (var p in DeserializeTopProcesses(s.TopDiskReadProcessesJson)
                         .Concat(DeserializeTopProcesses(s.TopDiskWriteProcessesJson)))
            {
                var piece = Math.Max(0, p.Value) * dtSec; // Value = bytes/sec
                if (piece <= 0) continue;
                hasFleetProcess = true;
                diskWeights[p.ProcessName] = diskWeights.GetValueOrDefault(p.ProcessName) + piece;
            }
        }

        if (!hasFleetProcess)
        {
            var runs = FilterRunsInWindow(
                await db.ProcessRuns.AsNoTracking().Where(r => r.MachineId == machine.Id).ToListAsync(ct),
                fromUtc,
                toUtc);

            foreach (var g in runs.GroupBy(r => r.ProcessName, StringComparer.OrdinalIgnoreCase))
            {
                var group = g.ToList();
                var openSec = ProcessRunMetrics.UnionDurationSeconds(group, fromUtc, toUtc);
                var peakCpu = group.Where(r => r.PeakCpuPercent.HasValue).Select(r => r.PeakCpuPercent!.Value).DefaultIfEmpty(0).Max();
                var peakGpu = group.Where(r => r.PeakGpuPercent.HasValue).Select(r => r.PeakGpuPercent!.Value).DefaultIfEmpty(0).Max();
                var computeH = Math.Max(peakCpu, peakGpu) / 100.0 * (openSec / 3600.0);
                if (computeH > 0)
                    cpuGpuWeights[g.Key] = computeH;

                var disk = group.Sum(r => (r.DiskReadBytes ?? 0) + (r.DiskWriteBytes ?? 0));
                if (disk > 0)
                    diskWeights[g.Key] = disk;
            }
        }

        string AppLabel(string processName) =>
            displayNames.TryGetValue(processName, out var dn) && !string.IsNullOrWhiteSpace(dn)
                ? dn
                : processName;

        var topCompute = cpuGpuWeights
            .OrderByDescending(kv => kv.Value)
            .Take(3)
            .Select(kv => new MachineStatsRankRow(
                Label: AppLabel(kv.Key),
                Detail: MachineUtilisationService.FormatHoursCompact(kv.Value) + "h CPU/GPU",
                SortValue: kv.Value))
            .ToList();

        var topDisk = diskWeights
            .OrderByDescending(kv => kv.Value)
            .Take(3)
            .Select(kv => new MachineStatsRankRow(
                Label: AppLabel(kv.Key),
                Detail: MachineUtilisationService.FormatBytesCompact(kv.Value) + " disk",
                SortValue: kv.Value))
            .ToList();

        var netDetail = (netRxBytes > 0 || netTxBytes > 0)
            ? $"Net Rx {MachineUtilisationService.FormatBytesCompact(netRxBytes)} · Tx {MachineUtilisationService.FormatBytesCompact(netTxBytes)}"
            : null;

        return new MachinePeriodStatsCards(
            Sessions: sessionSummary,
            TopUsers: topUsers,
            TopComputeApps: topCompute,
            TopIoApps: topDisk,
            NetworkSummary: netDetail);
    }

    private static double FleetSampleDtSeconds(IReadOnlyList<FleetMetricSnapshot> snaps, int i)
    {
        var interval = MachineUtilisationService.SampleInterval.TotalSeconds;
        double dt = interval;
        if (i + 1 < snaps.Count)
        {
            dt = (snaps[i + 1].SampledAtUtc - snaps[i].SampledAtUtc).TotalSeconds;
            if (dt <= 0 || dt > interval * 4)
                dt = interval;
        }

        return dt;
    }

    private static readonly System.Text.Json.JsonSerializerOptions TopProcessJsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    private static List<TopProcessSampleDto> DeserializeTopProcesses(string? json)
    {
        if (string.IsNullOrWhiteSpace(json) || json == "[]")
            return [];
        try
        {
            return System.Text.Json.JsonSerializer.Deserialize<List<TopProcessSampleDto>>(json, TopProcessJsonOptions) ?? [];
        }
        catch
        {
            return [];
        }
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
    double AvgConcurrentProcesses,
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
    DateTimeOffset? LastUserDepartedUtc,
    bool LastUserStillLoggedIn,
    SessionType? LastSessionType,
    SessionState? LastSessionState,
    double UtilisationPct,
    MachineSessionSummary Sessions,
    IReadOnlyList<AppFilterOption> AppOptions,
    IReadOnlyList<AppStatRow> Apps,
    IReadOnlyDictionary<string, string?> ProcessPaths,
    DateTimeOffset FromUtc,
    DateTimeOffset ToUtc);

public sealed record MachineSessionSummary(
    int TotalCount,
    int LocalCount,
    int RdpCount,
    int OpenCount);

public sealed record MachineStatsRankRow(
    string Label,
    string Detail,
    double SortValue);

public sealed record MachinePeriodStatsCards(
    MachineSessionSummary Sessions,
    IReadOnlyList<MachineStatsRankRow> TopUsers,
    IReadOnlyList<MachineStatsRankRow> TopComputeApps,
    IReadOnlyList<MachineStatsRankRow> TopIoApps,
    string? NetworkSummary);

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
    double AvgConcurrentProcesses,
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
    double AvgConcurrentProcesses,
    double AvgRunSeconds,
    DateTimeOffset LastUsedUtc);

public sealed record AppUserUsageRow(
    string Username,
    int RunCount,
    double TotalOpenSeconds,
    double AvgConcurrentProcesses,
    double AvgRunSeconds,
    DateTimeOffset LastUsedUtc);

public sealed record SessionsPageSnapshot(
    DateTimeOffset FromUtc,
    DateTimeOffset ToUtc,
    SessionsTotals Totals,
    IReadOnlyList<SessionMachineSummaryRow> MachineSummaries,
    IReadOnlyList<SessionDetailRow> Sessions,
    bool ShowSessionDetails);

public sealed record SessionsTotals(
    int SessionCount,
    long ActiveSeconds,
    long DisconnectedSeconds,
    int LocalCount,
    int RdpCount,
    int DisconnectedWithAppCount);

public sealed record SessionMachineSummaryRow(
    string Hostname,
    int SessionCount,
    long ActiveSeconds,
    long DisconnectedSeconds,
    int LocalCount,
    int RdpCount,
    int OpenCount);

public sealed record SessionDetailRow(
    string Hostname,
    string Username,
    string? Domain,
    SessionType SessionType,
    SessionState State,
    DateTimeOffset StartedAtUtc,
    DateTimeOffset? EndedAtUtc,
    DateTimeOffset LastObservedUtc,
    long ActiveSeconds,
    long DisconnectedSeconds,
    string? ClientName,
    string? ClientAddress,
    bool HadAppActivityWhileDisconnected,
    IReadOnlyList<string> AppProcesses);
