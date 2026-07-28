using Heimdall.Api.Data;
using Heimdall.Shared.Contracts;
using Microsoft.EntityFrameworkCore;

namespace Heimdall.Api.Services;

/// <summary>
/// Socratize: retrospective interrogation of already-collected Heimdall data for one machine.
/// Product name kept for the machine deep-dive / cost-justification brief (POC).
/// </summary>
public sealed class SocratizeQueryService(HeimdallDbContext db)
{
    public async Task<SocratizeBrief?> BuildBriefAsync(
        string hostname,
        DateTimeOffset fromUtc,
        DateTimeOffset toUtc,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(hostname))
            return null;

        var host = hostname.Trim();
        var machines = await db.Machines.AsNoTracking().ToListAsync(ct);
        foreach (var m in machines)
            MachineHierarchy.EnsureDefaults(m);

        var machine = machines.FirstOrDefault(m =>
            string.Equals(m.Hostname, host, StringComparison.OrdinalIgnoreCase));
        if (machine is null)
            return null;

        // SQLite EF DateTimeOffset filters are unreliable — load then filter in memory.
        var sessions = (await db.Sessions.AsNoTracking().ToListAsync(ct))
            .Where(s => s.MachineId == machine.Id
                        && s.StartedAtUtc < toUtc
                        && (s.EndedAtUtc ?? s.LastObservedUtc) >= fromUtc)
            .ToList();

        var runs = (await db.ProcessRuns.AsNoTracking().ToListAsync(ct))
            .Where(r => r.MachineId == machine.Id
                        && r.StartedAtUtc < toUtc
                        && (r.EndedAtUtc ?? r.LastSeenAtUtc) >= fromUtc)
            .ToList();

        var people = await db.PersonTeams.AsNoTracking().Include(p => p.Team).ToListAsync(ct);
        var policies = await db.MetricPolicies.AsNoTracking()
            .Where(p => p.IsEnabled)
            .ToListAsync(ct);
        var criteria = await db.UtilizationCriteria.AsNoTracking().FirstOrDefaultAsync(ct)
                       ?? new UtilizationCriteria { Scope = "Global" };
        var licenseCosts = await db.AppLicenseCosts.AsNoTracking().ToListAsync(ct);
        var licenseByProcess = licenseCosts.ToDictionary(
            c => ConfigService.NormalizeProcessName(c.ProcessName),
            c => c,
            StringComparer.OrdinalIgnoreCase);

        var windowSeconds = Math.Max(1, (toUtc - fromUtc).TotalSeconds);
        var periodDays = Math.Max(1.0 / 24.0, (toUtc - fromUtc).TotalDays);
        var occupiedSeconds = sessions.Sum(s =>
        {
            var start = s.StartedAtUtc < fromUtc ? fromUtc : s.StartedAtUtc;
            var end = s.EndedAtUtc ?? (s.LastObservedUtc > toUtc ? toUtc : s.LastObservedUtc);
            if (end > toUtc) end = toUtc;
            if (end < start) return 0.0;
            return (end - start).TotalSeconds;
        });
        var utilPct = Math.Clamp(occupiedSeconds / windowSeconds * 100.0, 0, 100);

        var activeSeconds = sessions.Sum(s => s.ActiveSeconds);
        var disconnectedSeconds = sessions.Sum(s => s.DisconnectedSeconds);

        long localActive = 0, localDisconnected = 0, inboundActive = 0, inboundDisconnected = 0;
        foreach (var s in sessions)
        {
            var (la, ld, ia, id) = AccountSessionTime(s);
            localActive += la;
            localDisconnected += ld;
            inboundActive += ia;
            inboundDisconnected += id;
        }

        var outboundSeconds = runs
            .Where(r => RdpClientProcesses.IsRdpClient(r.ProcessName))
            .Sum(RunDurationSeconds);

        var sessionAccounted = localActive + localDisconnected + inboundActive + inboundDisconnected;
        if (sessionAccounted <= 0)
            sessionAccounted = activeSeconds + disconnectedSeconds;

