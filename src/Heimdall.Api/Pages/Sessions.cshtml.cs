using Heimdall.Api.Data;
using Heimdall.Api.Pages.Teams;
using Heimdall.Api.Services;
using Heimdall.Shared;
using Heimdall.Shared.Contracts;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;

namespace Heimdall.Api.Pages;

public class SessionsModel(StatsQueryService stats, HeimdallDbContext db) : PageModel
{
    public SessionsPageSnapshot? Snapshot { get; private set; }
    public IReadOnlyList<SessionRow> SessionRows { get; private set; } = [];
    public IReadOnlyList<UserSessionSummary> UserSummaries { get; private set; } = [];
    public string RangeLabel { get; private set; } = "7 day";
    public int RangeDays { get; private set; } = 7;

    [BindProperty(SupportsGet = true)]
    public string Range { get; set; } = "7d";

    /// <summary>When set (from machine Stats Duration), uses calendar/rolling windows instead of day Range.</summary>
    [BindProperty(SupportsGet = true)]
    public string? StatsDuration { get; set; }

    [BindProperty(SupportsGet = true)]
    public List<string> Hosts { get; set; } = [];

    [BindProperty(SupportsGet = true)]
    public bool HideSystem { get; set; } = true;

    [BindProperty(SupportsGet = true)]
    public bool OnlyDisconnectedApps { get; set; }

    public bool UsesStatsDuration { get; private set; }

    public async Task<IActionResult> OnGetAsync(CancellationToken ct)
    {
        if (!OpsPartial.IsPartial(Request))
            return OpsPartial.RedirectToOpsTab(Request, "sessions");

        DateTimeOffset fromUtc;
        DateTimeOffset toUtc;
        var rawStats = StatsDuration?.Trim();
        UsesStatsDuration = !string.IsNullOrWhiteSpace(rawStats)
            && MachineModel.StatsDurationOptions.Any(o =>
                string.Equals(o.Key, rawStats, StringComparison.OrdinalIgnoreCase));

        if (UsesStatsDuration)
        {
            StatsDuration = MachineModel.NormalizeStatsDuration(rawStats);
            RangeLabel = MachineModel.StatsDurationLabelFor(StatsDuration);
            Range = MachineModel.MapStatsDurationToSessionsRange(StatsDuration);
            (_, _, RangeDays) = IndexModel.ResolveRange(Range);
            (fromUtc, toUtc) = MachineModel.ResolveStatsDurationWindow(StatsDuration, DateTimeOffset.UtcNow);
        }
        else
        {
            StatsDuration = null;
            var (key, label, days) = IndexModel.ResolveRange(Range);
            Range = key;
            RangeLabel = label;
            RangeDays = days;
            (fromUtc, toUtc) = IndexModel.ResolveRangeWindow(Range);
        }

        var selectedHosts = Hosts.Count > 0 ? Hosts : null;

        Snapshot = await stats.QuerySessionsPageAsync(
            fromUtc,
            toUtc,
            selectedHosts,
            HideSystem,
            OnlyDisconnectedApps,
            ct);

        if (Snapshot.ShowSessionDetails)
        {
            var people = await db.PersonTeams.AsNoTracking()
                .Include(p => p.Team)
                .ToListAsync(ct);

            SessionRows = Snapshot.Sessions
                .Select(s => new SessionRow(
                    s.Hostname,
                    s.Username,
                    s.Domain,
                    ResolveTeam(people, s.Username, s.Domain),
                    s.SessionType,
                    s.State,
                    s.StartedAtUtc,
                    s.EndedAtUtc,
                    s.ActiveSeconds,
                    s.DisconnectedSeconds,
                    s.ClientName,
                    s.ClientAddress,
                    s.HadAppActivityWhileDisconnected,
                    s.AppProcesses))
                .ToList();

            UserSummaries = SessionRows
                .GroupBy(s => UsernameDisplay.Format(s.Username, s.Domain), StringComparer.OrdinalIgnoreCase)
                .Select(g =>
                {
                    var first = g.First();
                    return new UserSessionSummary(
                        Username: first.Username,
                        Domain: first.Domain,
                        DisplayName: UsernameDisplay.Format(first.Username, first.Domain),
                        TeamName: g.Select(x => x.TeamName).FirstOrDefault(t => !string.IsNullOrWhiteSpace(t)),
                        SessionCount: g.Count(),
                        LocalCount: g.Count(x => x.SessionType == SessionType.Local),
                        RdpCount: g.Count(x => x.SessionType == SessionType.Rdp),
                        OpenCount: g.Count(x => x.State != SessionState.Ended),
                        ActiveSeconds: g.Sum(x => x.ActiveSeconds),
                        DisconnectedSeconds: g.Sum(x => x.DisconnectedSeconds));
                })
                .OrderByDescending(u => u.SessionCount)
                .ThenBy(u => u.DisplayName, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }

        return Page();
    }

    private static string? ResolveTeam(List<PersonTeam> people, string username, string? domain)
    {
        foreach (var p in people)
        {
            var personKeys = TeamPageHelpers.MatchKeys(p.Username, p.Domain).ToHashSet(StringComparer.OrdinalIgnoreCase);
            var sessionKeys = TeamPageHelpers.MatchKeys(username, domain);
            if (sessionKeys.Any(personKeys.Contains))
                return p.Team.Name;
        }

        return null;
    }

    public record UserSessionSummary(
        string Username,
        string? Domain,
        string DisplayName,
        string? TeamName,
        int SessionCount,
        int LocalCount,
        int RdpCount,
        int OpenCount,
        long ActiveSeconds,
        long DisconnectedSeconds);

    public record SessionRow(
        string Hostname,
        string Username,
        string? Domain,
        string? TeamName,
        SessionType SessionType,
        SessionState State,
        DateTimeOffset StartedAtUtc,
        DateTimeOffset? EndedAtUtc,
        long ActiveSeconds,
        long DisconnectedSeconds,
        string? ClientName,
        string? ClientAddress,
        bool HadAppActivityWhileDisconnected,
        IReadOnlyList<string> AppProcesses);
}
