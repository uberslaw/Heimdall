using System.Text;
using Heimdall.Api.Data;
using Heimdall.Api.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;

namespace Heimdall.Api.Pages.Teams;

public class ImportCsvModel(HeimdallDbContext db, DirectoryAuthSettingsService authSettings) : PageModel
{
    [BindProperty]
    public IFormFile? CsvFile { get; set; }

    public string? FormError { get; private set; }
    public bool ManualCsvEnabled { get; private set; } = true;

    public async Task OnGetAsync()
    {
        ManualCsvEnabled = await authSettings.IsManualCsvMembershipEnabledAsync(HttpContext.RequestAborted);
        if (!ManualCsvEnabled)
            FormError = "Manual/CSV membership is turned off under Admin → Auth.";
    }

    public IActionResult OnGetTemplate()
    {
        const string csv =
            """
            Username,Domain,DisplayName,Email,Team,ParentTeam
            jsmith,ARUP,Jane Smith,jane.smith@example.com,Digital,Buildings
            ajones,,Alex Jones,alex.jones@example.com,Structures,
            """;
        var bytes = Encoding.UTF8.GetPreamble()
            .Concat(Encoding.UTF8.GetBytes(csv.Replace("\r\n", "\n").Replace("\n", "\r\n")))
            .ToArray();
        return File(bytes, "text/csv", "heimdall-teams-template.csv");
    }

    public async Task<IActionResult> OnPostAsync()
    {
        ManualCsvEnabled = await authSettings.IsManualCsvMembershipEnabledAsync(HttpContext.RequestAborted);
        if (!ManualCsvEnabled)
        {
            FormError = "Manual/CSV membership is turned off under Admin → Auth.";
            return Page();
        }

        if (CsvFile is null || CsvFile.Length == 0)
        {
            FormError = "Choose a CSV file to upload.";
            return Page();
        }

        await using var stream = CsvFile.OpenReadStream();
        using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
        var text = await reader.ReadToEndAsync();
        var result = await ImportCsvAsync(text);
        if (result.Error is not null)
        {
            FormError = result.Error;
            return Page();
        }

        return RedirectToPage("/Teams");
    }

    private async Task<(int TeamsCreated, int PeopleCreated, int PeopleUpdated, int Skipped, string? Error)> ImportCsvAsync(string text)
    {
        var lines = text.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries);
        if (lines.Length == 0)
            return (0, 0, 0, 0, "CSV is empty.");

        var headerCells = TeamPageHelpers.ParseCsvLine(lines[0]);
        var headers = headerCells.Select(h => h.Trim().ToLowerInvariant()).ToList();
        if (!headers.Contains("username") || !headers.Contains("team"))
            return (0, 0, 0, 0, "CSV must include at least Username and Team columns. Optional: Domain, DisplayName, Email, ParentTeam, Code.");

        int Col(string name) => headers.IndexOf(name);

        var iUsername = Col("username");
        var iDomain = Col("domain");
        var iDisplay = Col("displayname");
        var iEmail = Col("email");
        var iTeam = Col("team");
        var iParent = Col("parentteam");
        var iCode = Col("code");

        var teamsCreated = 0;
        var peopleCreated = 0;
        var peopleUpdated = 0;
        var skipped = 0;

        for (var i = 1; i < lines.Length; i++)
        {
            var cells = TeamPageHelpers.ParseCsvLine(lines[i]);
            string Cell(int idx) => idx >= 0 && idx < cells.Count ? cells[idx].Trim() : "";

            var teamName = Cell(iTeam);
            var parentName = Cell(iParent);
            var code = Cell(iCode);
            if (string.IsNullOrWhiteSpace(teamName))
            {
                skipped++;
                continue;
            }

            if (!string.IsNullOrWhiteSpace(parentName))
                teamsCreated += await EnsureTeamAsync(parentName, null, null);

            teamsCreated += await EnsureTeamAsync(teamName, string.IsNullOrWhiteSpace(parentName) ? null : parentName, TeamPageHelpers.NullIfEmpty(code));
        }

        await db.SaveChangesAsync();

        for (var i = 1; i < lines.Length; i++)
        {
            var cells = TeamPageHelpers.ParseCsvLine(lines[i]);
            string Cell(int idx) => idx >= 0 && idx < cells.Count ? cells[idx].Trim() : "";

            var rawUser = Cell(iUsername);
            var teamName = Cell(iTeam);
            if (string.IsNullOrWhiteSpace(rawUser) || string.IsNullOrWhiteSpace(teamName))
            {
                skipped++;
                continue;
            }

            var team = await db.Teams.FirstOrDefaultAsync(t => t.Name.ToLower() == teamName.ToLower());
            if (team is null)
            {
                skipped++;
                continue;
            }

            var (username, domain) = TeamPageHelpers.SplitUser(rawUser, TeamPageHelpers.NullIfEmpty(Cell(iDomain)));
            var existing = await FindPersonAsync(username, domain);
            if (existing is null)
            {
                db.PersonTeams.Add(new PersonTeam
                {
                    Username = username,
                    Domain = domain,
                    DisplayName = TeamPageHelpers.NullIfEmpty(Cell(iDisplay)),
                    Email = TeamPageHelpers.NullIfEmpty(Cell(iEmail)),
                    TeamId = team.Id
                });
                peopleCreated++;
            }
            else
            {
                existing.TeamId = team.Id;
                existing.Domain = domain ?? existing.Domain;
                var display = TeamPageHelpers.NullIfEmpty(Cell(iDisplay));
                var email = TeamPageHelpers.NullIfEmpty(Cell(iEmail));
                if (display is not null) existing.DisplayName = display;
                if (email is not null) existing.Email = email;
                peopleUpdated++;
            }
        }

        await db.SaveChangesAsync();
        return (teamsCreated, peopleCreated, peopleUpdated, skipped, null);
    }

    private async Task<int> EnsureTeamAsync(string name, string? parentName, string? code)
    {
        var existing = await db.Teams.FirstOrDefaultAsync(t => t.Name.ToLower() == name.ToLower());
        int? parentId = null;
        if (!string.IsNullOrWhiteSpace(parentName))
        {
            var parent = await db.Teams.FirstOrDefaultAsync(t => t.Name.ToLower() == parentName.ToLower());
            parentId = parent?.Id;
        }

        if (existing is not null)
        {
            if (parentId is not null && existing.ParentTeamId is null)
                existing.ParentTeamId = parentId;
            if (code is not null && string.IsNullOrWhiteSpace(existing.Code))
                existing.Code = code;
            return 0;
        }

        db.Teams.Add(new Team
        {
            Name = name.Trim(),
            Code = code,
            ParentTeamId = parentId
        });
        await db.SaveChangesAsync();
        return 1;
    }

    private async Task<PersonTeam?> FindPersonAsync(string username, string? domain)
    {
        var all = await db.PersonTeams.ToListAsync();
        return all.FirstOrDefault(p =>
            TeamPageHelpers.MatchKeys(p.Username, p.Domain).Any(k =>
                TeamPageHelpers.MatchKeys(username, domain).Any(mk => string.Equals(k, mk, StringComparison.OrdinalIgnoreCase))));
    }
}