        var threeWayAccounted = sessionAccounted + outboundSeconds;
        var rdpSharePct = threeWayAccounted <= 0
            ? 0
            : (inboundActive + inboundDisconnected) * 100.0 / threeWayAccounted;
        var localSharePct = threeWayAccounted <= 0
            ? 0
            : (localActive + localDisconnected) * 100.0 / threeWayAccounted;
        var outboundSharePct = threeWayAccounted <= 0
            ? 0
            : outboundSeconds * 100.0 / threeWayAccounted;
        var inboundShareOfSession = sessionAccounted <= 0
            ? 0
            : (inboundActive + inboundDisconnected) * 100.0 / sessionAccounted;
        var rdpIdleShareOfRdp = (inboundActive + inboundDisconnected) <= 0
            ? 0
            : inboundDisconnected * 100.0 / (inboundActive + inboundDisconnected);

        var localSessions = sessions.Where(s =>
            s.SessionType == SessionType.Local && !LooksLikeInboundRdpFingerprint(s)).ToList();
        var inboundSessions = sessions.Where(s =>
            s.SessionType == SessionType.Rdp || LooksLikeInboundRdpFingerprint(s)).ToList();

        var users = sessions
            .GroupBy(s => NormalizeUser(s.Username, s.Domain), StringComparer.OrdinalIgnoreCase)
            .Select(g =>
            {
                var team = MatchTeam(people, g.Key);
                return new SocratizeUserRow(
                    Username: g.Key,
                    TeamName: team?.Team.Name,
                    TeamCode: team?.Team.Code,
                    LogonCount: g.Count(),
                    ActiveSeconds: g.Sum(s => s.ActiveSeconds),
                    DisconnectedSeconds: g.Sum(s => s.DisconnectedSeconds),
                    RdpLogons: g.Count(s => s.SessionType == SessionType.Rdp || LooksLikeInboundRdpFingerprint(s)),
                    LocalLogons: g.Count(s => s.SessionType == SessionType.Local && !LooksLikeInboundRdpFingerprint(s))
                );
            })
            .OrderByDescending(u => u.ActiveSeconds)
            .ToList();

        var teams = users
            .Where(u => !string.IsNullOrWhiteSpace(u.TeamName))
            .GroupBy(u => u.TeamName!, StringComparer.OrdinalIgnoreCase)
            .Select(g => new SocratizeTeamRow(g.Key, g.Count(), g.Sum(u => u.ActiveSeconds)))
            .OrderByDescending(t => t.ActiveSeconds)
            .ToList();

        var apps = runs
            .GroupBy(r => r.ProcessName, StringComparer.OrdinalIgnoreCase)
            .Select(g =>
            {
                var seconds = g.Sum(RunDurationSeconds);
                var cpuPeaks = g.Where(r => r.PeakCpuPercent.HasValue).Select(r => r.PeakCpuPercent!.Value).ToList();
                var key = ConfigService.NormalizeProcessName(g.Key);
                licenseByProcess.TryGetValue(key, out var lic);
                double? costPerHour = null;
                if (lic is not null && lic.LicenseCostPerYear > 0 && seconds > 0)
                {
                    var hours = seconds / 3600.0;
                    var annualizedHours = hours * (365.0 / periodDays);
                    costPerHour = lic.LicenseCostPerYear / Math.Max(annualizedHours, 0.01);
                }

                return new SocratizeAppRow(
                    ProcessName: g.Key,
                    RunCount: g.Count(),
                    TotalOpenSeconds: seconds,
                    UniqueUsers: g.Select(x => x.Username).Distinct(StringComparer.OrdinalIgnoreCase).Count(),
                    PeakCpuPercent: cpuPeaks.Count == 0 ? null : cpuPeaks.Max(),
                    LicenseCostPerYear: lic?.LicenseCostPerYear,
                    CostPerHour: costPerHour
                );
            })
            .OrderByDescending(a => a.TotalOpenSeconds)
            .ToList();

        var appTotal = apps.Sum(a => a.TotalOpenSeconds);
        var topAppShare = appTotal <= 0 || apps.Count == 0
            ? 0
            : apps[0].TotalOpenSeconds * 100.0 / appTotal;

