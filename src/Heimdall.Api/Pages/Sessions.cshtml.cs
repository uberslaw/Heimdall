using Heimdall.Api.Data;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;

namespace Heimdall.Api.Pages;

public class SessionsModel(HeimdallDbContext db) : PageModel
{
    public IReadOnlyList<Row> Rows { get; private set; } = [];

    public async Task OnGetAsync()
    {
        // SQLite EF cannot OrderBy DateTimeOffset; load then order in memory for POC.
        var sessions = await db.Sessions.AsNoTracking()
            .Include(s => s.Machine)
            .ToListAsync();

        var people = await db.PersonTeams.AsNoTracking()
            .Include(p => p.Team)
            .ToListAsync();

        string? ResolveTeam(string username, string? domain)
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

        Rows = sessions
            .OrderByDescending(s => s.LastObservedUtc)
            .Take(200)
            .Select(s => new Row(
                s.Machine.Hostname,
                s.Username,
                s.Domain,
                ResolveTeam(s.Username, s.Domain),
                s.SessionType,
                s.State,
                s.StartedAtUtc,
                s.EndedAtUtc,
                s.ActiveSeconds,
                s.DisconnectedSeconds,
                s.ClientName,
                s.ClientAddress
            ))
            .ToList();
    }

    public record Row(
        string Hostname,
        string Username,
        string? Domain,
        string? TeamName,
        Heimdall.Shared.Contracts.SessionType SessionType,
        Heimdall.Shared.Contracts.SessionState State,
        DateTimeOffset StartedAtUtc,
        DateTimeOffset? EndedAtUtc,
        long ActiveSeconds,
        long DisconnectedSeconds,
        string? ClientName,
        string? ClientAddress);
}
