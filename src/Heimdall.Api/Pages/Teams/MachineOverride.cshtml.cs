using Heimdall.Api.Data;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;

namespace Heimdall.Api.Pages.Teams;

public class MachineOverrideModel(HeimdallDbContext db) : PageModel
{
    public string TeamName { get; private set; } = "";
    public IReadOnlyList<MachinePick> TeamMachines { get; private set; } = [];
    public IReadOnlyList<AppListPick> AppLists { get; private set; } = [];
    public IReadOnlyList<OverrideRow> ExistingOverrides { get; private set; } = [];
    public IReadOnlyList<TeamLinkRow> TeamLinks { get; private set; } = [];
    public string? FormError { get; private set; }

    [BindProperty]
    public int TeamId { get; set; }

    [BindProperty]
    public int MachineId { get; set; }

    [BindProperty]
    public int AppListId { get; set; }

    [BindProperty]
    public bool IsExcluded { get; set; }

    public async Task<IActionResult> OnGetAsync(int teamId, int? machineId)
    {
        if (!await LoadContextAsync(teamId))
            return RedirectToPage("/Teams");

        if (machineId is int mid && TeamMachines.Any(m => m.Id == mid))
            MachineId = mid;
        else if (TeamMachines.Count > 0)
            MachineId = TeamMachines[0].Id;

        await LoadOverridesAsync();
        return Page();
    }

    public async Task<IActionResult> OnPostSaveAsync()
    {
        if (!await LoadContextAsync(TeamId))
            return RedirectToPage("/Teams");

        if (!TeamMachines.Any(m => m.Id == MachineId))
        {
            FormError = "Select a machine on this team.";
            await LoadOverridesAsync();
            return Page();
        }

        if (!await db.AppLists.AnyAsync(a => a.Id == AppListId))
        {
            FormError = "App list not found.";
            await LoadOverridesAsync();
            return Page();
        }

        var existing = await db.MachineAppListOverrides
            .FirstOrDefaultAsync(o => o.MachineId == MachineId && o.AppListId == AppListId);
        if (existing is null)
        {
            db.MachineAppListOverrides.Add(new MachineAppListOverride
            {
                MachineId = MachineId,
                AppListId = AppListId,
                IsExcluded = IsExcluded
            });
        }
        else
        {
            existing.IsExcluded = IsExcluded;
        }

        await db.SaveChangesAsync();
        return RedirectToPage(new { teamId = TeamId, machineId = MachineId });
    }

    public async Task<IActionResult> OnPostRemoveAsync(int overrideId)
    {
        if (!await LoadContextAsync(TeamId))
            return RedirectToPage("/Teams");

        var o = await db.MachineAppListOverrides.FirstOrDefaultAsync(x => x.Id == overrideId);
        if (o is not null && TeamMachines.Any(m => m.Id == o.MachineId))
        {
            MachineId = o.MachineId;
            db.MachineAppListOverrides.Remove(o);
            await db.SaveChangesAsync();
        }

        return RedirectToPage(new { teamId = TeamId, machineId = MachineId > 0 ? MachineId : (int?)null });
    }

    private async Task<bool> LoadContextAsync(int teamId)
    {
        var team = await db.Teams.AsNoTracking().FirstOrDefaultAsync(t => t.Id == teamId);
        if (team is null)
        {
            TempData["Error"] = "Team not found.";
            return false;
        }

        TeamId = team.Id;
        TeamName = team.Name;
        TeamMachines = await db.Machines.AsNoTracking()
            .Where(m => m.TeamId == teamId)
            .OrderBy(m => m.Hostname)
            .Select(m => new MachinePick(m.Id, m.Hostname, m.FriendlyName))
            .ToListAsync();

        TeamLinks = await db.TeamAppListLinks.AsNoTracking()
            .Include(l => l.AppList)
            .Where(l => l.TeamId == teamId)
            .OrderBy(l => l.AppList.Name)
            .Select(l => new TeamLinkRow(l.AppListId, l.AppList.Name, l.IsExcluded))
            .ToListAsync();

        AppLists = await db.AppLists.AsNoTracking()
            .Where(a => !a.IsAutoDiscovered)
            .OrderBy(a => a.Name)
            .Select(a => new AppListPick(a.Id, a.Name))
            .ToListAsync();

        return true;
    }

    private async Task LoadOverridesAsync()
    {
        if (MachineId <= 0)
        {
            ExistingOverrides = [];
            return;
        }

        ExistingOverrides = await db.MachineAppListOverrides.AsNoTracking()
            .Include(o => o.AppList)
            .Where(o => o.MachineId == MachineId)
            .OrderBy(o => o.AppList.Name)
            .Select(o => new OverrideRow(o.Id, o.AppListId, o.AppList.Name, o.IsExcluded))
            .ToListAsync();
    }

    public sealed record MachinePick(int Id, string Hostname, string? FriendlyName);
    public sealed record AppListPick(int Id, string Name);
    public sealed record OverrideRow(int Id, int AppListId, string AppListName, bool IsExcluded);
    public sealed record TeamLinkRow(int AppListId, string AppListName, bool IsExcluded);
}
