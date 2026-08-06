using Heimdall.Api.Data;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;

namespace Heimdall.Api.Pages.Teams;

public class EditPersonModel(HeimdallDbContext db) : PageModel
{
    public IReadOnlyList<TeamPageHelpers.TeamOption> TeamOptions { get; private set; } = [];
    public bool IsEdit => EditingPersonId is not null;
    public string? FormError { get; private set; }

    [BindProperty]
    public int? EditingPersonId { get; set; }

    [BindProperty]
    public string PersonUsername { get; set; } = "";

    [BindProperty]
    public string? PersonDomain { get; set; }

    [BindProperty]
    public string? PersonDisplayName { get; set; }

    [BindProperty]
    public string? PersonEmail { get; set; }

    [BindProperty]
    public int PersonTeamId { get; set; }

    public async Task<IActionResult> OnGetAsync(int? id, int? teamId)
    {
        TeamOptions = await TeamPageHelpers.LoadTeamOptionsAsync(db);
        if (id is int pid)
        {
            var p = await db.PersonTeams.AsNoTracking().FirstOrDefaultAsync(x => x.Id == pid);
            if (p is null)
            {
                TempData["Error"] = "Person assignment not found.";
                return RedirectToPage("/Teams");
            }

            EditingPersonId = p.Id;
            PersonUsername = p.Username;
            PersonDomain = p.Domain;
            PersonDisplayName = p.DisplayName;
            PersonEmail = p.Email;
            PersonTeamId = p.TeamId;
        }
        else if (teamId is int tid && await db.Teams.AnyAsync(t => t.Id == tid))
        {
            PersonTeamId = tid;
        }

        return Page();
    }

    public async Task<IActionResult> OnPostAsync()
    {
        TeamOptions = await TeamPageHelpers.LoadTeamOptionsAsync(db);

        if (string.IsNullOrWhiteSpace(PersonUsername))
        {
            FormError = "Username is required.";
            return Page();
        }

        if (!await db.Teams.AnyAsync(t => t.Id == PersonTeamId))
        {
            FormError = "Select a valid team.";
            return Page();
        }

        var (username, domain) = TeamPageHelpers.SplitUser(PersonUsername, PersonDomain);
        int teamId;

        if (EditingPersonId is int id)
        {
            var person = await db.PersonTeams.FindAsync(id);
            if (person is null)
            {
                TempData["Error"] = "Person assignment not found.";
                return RedirectToPage("/Teams");
            }

            if (domain is null && PersonUsername.IndexOf('\\') < 0)
                domain = person.Domain;

            person.Username = username;
            person.Domain = domain;
            person.DisplayName = TeamPageHelpers.NullIfEmpty(PersonDisplayName);
            person.Email = TeamPageHelpers.NullIfEmpty(PersonEmail);
            person.TeamId = PersonTeamId;
            teamId = person.TeamId;
        }
        else
        {
            db.PersonTeams.Add(new PersonTeam
            {
                Username = username,
                Domain = domain,
                DisplayName = TeamPageHelpers.NullIfEmpty(PersonDisplayName),
                Email = TeamPageHelpers.NullIfEmpty(PersonEmail),
                TeamId = PersonTeamId
            });
            teamId = PersonTeamId;
        }

        await db.SaveChangesAsync();
        return RedirectToPage("/Teams/Detail", new { id = teamId, tab = "membership" });
    }
}
