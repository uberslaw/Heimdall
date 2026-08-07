using Heimdall.Api.Data;
using Heimdall.Api.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;

namespace Heimdall.Api.Pages.Teams;

public class EditTeamModel(HeimdallDbContext db, EntraGraphService graph, EntraTeamMembershipSyncService entraSync) : PageModel
{
    public IReadOnlyList<TeamPageHelpers.TeamOption> TeamOptions { get; private set; } = [];
    public bool IsEdit => EditingTeamId is not null;
    public bool EntraConfigured => graph.IsConfigured;
    public string? EntraSetupHint => graph.IsConfigured ? null : graph.SetupHint;

    [BindProperty]
    public int? EditingTeamId { get; set; }

    [BindProperty]
    public string TeamName { get; set; } = "";

    [BindProperty]
    public string? TeamCode { get; set; }

    [BindProperty]
    public int? TeamParentId { get; set; }

    [BindProperty]
    public bool TeamIsPublicFacing { get; set; }

    [BindProperty]
    public string? EntraGroupId { get; set; }

    public string? EntraGroupName { get; private set; }
    public DateTimeOffset? EntraMembersSyncedUtc { get; private set; }
    public string? EntraLastSyncError { get; private set; }

    public async Task<IActionResult> OnGetAsync(int? id)
    {
        TeamOptions = await TeamPageHelpers.LoadTeamOptionsAsync(db);
        if (id is int tid)
        {
            var t = await db.Teams.AsNoTracking().FirstOrDefaultAsync(x => x.Id == tid);
            if (t is null)
            {
                TempData["Error"] = "Team not found.";
                return RedirectToPage("/Teams");
            }

            EditingTeamId = t.Id;
            TeamName = t.Name;
            TeamCode = t.Code;
            TeamParentId = t.ParentTeamId;
            TeamIsPublicFacing = t.IsPublicFacing;
            EntraGroupId = t.EntraGroupId;
            EntraGroupName = t.EntraGroupName;
            EntraMembersSyncedUtc = t.EntraMembersSyncedUtc;
            EntraLastSyncError = t.EntraLastSyncError;
        }

        return Page();
    }

    public async Task<IActionResult> OnPostAsync()
    {
        TeamOptions = await TeamPageHelpers.LoadTeamOptionsAsync(db);

        if (string.IsNullOrWhiteSpace(TeamName))
        {
            ModelState.AddModelError(nameof(TeamName), "Team name is required.");
            return Page();
        }

        var name = TeamName.Trim();
        if (TeamParentId is int parentId)
        {
            if (EditingTeamId is int self && parentId == self)
            {
                ModelState.AddModelError(nameof(TeamParentId), "A team cannot be its own parent.");
                return Page();
            }

            if (!await db.Teams.AnyAsync(t => t.Id == parentId))
            {
                ModelState.AddModelError(nameof(TeamParentId), "Parent team not found.");
                return Page();
            }

            if (EditingTeamId is int editId && await TeamPageHelpers.WouldCreateCycleAsync(db, editId, parentId))
            {
                ModelState.AddModelError(nameof(TeamParentId), "That parent would create a cycle in the team hierarchy.");
                return Page();
            }
        }

        string? entraId = null;
        string? entraName = null;
        if (!string.IsNullOrWhiteSpace(EntraGroupId))
        {
            entraId = EntraGraphService.NormalizeGuid(EntraGroupId);
            if (entraId is null)
            {
                ModelState.AddModelError(nameof(EntraGroupId), "Entra group Object ID must be a GUID.");
                return Page();
            }

            if (graph.IsConfigured)
            {
                var (resolvedId, displayName, error) = await entraSync.ResolveGroupAsync(entraId, HttpContext.RequestAborted);
                if (error is not null && displayName is null)
                {
                    ModelState.AddModelError(nameof(EntraGroupId), error);
                    return Page();
                }

                entraId = resolvedId ?? entraId;
                entraName = displayName;
            }
        }

        int teamId;
        if (EditingTeamId is int id)
        {
            var team = await db.Teams.FindAsync(id);
            if (team is null)
            {
                TempData["Error"] = "Team not found.";
                return RedirectToPage("/Teams");
            }

            team.Name = name;
            team.Code = TeamPageHelpers.NullIfEmpty(TeamCode);
            team.ParentTeamId = TeamParentId;
            team.IsPublicFacing = TeamIsPublicFacing;
            ApplyEntraLink(team, entraId, entraName);
            teamId = team.Id;
        }
        else
        {
            var team = new Team
            {
                Name = name,
                Code = TeamPageHelpers.NullIfEmpty(TeamCode),
                ParentTeamId = TeamParentId,
                IsPublicFacing = TeamIsPublicFacing
            };
            ApplyEntraLink(team, entraId, entraName);
            db.Teams.Add(team);
            await db.SaveChangesAsync();
            return RedirectToPage("/Teams/Detail", new { id = team.Id });
        }

        await db.SaveChangesAsync();
        return RedirectToPage("/Teams/Detail", new { id = teamId });
    }

    private static void ApplyEntraLink(Team team, string? entraId, string? entraName)
    {
        if (entraId is null)
        {
            team.EntraGroupId = null;
            team.EntraGroupName = null;
            team.EntraMembersSyncedUtc = null;
            team.EntraLastSyncError = null;
            return;
        }

        if (!string.Equals(team.EntraGroupId, entraId, StringComparison.OrdinalIgnoreCase))
        {
            team.EntraMembersSyncedUtc = null;
            team.EntraLastSyncError = null;
        }

        team.EntraGroupId = entraId;
        if (!string.IsNullOrWhiteSpace(entraName))
            team.EntraGroupName = entraName;
    }
}
