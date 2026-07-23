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

        var windowSeconds = Math.Max(1, (toUtc - fromUtc).TotalSeconds);
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
        var rdpSessions = sessions.Where(s => s.SessionType == SessionType.Rdp).ToList();
        var localSessions = sessions.Where(s => s.SessionType == SessionType.Local).ToList();
        var rdpActive = rdpSessions.Sum(s => s.ActiveSeconds);
        var rdpDisconnected = rdpSessions.Sum(s => s.DisconnectedSeconds);
        var localActive = localSessions.Sum(s => s.ActiveSeconds);
        var sessionAccounted = activeSeconds + disconnectedSeconds;
        var rdpSharePct = sessionAccounted <= 0
            ? 0
            : (rdpActive + rdpDisconnected) * 100.0 / sessionAccounted;
        var localSharePct = sessionAccounted <= 0
            ? 0
            : (localActive + localSessions.Sum(s => s.DisconnectedSeconds)) * 100.0 / sessionAccounted;
        var rdpIdleShareOfRdp = (rdpActive + rdpDisconnected) <= 0
            ? 0
            : rdpDisconnected * 100.0 / (rdpActive + rdpDisconnected);

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
                    RdpLogons: g.Count(s => s.SessionType == SessionType.Rdp),
                    LocalLogons: g.Count(s => s.SessionType == SessionType.Local)
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
                return new SocratizeAppRow(
                    ProcessName: g.Key,
                    RunCount: g.Count(),
                    TotalOpenSeconds: seconds,
                    UniqueUsers: g.Select(x => x.Username).Distinct(StringComparer.OrdinalIgnoreCase).Count(),
                    PeakCpuPercent: cpuPeaks.Count == 0 ? null : cpuPeaks.Max()
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

        var verdict = DeriveVerdict(
            hasData: sessions.Count > 0 || runs.Count > 0,
            utilPct,
            rdpIdleShareOfRdp,
            rdpSharePct,
            topAppShare,
            apps.Count);

        var findings = BuildFindings(
            users, teams, utilPct, localSharePct, rdpSharePct, rdpIdleShareOfRdp,
            rdpDisconnected, apps, topAppShare, scopedPolicies, hasGpuSamples, hasDiskSamples,
            sessions.Count, runs.Count, verdict);

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
            RdpSessionCount: rdpSessions.Count,
            LocalSharePct: localSharePct,
            RdpSharePct: rdpSharePct,
            RdpActiveSeconds: rdpActive,
            RdpDisconnectedSeconds: rdpDisconnected,
            RdpIdleShareOfRdpPct: rdpIdleShareOfRdp,
            Users: users,
            Teams: teams,
            Apps: apps,
            TopAppSharePct: topAppShare,
            PoliciesInScope: scopedPolicies,
            HasGpuSamples: hasGpuSamples,
            HasDiskSamples: hasDiskSamples,
            Verdict: verdict,
            Findings: findings
        );
    }

    public async Task<IReadOnlyList<string>> ListHostnamesAsync(CancellationToken ct = default)
    {
        var hosts = await db.Machines.AsNoTracking().Select(m => m.Hostname).ToListAsync(ct);
        return hosts.OrderBy(h => h, StringComparer.OrdinalIgnoreCase).ToList();
    }

    private static SocratizeVerdict DeriveVerdict(
        bool hasData,
        double utilPct,
        double rdpIdleShareOfRdp,
        double rdpSharePct,
        double topAppShare,
        int appCount)
    {
        if (!hasData)
            return new SocratizeVerdict("insufficient-data", "Insufficient data", "Little or no session/app telemetry in this period.");

        if (rdpSharePct >= 40 && rdpIdleShareOfRdp >= 35)
            return new SocratizeVerdict("rdp-idle-heavy", "RDP-idle-heavy", "A large share of RDP time is disconnected — possible seat waste.");

        if (utilPct < 15)
            return new SocratizeVerdict("underused", "Underused", "Low occupancy vs calendar window — weak cost justification unless bursty.");

        if (appCount > 0 && topAppShare >= 55)
            return new SocratizeVerdict("app-concentrated", "App-concentrated", "One title dominates open time — justify the box for that workload.");

        if (utilPct >= 40)
            return new SocratizeVerdict("healthy", "Healthy", "Material occupancy with a plausible mix of use.");

        return new SocratizeVerdict("mixed", "Mixed / light use", "Some activity, but not a clear high-utilisation story yet.");
    }

    private static List<SocratizeFinding> BuildFindings(
        List<SocratizeUserRow> users,
        List<SocratizeTeamRow> teams,
        double utilPct,
        double localSharePct,
        double rdpSharePct,
        double rdpIdleShareOfRdp,
        long rdpDisconnected,
        List<SocratizeAppRow> apps,
        double topAppShare,
        List<SocratizePolicyRow> policies,
        bool hasGpu,
        bool hasDisk,
        int sessionCount,
        int runCount,
        SocratizeVerdict verdict)
    {
        var list = new List<SocratizeFinding>();

        if (sessionCount == 0 && runCount == 0)
        {
            list.Add(new SocratizeFinding(
                "Who uses this machine?",
                "No sessions or process runs in this period. Either the agent is quiet, tracking is narrow, or the box was unused."));
            list.Add(new SocratizeFinding(
                "POC verdict",
                $"{verdict.Label}: {verdict.Detail} (heuristic — not a formal chargeback)."));
            return list;
        }

        if (users.Count == 0)
        {
            list.Add(new SocratizeFinding(
                "Who uses this machine?",
                "No user sessions recorded. Process runs may still show app activity without a matched logon."));
        }
        else
        {
            var top = string.Join(", ", users.Take(5).Select(u =>
            {
                var team = string.IsNullOrWhiteSpace(u.TeamName) ? "" : $" ({u.TeamName})";
                return $"{u.Username}{team} — {FormatDuration(u.ActiveSeconds)} active / {u.LogonCount} logons";
            }));
            var teamLine = teams.Count == 0
                ? " No PersonTeam mappings matched these usernames yet."
                : $" Teams seen: {string.Join(", ", teams.Select(t => $"{t.TeamName} ({t.UserCount} users)"))}.";
            list.Add(new SocratizeFinding(
                "Who uses this machine?",
                $"{users.Count} distinct user(s). Top: {top}.{teamLine}"));
        }

        list.Add(new SocratizeFinding(
            "How do they connect?",
            sessionCount == 0
                ? "No session type mix available."
                : $"Local ≈ {localSharePct:0}% of accounted session time · RDP ≈ {rdpSharePct:0}% (by active+disconnected seconds)."));

        list.Add(new SocratizeFinding(
            "How much of the time is it occupied?",
            $"≈ {utilPct:0}% of the calendar window had at least one open session (overlap not de-duplicated — POC occupancy)."));

        list.Add(new SocratizeFinding(
            "Active vs disconnected RDP waste?",
            rdpDisconnected <= 0 && rdpSharePct < 1
                ? "Little or no RDP disconnected time in this window."
                : $"RDP disconnected ≈ {FormatDuration(rdpDisconnected)} ({rdpIdleShareOfRdp:0}% of RDP accounted time). High disconnected share can mean paid capacity sitting idle while sessions linger."));

        if (apps.Count == 0)
        {
            list.Add(new SocratizeFinding(
                "What applications dominate time?",
                "No allowlisted process runs in this period. Check Track Software / Config include lists."));
        }
        else
        {
            var topApps = string.Join(", ", apps.Take(5).Select(a =>
                $"{a.ProcessName} ({FormatDuration(a.TotalOpenSeconds)}, {a.RunCount} runs)"));
            list.Add(new SocratizeFinding(
                "What applications dominate time?",
                $"Top titles: {topApps}. Leading app share ≈ {topAppShare:0}% of tracked open time."));
        }

        if (policies.Count == 0)
        {
            list.Add(new SocratizeFinding(
                "Any high-threshold metric policies in scope?",
                "No enabled MetricPolicy matches this host’s scope."));
        }
        else
        {
            var lines = string.Join("; ", policies.Select(p =>
                $"{p.Name} [{p.MetricType}] @ {p.Scope}{(string.IsNullOrWhiteSpace(p.ScopeValue) ? "" : $"={p.ScopeValue}")}: {p.ThresholdSummary}"));
            var sampleNote = (!hasGpu || !hasDisk)
                ? " Note: agent GPU/disk sampling may still be stubbed — thresholds can exist without samples."
                : "";
            list.Add(new SocratizeFinding(
                "Any high-threshold metric policies in scope?",
                lines + sampleNote));
        }

        list.Add(new SocratizeFinding(
            "POC verdict",
            $"{verdict.Label}: {verdict.Detail}"));

        return list;
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
    long RdpActiveSeconds,
    long RdpDisconnectedSeconds,
    double RdpIdleShareOfRdpPct,
    IReadOnlyList<SocratizeUserRow> Users,
    IReadOnlyList<SocratizeTeamRow> Teams,
    IReadOnlyList<SocratizeAppRow> Apps,
    double TopAppSharePct,
    IReadOnlyList<SocratizePolicyRow> PoliciesInScope,
    bool HasGpuSamples,
    bool HasDiskSamples,
    SocratizeVerdict Verdict,
    IReadOnlyList<SocratizeFinding> Findings);

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
    double? PeakCpuPercent);

public sealed record SocratizePolicyRow(
    string Name,
    string MetricType,
    string Scope,
    string? ScopeValue,
    string ThresholdSummary);

public sealed record SocratizeVerdict(string Code, string Label, string Detail);

public sealed record SocratizeFinding(string Question, string Answer);