        var scopedPolicies = policies
            .Where(p => ConfigService.MatchesScope(p.Scope, p.ScopeValue, machine, machine.Hostname))
            .OrderByDescending(p => ConfigService.ScopeRank(p.Scope))
            .ThenBy(p => p.Name)
            .Select(p => new SocratizePolicyRow(
                p.Name,
                p.MetricType.ToString(),
                p.Scope.ToString(),
                p.ScopeValue,
                FormatThresholds(p)
            ))
            .ToList();

        var hasGpuSamples = runs.Any(r => r.PeakGpuPercent is > 0);
        var hasDiskSamples = runs.Any(r => (r.DiskReadBytes ?? 0) > 0 || (r.DiskWriteBytes ?? 0) > 0);
        var hasCpuSamples = runs.Any(r => r.PeakCpuPercent.HasValue);

        var (verdict, breakdown, overallScore) = ScoreUtilization(
            criteria,
            hasData: sessions.Count > 0 || runs.Count > 0,
            users.Count,
            activeSeconds,
            utilPct,
            periodDays,
            runs,
            apps,
            hasCpuSamples,
            hasGpuSamples,
            hasDiskSamples,
            inboundShareOfSession,
            rdpIdleShareOfRdp);

        return new SocratizeBrief(
            Hostname: machine.Hostname,
            Region: machine.Region,
            Office: machine.Office,
            MachineGroup: machine.MachineGroup,
            FromUtc: fromUtc,
            ToUtc: toUtc,
            SessionCount: sessions.Count,
            ProcessRunCount: runs.Count,
            UtilisationPct: utilPct,
            OccupiedSeconds: occupiedSeconds,
            ActiveSeconds: activeSeconds,
            DisconnectedSeconds: disconnectedSeconds,
            LocalSessionCount: localSessions.Count,
            RdpSessionCount: inboundSessions.Count,
            LocalSharePct: localSharePct,
            RdpSharePct: rdpSharePct,
            LocalActiveSeconds: localActive,
            LocalDisconnectedSeconds: localDisconnected,
            RdpActiveSeconds: inboundActive,
            RdpDisconnectedSeconds: inboundDisconnected,
            RdpIdleShareOfRdpPct: rdpIdleShareOfRdp,
            OutboundRdpSeconds: (long)Math.Round(outboundSeconds),
            OutboundRdpSharePct: outboundSharePct,
            Users: users,
            Teams: teams,
            Apps: apps,
            TopAppSharePct: topAppShare,
            PoliciesInScope: scopedPolicies,
            HasGpuSamples: hasGpuSamples,
            HasDiskSamples: hasDiskSamples,
            OverallScore: overallScore,
            ScoreBreakdown: breakdown,
            Verdict: verdict
        );
    }

    public async Task<IReadOnlyList<string>> ListHostnamesAsync(CancellationToken ct = default)
    {
        var hosts = await db.Machines.AsNoTracking().Select(m => m.Hostname).ToListAsync(ct);
        return hosts.OrderBy(h => h, StringComparer.OrdinalIgnoreCase).ToList();
    }

    private static (SocratizeVerdict Verdict, IReadOnlyList<SocratizeScoreRow> Breakdown, double? Overall)
        ScoreUtilization(
            UtilizationCriteria c,
            bool hasData,
            int distinctUsers,
            long activeSeconds,
            double calendarOccupancyPct,
            double periodDays,
            List<ProcessRun> runs,
            List<SocratizeAppRow> apps,
            bool hasCpuSamples,
            bool hasGpuSamples,
            bool hasDiskSamples,
            double rdpSharePct,
            double rdpIdleShareOfRdp)
    {
        if (!hasData)
        {
            return (
                new SocratizeVerdict("insufficient-data", "Insufficient data",
                    "Little or no session/app telemetry in this period."),
                [],
                null);
        }

        var workingHours = Math.Clamp(c.WorkingHoursPerDay, 1, 24);
        var workingCapacitySeconds = Math.Max(1, periodDays * workingHours * 3600.0);
        var rows = new List<SocratizeScoreRow>();

        // 1) Distinct users
        var userScore = c.IdealMinUsers <= 0
            ? (distinctUsers > 0 ? 100.0 : 0.0)
            : Math.Clamp(distinctUsers / (double)c.IdealMinUsers * 100.0, 0, 100);
        rows.Add(new SocratizeScoreRow(
            Criterion: "# of users",
            Weight: c.WeightUsers,
            Score: userScore,
            Notes: $"{distinctUsers} distinct (ideal ≥ {c.IdealMinUsers})"));

        // 2) Daily util = active session time / working capacity
        var dailyUtilPct = activeSeconds / workingCapacitySeconds * 100.0;
        var dailyScore = c.IdealDailyUtilPct <= 0
            ? 100.0
            : Math.Clamp(dailyUtilPct / c.IdealDailyUtilPct * 100.0, 0, 100);
        rows.Add(new SocratizeScoreRow(
            Criterion: "% time in use / day",
            Weight: c.WeightDailyUtil,
            Score: dailyScore,
            Notes: $"Active {FormatDuration(activeSeconds)} ÷ ({periodDays:0.#}d × {workingHours:0.#}h) = {dailyUtilPct:0.#}% (ideal ≥ {c.IdealDailyUtilPct:0.#}%). Calendar occupancy {calendarOccupancyPct:0}% (sessions may overlap)."));

        // 3) Metric busy time (CPU / GPU / disk)
        double metricScore;
        string metricNotes;
        if (!hasCpuSamples && !hasGpuSamples && !hasDiskSamples)
        {
            metricScore = 50;
            metricNotes = "No CPU/GPU/disk samples yet — neutral 50.";
        }
        else
        {
            var busySeconds = runs.Sum(r =>
            {
                var busy =
                    (r.PeakCpuPercent is double cpu && cpu >= c.BusyCpuPercentThreshold)
                    || (hasGpuSamples && r.PeakGpuPercent is double gpu && gpu >= c.BusyGpuPercentThreshold)
                    || (hasDiskSamples && ((r.DiskReadBytes ?? 0) + (r.DiskWriteBytes ?? 0)) > 0);
                return busy ? RunDurationSeconds(r) : 0;
            });
            var busyPct = busySeconds / workingCapacitySeconds * 100.0;
            metricScore = c.IdealMetricBusyPct <= 0
                ? 100.0
                : Math.Clamp(busyPct / c.IdealMetricBusyPct * 100.0, 0, 100);
            var stub = (!hasGpuSamples || !hasDiskSamples)
                ? " GPU/disk may be stubbed."
                : "";
            metricNotes =
                $"Busy open time {FormatDuration(busySeconds)} = {busyPct:0.#}% of working capacity (ideal ≥ {c.IdealMetricBusyPct:0.#}%; CPU≥{c.BusyCpuPercentThreshold:0.#}%).{stub}";
        }

        rows.Add(new SocratizeScoreRow(
            Criterion: "CPU / GPU / disk busy",
            Weight: c.WeightMetricBusy,
            Score: metricScore,
            Notes: metricNotes));

        // 4) App business value ($/hour)
        double appScore;
        string appNotes;
        var priced = apps.Where(a => a.LicenseCostPerYear is > 0 && a.CostPerHour is > 0).ToList();
        if (priced.Count == 0)
        {
            appScore = 50;
            appNotes = "No license costs configured — neutral 50. Set costs on Utilization criteria.";
        }
        else
        {
            var weightSum = priced.Sum(a => a.TotalOpenSeconds);
            var avgCostPerHour = weightSum <= 0
                ? priced.Average(a => a.CostPerHour!.Value)
                : priced.Sum(a => a.CostPerHour!.Value * a.TotalOpenSeconds) / weightSum;
            appScore = c.IdealMaxCostPerHour <= 0
                ? 100.0
                : Math.Clamp(c.IdealMaxCostPerHour / Math.Max(avgCostPerHour, 0.01) * 100.0, 0, 100);
            appNotes =
                $"Usage-weighted avg ${avgCostPerHour:0.##}/h across {priced.Count} priced app(s) (ideal ≤ ${c.IdealMaxCostPerHour:0.##}/h). $/h = cost/year ÷ annualized open hours.";
        }

        rows.Add(new SocratizeScoreRow(
            Criterion: "App business value",
            Weight: c.WeightAppValue,
            Score: appScore,
            Notes: appNotes));

        var totalWeight = rows.Sum(r => Math.Max(0, r.Weight));
        if (totalWeight <= 0)
            totalWeight = 1;
        var overall = rows.Sum(r => Math.Max(0, r.Weight) * r.Score) / totalWeight;

        var normalized = rows
            .Select(r => r with { WeightPct = Math.Max(0, r.Weight) / totalWeight * 100.0 })
            .ToList();

        string code;
        string label;
        string detail;
        if (overall >= c.HighScoreThreshold)
        {
            code = "high";
            label = "High";
            detail = $"Weighted score {overall:0.#}/100 meets High (≥ {c.HighScoreThreshold:0}).";
        }
        else if (overall >= c.AdequateScoreThreshold)
        {
            code = "adequate";
            label = "Adequate";
            detail = $"Weighted score {overall:0.#}/100 — Adequate (≥ {c.AdequateScoreThreshold:0}).";
        }
        else if (overall >= c.MixedScoreThreshold)
        {
            code = "mixed";
            label = "Mixed / light use";
            detail = $"Weighted score {overall:0.#}/100 — Mixed (≥ {c.MixedScoreThreshold:0}).";
        }
        else
        {
            code = "underused";
            label = "Underused";
            detail = $"Weighted score {overall:0.#}/100 below Mixed threshold ({c.MixedScoreThreshold:0}).";
        }

        if (rdpSharePct >= 40 && rdpIdleShareOfRdp >= 35)
        {
            detail += $" Note: RDP disconnected is {rdpIdleShareOfRdp:0}% of RDP time — possible seat waste.";
        }

        return (new SocratizeVerdict(code, label, detail), normalized, overall);
    }

    private static PersonTeam? MatchTeam(List<PersonTeam> people, string normalizedUser)
    {
        var bare = normalizedUser.Contains('\\')
            ? normalizedUser[(normalizedUser.IndexOf('\\') + 1)..]
            : normalizedUser;

        return people.FirstOrDefault(p =>
        {
            var key = string.IsNullOrWhiteSpace(p.Domain) || p.Username.Contains('\\')
                ? p.Username.Trim()
                : $"{p.Domain.Trim()}\\{p.Username.Trim()}";
            if (string.Equals(key, normalizedUser, StringComparison.OrdinalIgnoreCase))
                return true;
            return string.Equals(p.Username.Trim(), bare, StringComparison.OrdinalIgnoreCase)
                   || string.Equals(p.Username.Trim(), normalizedUser, StringComparison.OrdinalIgnoreCase);
        });
    }

    private static string FormatThresholds(MetricPolicy p)
    {
        var parts = new List<string>();
        if (p.RamPercentThreshold is double rp) parts.Add($"RAM {rp:0.#}%");
        if (p.RamMbThreshold is double mb) parts.Add($"RAM {mb:0.#} MB");
        if (p.GpuPercentThreshold is double gp) parts.Add($"GPU {gp:0.#}%");
        if (p.DiskReadMBpsThreshold is double dr) parts.Add($"Disk read {dr:0.#} MB/s");
        if (p.DiskWriteMBpsThreshold is double dw) parts.Add($"Disk write {dw:0.#} MB/s");
        if (p.DiskCombinedMBpsThreshold is double dc) parts.Add($"Disk combined {dc:0.#} MB/s");
        return parts.Count == 0 ? "(no thresholds set)" : string.Join(", ", parts);
    }

    private static double RunDurationSeconds(ProcessRun r)
    {
        var end = r.EndedAtUtc ?? r.LastSeenAtUtc;
        return Math.Max(0, (end - r.StartedAtUtc).TotalSeconds);
    }

    /// <summary>
    /// Prefer per-kind buckets when the agent has reported them; otherwise attribute all
    /// Active/Disconnected seconds to the stored SessionType (legacy rows).
    /// </summary>
    private static (long LocalActive, long LocalDisc, long InboundActive, long InboundDisc) AccountSessionTime(UserSession s)
    {
        var bucketed = s.LocalActiveSeconds + s.LocalDisconnectedSeconds
                       + s.InboundRdpActiveSeconds + s.InboundRdpDisconnectedSeconds;
        if (bucketed > 0)
            return (s.LocalActiveSeconds, s.LocalDisconnectedSeconds, s.InboundRdpActiveSeconds, s.InboundRdpDisconnectedSeconds);

        if (s.SessionType == SessionType.Rdp || LooksLikeInboundRdpFingerprint(s))
            return (0, 0, s.ActiveSeconds, s.DisconnectedSeconds);

        return (s.ActiveSeconds, s.DisconnectedSeconds, 0, 0);
    }

    private static bool LooksLikeInboundRdpFingerprint(UserSession s)
    {
        if (!string.IsNullOrWhiteSpace(s.ClientName))
            return true;
        if (string.IsNullOrWhiteSpace(s.ClientAddress))
            return false;
        var addr = s.ClientAddress.Trim();
        return addr is not ("0.0.0.0" or "::" or "::1");
    }

    private static string NormalizeUser(string username, string? domain)
    {
        if (string.IsNullOrWhiteSpace(username))
            return "unknown";
        if (username.Contains('\\') || string.IsNullOrWhiteSpace(domain))
            return username.Trim();
        return $"{domain.Trim()}\\{username.Trim()}";
    }

    private static string FormatDuration(double seconds)
    {
        if (seconds < 60) return $"{seconds:0}s";
        if (seconds < 3600) return $"{seconds / 60.0:0.#}m";
        if (seconds < 86400) return $"{seconds / 3600.0:0.#}h";
        return $"{seconds / 86400.0:0.#}d";
    }
}

