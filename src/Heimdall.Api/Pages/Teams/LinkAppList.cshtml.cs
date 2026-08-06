using Heimdall.Api.Data;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;

namespace Heimdall.Api.Pages.Teams;

public class LinkAppListModel(HeimdallDbContext db) : PageModel
{
    public string TeamName { get; private set; } = "";
    public IReadOnlyList<AppListPick> LinkableAppLists { get; private set; } = [];
    public HashSet<int> AlreadyLinkedIds { get; private set; } = [];
    public string? FormError { get; private set; }

    [BindProperty]
    public int TeamId { get; set; }

    [BindProperty]
    public int LinkAppListId { get; set; }

    [BindProperty]
    public bool LinkAsIgnored { get; set; }

    public async Task<IActionResult> OnGetAsync(int teamId)
    {
        var team = await db.Teams.AsNoTracking().FirstOrDefaultAsync(t => t.Id == teamId);
        if (team is null)
        {
            TempData["Error"] = "Team not found.";
            return RedirectToPage("/Teams");
        }

        TeamId = team.Id;
        TeamName = team.Name;
        await LoadListsAsync();
        return Page();
    }

    public async Task<IActionResult> OnPostAsync()
    {
        var team = await db.Teams.AsNoTracking().FirstOrDefaultAsync(t => t.Id == TeamId);
        if (team is null)
        {
            TempData["Error"] = "Team not found.";
            return RedirectToPage("/Teams");
        }

        TeamName = team.Name;
        var list = await db.AppLists.FindAsync(LinkAppListId);
        if (list is null)
        {
            FormError = "App list not found.";
            await LoadListsAsync();
            return Page();
        }

        var link = await db.TeamAppListLinks
            .FirstOrDefaultAsync(l => l.TeamId == TeamId && l.AppListId == LinkAppListId);
        if (link is null)
        {
            db.TeamAppListLinks.Add(new TeamAppListLink
            {
                TeamId = TeamId,
                AppListId = LinkAppListId,
                IsExcluded = LinkAsIgnored
            });
        }
        else
        {
            link.IsExcluded = LinkAsIgnored;
        }

        await db.SaveChangesAsync();
        return RedirectToPage("/Teams/Detail", new { id = TeamId, tab = "apps" });
    }

    private async Task LoadListsAsync()
    {
        AlreadyLinkedIds = (await db.TeamAppListLinks.AsNoTracking()
            .Where(l => l.TeamId == TeamId)
            .Select(l => l.AppListId)
            .ToListAsync()).ToHashSet();

        LinkableAppLists = await db.AppLists.AsNoTracking()
            .Where(a => !a.IsAutoDiscovered)
            .OrderBy(a => a.Name)
            .Select(a => new AppListPick(a.Id, a.Name, a.Entries.Count))
            .ToListAsync();
    }

    public sealed record AppListPick(int Id, string Name, int EntryCount);
}
