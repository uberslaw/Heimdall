using Heimdall.Api.Data;
using Heimdall.Api.Services;
using Heimdall.Shared.Contracts;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;

namespace Heimdall.Api.Pages;

public class SessionsModel(StatsQueryService stats, HeimdallDbContext db) : PageModel
{
    public SessionsPageSnapshot? Snapshot { get; private set; }
    public IReadOnlyList<SessionRow> SessionRows { get; private set; } = [];
    public string RangeLabel { get; private set; } = "7 day";
    public int RangeDays { get; private set; } = 7;

    [BindProperty(SupportsGet = true)]
    public string Range { get; set; } = "7d";

    [BindProperty(SupportsGet = true)]
    public List<string> Hosts { get; set; } = [];

    [BindProperty(SupportsGet = true)]
    public bool HideSystem { get; set; } = true;

    [BindProperty(SupportsGet = true)]
    public bool OnlyDisconnectedApps { get; set; }

    public async Task OnGetAsync(CancellationToken ct)
    {
        var (key, label, days) = IndexModel.ResolveRange(Range);
        Range = key;
        RangeLabel = label;
        RangeDays = days;

        var fromUtc = DateTimeOffset.UtcNow.AddDays(-days);
        var toUtc = DateTimeOffset.UtcNow;
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
        }
    }

    private static string? ResolveTeam(List<PersonTeam> people, string username, string? domain)
    {
        foreach (var p in people)
        {
            var personKeys = TeamsModel.MatchKeys(p.Username, p.Domain).ToHashSet(StringComparer.OrdinalIgnoreCase);
            var sessionKeys = TeamsModel.MatchKeys(username, domain);
            if (sessionKeys.Any(personKeys.Contains))
                return p.Team.Name;
        }

        return null;
    }

    public static string FormatDuration(long seconds) => StatsModel.FormatDuration(seconds);

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
