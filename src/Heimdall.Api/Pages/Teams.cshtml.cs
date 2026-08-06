using Heimdall.Api.Data;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;

namespace Heimdall.Api.Pages;

/// <summary>Teams list hub — composition counts; add/edit via purposeful forms.</summary>
public class TeamsModel(HeimdallDbContext db) : PageModel
{
    public IReadOnlyList<TeamListRow> Rows { get; private set; } = [];
    public bool IsEmpty => Rows.Count == 0;

    public async Task OnGetAsync()
    {
        var teams = await db.Teams.AsNoTracking().OrderBy(t => t.Name).ToListAsync();
        var peopleCounts = await db.PersonTeams.AsNoTracking()
            .GroupBy(p => p.TeamId)
            .Select(g => new { TeamId = g.Key, N = g.Count() })
            .ToDictionaryAsync(x => x.TeamId, x => x.N);
        var machineCounts = await db.Machines.AsNoTracking()
            .Where(m => m.TeamId != null)
            .GroupBy(m => m.TeamId!.Value)
            .Select(g => new { TeamId = g.Key, N = g.Count() })
            .ToDictionaryAsync(x => x.TeamId, x => x.N);
        var linkRows = await db.TeamAppListLinks.AsNoTracking()
            .GroupBy(l => new { l.TeamId, l.IsExcluded })
            .Select(g => new { g.Key.TeamId, g.Key.IsExcluded, N = g.Count() })
            .ToListAsync();
        var links = linkRows
            .Select(x => (x.TeamId, x.IsExcluded, x.N))
            .ToList();

        var byParent = teams.ToLookup(t => t.ParentTeamId);
        Rows = Flatten(byParent, null, 0, peopleCounts, machineCounts, links);
    }

    public async Task<IActionResult> OnPostDeleteTeamAsync(int teamId)
    {
        var team = await db.Teams
            .Include(t => t.Children)
            .Include(t => t.Members)
            .FirstOrDefaultAsync(t => t.Id == teamId);
        if (team is null)
        {
            TempData["Error"] = "Team not found.";
            return RedirectToPage();
        }

        if (team.Children.Count > 0)
        {
            TempData["Error"] = $"Cannot delete “{team.Name}” while it has child teams. Reassign or delete children first.";
            return RedirectToPage();
        }

        var machines = await db.Machines.Where(m => m.TeamId == team.Id).ToListAsync();
        foreach (var m in machines)
            m.TeamId = null;

        var teamLinks = await db.TeamAppListLinks.Where(l => l.TeamId == team.Id).ToListAsync();
        db.TeamAppListLinks.RemoveRange(teamLinks);

        var lists = await db.AppLists.Where(a => a.TeamId == team.Id).ToListAsync();
        foreach (var a in lists)
        {
            a.TeamId = null;
            a.IsTeamExcluded = false;
        }

        db.PersonTeams.RemoveRange(team.Members);
        db.Teams.Remove(team);
        await db.SaveChangesAsync();
        return RedirectToPage();
    }

    private static List<TeamListRow> Flatten(
        ILookup<int?, Team> byParent,
        int? parentId,
        int depth,
        IReadOnlyDictionary<int, int> people,
        IReadOnlyDictionary<int, int> machines,
        IReadOnlyList<(int TeamId, bool IsExcluded, int N)> links)
    {
        var rows = new List<TeamListRow>();
        foreach (var t in byParent[parentId].OrderBy(x => x.Name, StringComparer.OrdinalIgnoreCase))
        {
            var track = links.Where(l => l.TeamId == t.Id && !l.IsExcluded).Sum(l => l.N);
            var ignore = links.Where(l => l.TeamId == t.Id && l.IsExcluded).Sum(l => l.N);
            people.TryGetValue(t.Id, out var pc);
            machines.TryGetValue(t.Id, out var mc);
            rows.Add(new TeamListRow(t.Id, t.Name, t.Code, t.IsPublicFacing, depth, pc, mc, track, ignore));
            rows.AddRange(Flatten(byParent, t.Id, depth + 1, people, machines, links));
        }

        return rows;
    }

    public sealed record TeamListRow(
        int Id,
        string Name,
        string? Code,
        bool IsPublicFacing,
        int Depth,
        int PeopleCount,
        int MachineCount,
        int TrackingListCount,
        int IgnoredListCount);
}
