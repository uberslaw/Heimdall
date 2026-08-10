using Heimdall.Api.Data;
using Heimdall.Api.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;

namespace Heimdall.Api.Pages.Teams;

public class DetailModel(
    HeimdallDbContext db,
    EntraGraphService graph,
    EntraTeamMembershipSyncService entraSync,
    DirectoryAuthSettingsService authSettings,
    SpecReviewService specReview) : PageModel
{
    public Team Team { get; private set; } = null!;
    public string Tab { get; private set; } = "membership";
    public IReadOnlyList<PersonRow> People { get; private set; } = [];
    public IReadOnlyList<MachineRow> Machines { get; private set; } = [];
    public IReadOnlyList<AppListRow> TrackingLists { get; private set; } = [];
    public IReadOnlyList<AppListRow> IgnoredLists { get; private set; } = [];
    public int OverrideCount { get; private set; }
    public bool EntraConfigured => graph.IsConfigured;
    public bool HasEntraGroup => !string.IsNullOrWhiteSpace(Team.EntraGroupId);
    public bool EntraGraphMembershipEnabled { get; private set; }
    public bool ManualCsvMembershipEnabled { get; private set; }
    public bool CanSyncEntra => EntraConfigured && HasEntraGroup && EntraGraphMembershipEnabled;

    [BindProperty]
    public int PersonId { get; set; }

    [BindProperty]
    public int LinkTeamId { get; set; }

    [BindProperty]
    public int AppListId { get; set; }

    public async Task<IActionResult> OnGetAsync(int id, string? tab)
    {
        Tab = NormalizeTab(tab);
        if (!await LoadAsync(id))
        {
            TempData["Error"] = "Team not found.";
            return RedirectToPage("/Teams");
        }

        return Page();
    }

    public async Task<IActionResult> OnPostDeletePersonAsync(int id)
    {
        var person = await db.PersonTeams.FindAsync(PersonId);
        if (person is null || person.TeamId != id)
        {
            TempData["Error"] = "Person assignment not found.";
            return RedirectToPage(new { id, tab = "membership" });
        }

        db.PersonTeams.Remove(person);
        await db.SaveChangesAsync();
        return RedirectToPage(new { id, tab = "membership" });
    }

    public async Task<IActionResult> OnPostUnassignMachineAsync(int id, int machineId)
    {
        var machine = await db.Machines.FirstOrDefaultAsync(m => m.Id == machineId && m.TeamId == id);
        if (machine is null)
        {
            TempData["Error"] = "Machine not on this team.";
            return RedirectToPage(new { id, tab = "membership" });
        }

        machine.TeamId = null;
        await db.SaveChangesAsync();
        return RedirectToPage(new { id, tab = "membership" });
    }

    public async Task<IActionResult> OnPostSetAppListExcludedAsync(int id, bool excluded)
    {
        var link = await db.TeamAppListLinks
            .FirstOrDefaultAsync(l => l.TeamId == id && l.AppListId == AppListId);
        if (link is null)
        {
            TempData["Error"] = "App list not linked to that team.";
            return RedirectToPage(new { id, tab = "apps" });
        }

        link.IsExcluded = excluded;
        await db.SaveChangesAsync();
        return RedirectToPage(new { id, tab = "apps" });
    }

    public async Task<IActionResult> OnPostSetPrimaryAppListAsync(int id)
    {
        try
        {
            await specReview.SetPrimaryAppListAsync(id, AppListId, HttpContext.RequestAborted);
            TempData["Message"] = "Primary app list updated.";
        }
        catch (Exception ex)
        {
            TempData["Error"] = ex.Message;
        }

        return RedirectToPage(new { id, tab = "apps" });
    }

    public async Task<IActionResult> OnPostUnlinkAppListAsync(int id)
    {
        var link = await db.TeamAppListLinks
            .FirstOrDefaultAsync(l => l.TeamId == id && l.AppListId == AppListId);
        if (link is null)
        {
            TempData["Error"] = "App list not linked to that team.";
            return RedirectToPage(new { id, tab = "apps" });
        }

        db.TeamAppListLinks.Remove(link);
        await db.SaveChangesAsync();
        return RedirectToPage(new { id, tab = "apps" });
    }

    public async Task<IActionResult> OnPostSyncEntraAsync(int id)
    {
        var result = await entraSync.SyncTeamAsync(id, HttpContext.RequestAborted);
        if (result.Ok)
            TempData["Message"] = result.Summary;
        else
            TempData["Error"] = result.Summary;
        return RedirectToPage(new { id, tab = "membership" });
    }

    private async Task<bool> LoadAsync(int id)
    {
        var team = await db.Teams.AsNoTracking().FirstOrDefaultAsync(t => t.Id == id);
        if (team is null) return false;
        Team = team;

        var auth = await authSettings.GetAsync(HttpContext.RequestAborted);
        EntraGraphMembershipEnabled = auth.EntraGraphMembershipEnabled;
        ManualCsvMembershipEnabled = auth.ManualCsvMembershipEnabled;

        var people = await db.PersonTeams.AsNoTracking()
            .Where(p => p.TeamId == id)
            .OrderBy(p => p.Username)
            .ToListAsync();

        var sessionKeys = (await db.Sessions.AsNoTracking()
            .Select(s => new { s.Username, s.Domain })
            .ToListAsync())
            .Select(s => TeamPageHelpers.NormalizeKey(s.Username, s.Domain))
            .Where(k => k.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        People = people.Select(p =>
        {
            var matched = TeamPageHelpers.MatchKeys(p.Username, p.Domain)
                .Any(mk => sessionKeys.Contains(mk));
            return new PersonRow(
                p.Id,
                p.Username,
                p.Domain,
                p.DisplayName,
                p.Email,
                matched);
        }).ToList();

        Machines = await db.Machines.AsNoTracking()
            .Where(m => m.TeamId == id)
            .OrderBy(m => m.Hostname)
            .Select(m => new MachineRow(m.Id, m.Hostname, m.FriendlyName))
            .ToListAsync();

        var links = await db.TeamAppListLinks.AsNoTracking()
            .Include(l => l.AppList)
            .ThenInclude(a => a.Entries)
            .Where(l => l.TeamId == id)
            .OrderBy(l => l.AppList.Name)
            .ToListAsync();

        TrackingLists = links.Where(l => !l.IsExcluded).Select(ToAppListRow).ToList();
        IgnoredLists = links.Where(l => l.IsExcluded).Select(ToAppListRow).ToList();

        var machineIds = Machines.Select(m => m.Id).ToList();
        OverrideCount = machineIds.Count == 0
            ? 0
            : await db.MachineAppListOverrides.AsNoTracking()
                .CountAsync(o => machineIds.Contains(o.MachineId));

        return true;
    }

    private static AppListRow ToAppListRow(TeamAppListLink l)
    {
        var entries = l.AppList.Entries
            .OrderBy(e => e.DisplayName ?? e.ProcessName)
            .Select(e => string.IsNullOrWhiteSpace(e.DisplayName) ? e.ProcessName : e.DisplayName!)
            .ToList();
        return new AppListRow(l.AppListId, l.AppList.Name, entries.Count, string.Join(", ", entries), l.IsPrimary);
    }

    private static string NormalizeTab(string? tab) =>
        string.Equals(tab, "apps", StringComparison.OrdinalIgnoreCase) ? "apps" : "membership";

    public sealed record PersonRow(
        int Id,
        string Username,
        string? Domain,
        string? DisplayName,
        string? Email,
        bool SeenInSessions);

    public sealed record MachineRow(int Id, string Hostname, string? FriendlyName);

    public sealed record AppListRow(int Id, string Name, int EntryCount, string AppsSummary, bool IsPrimary);
}