public sealed record SocratizeBrief(
    string Hostname,
    string? Region,
    string? Office,
    string? MachineGroup,
    DateTimeOffset FromUtc,
    DateTimeOffset ToUtc,
    int SessionCount,
    int ProcessRunCount,
    double UtilisationPct,
    double OccupiedSeconds,
    long ActiveSeconds,
    long DisconnectedSeconds,
    int LocalSessionCount,
    int RdpSessionCount,
    double LocalSharePct,
    double RdpSharePct,
    long LocalActiveSeconds,
    long LocalDisconnectedSeconds,
    long RdpActiveSeconds,
    long RdpDisconnectedSeconds,
    double RdpIdleShareOfRdpPct,
    long OutboundRdpSeconds,
    double OutboundRdpSharePct,
    IReadOnlyList<SocratizeUserRow> Users,
    IReadOnlyList<SocratizeTeamRow> Teams,
    IReadOnlyList<SocratizeAppRow> Apps,
    double TopAppSharePct,
    IReadOnlyList<SocratizePolicyRow> PoliciesInScope,
    bool HasGpuSamples,
    bool HasDiskSamples,
    double? OverallScore,
    IReadOnlyList<SocratizeScoreRow> ScoreBreakdown,
    SocratizeVerdict Verdict);

public sealed record SocratizeUserRow(
    string Username,
    string? TeamName,
    string? TeamCode,
    int LogonCount,
    long ActiveSeconds,
    long DisconnectedSeconds,
    int RdpLogons,
    int LocalLogons);

public sealed record SocratizeTeamRow(string TeamName, int UserCount, long ActiveSeconds);

public sealed record SocratizeAppRow(
    string ProcessName,
    int RunCount,
    double TotalOpenSeconds,
    int UniqueUsers,
    double? PeakCpuPercent,
    double? LicenseCostPerYear = null,
    double? CostPerHour = null);

public sealed record SocratizePolicyRow(
    string Name,
    string MetricType,
    string Scope,
    string? ScopeValue,
    string ThresholdSummary);

public sealed record SocratizeVerdict(string Code, string Label, string Detail);

public sealed record SocratizeScoreRow(
    string Criterion,
    double Weight,
    double Score,
    string Notes,
    double WeightPct = 0);
