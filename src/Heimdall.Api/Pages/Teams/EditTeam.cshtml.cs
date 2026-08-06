using Heimdall.Api.Data;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;

namespace Heimdall.Api.Pages.Teams;

public class EditTeamModel(HeimdallDbContext db) : PageModel
{
    public IReadOnlyList<TeamPageHelpers.TeamOption> TeamOptions { get; private set; } = [];
    public bool IsEdit => EditingTeamId is not null;

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
            db.Teams.Add(team);
            await db.SaveChangesAsync();
            return RedirectToPage("/Teams/Detail", new { id = team.Id });
        }

        await db.SaveChangesAsync();
        return RedirectToPage("/Teams/Detail", new { id = teamId });
    }
}
