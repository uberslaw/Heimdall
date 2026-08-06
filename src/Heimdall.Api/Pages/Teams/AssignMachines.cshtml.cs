using Heimdall.Api.Data;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;

namespace Heimdall.Api.Pages.Teams;

public class AssignMachinesModel(HeimdallDbContext db) : PageModel
{
    public string TeamName { get; private set; } = "";
    public IReadOnlyList<MachinePick> Machines { get; private set; } = [];
    public string? FormError { get; private set; }

    [BindProperty]
    public int TeamId { get; set; }

    [BindProperty]
    public List<int> SelectedMachineIds { get; set; } = [];

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
        await LoadMachinesAsync();
        SelectedMachineIds = Machines.Where(m => m.TeamId == teamId).Select(m => m.Id).ToList();
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
        var selected = SelectedMachineIds.Distinct().ToHashSet();
        // Only touch machines on this team or newly selected — never delete Machine rows.
        var touched = await db.Machines
            .Where(m => m.TeamId == TeamId || selected.Contains(m.Id))
            .ToListAsync();
        foreach (var m in touched)
        {
            if (selected.Contains(m.Id))
                m.TeamId = TeamId;
            else if (m.TeamId == TeamId)
                m.TeamId = null;
        }

        await db.SaveChangesAsync();
        return RedirectToPage("/Teams/Detail", new { id = TeamId, tab = "membership" });
    }

    private async Task LoadMachinesAsync()
    {
        Machines = await db.Machines.AsNoTracking()
            .OrderBy(m => m.Hostname)
            .Select(m => new MachinePick(m.Id, m.Hostname, m.FriendlyName, m.TeamId))
            .ToListAsync();
    }

    public sealed record MachinePick(int Id, string Hostname, string? FriendlyName, int? TeamId);
}
