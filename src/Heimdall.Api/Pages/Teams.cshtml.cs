using System.Text;
using Heimdall.Api.Data;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;

namespace Heimdall.Api.Pages;

public class TeamsModel(HeimdallDbContext db) : PageModel
{
    public IReadOnlyList<TeamNode> TeamTree { get; private set; } = [];
    public IReadOnlyList<TeamOption> TeamOptions { get; private set; } = [];
    public IReadOnlyList<PersonRow> People { get; private set; } = [];
    public IReadOnlyList<string> SessionUsernames { get; private set; } = [];
    public IReadOnlyList<MachinePick> AllMachines { get; private set; } = [];
    public IReadOnlyList<TeamMachinesBlock> TeamMachines { get; private set; } = [];
    public IReadOnlyList<TeamAppListsBlock> TeamAppLists { get; private set; } = [];
    /// <summary>Non-auto-discovered lists eligible to link; filtered client-side by selected team.</summary>
    public IReadOnlyList<AppListPick> LinkableAppLists { get; private set; } = [];
    /// <summary>App list ids already linked per team (for picker filtering).</summary>
    public IReadOnlyDictionary<int, IReadOnlyList<int>> LinkedAppListIdsByTeam { get; private set; }
        = new Dictionary<int, IReadOnlyList<int>>();
    public bool IsEmpty => TeamOptions.Count == 0 && People.Count == 0;

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
    public int TeamId { get; set; }

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

    [BindProperty]
    public int PersonId { get; set; }

    [BindProperty]
    public IFormFile? CsvFile { get; set; }

    [BindProperty]
    public int AssignTeamId { get; set; }

    [BindProperty]
    public List<int> SelectedMachineIds { get; set; } = [];

    [BindProperty]
    public int LinkTeamId { get; set; }

    [BindProperty]
    public int LinkAppListId { get; set; }

    [BindProperty]
    public bool LinkAsIgnored { get; set; }

    [BindProperty]
    public int AppListId { get; set; }

    public async Task OnGetAsync(int? editTeam, int? editPerson)
    {
        await LoadAsync();
        if (editTeam is int tid)
        {
            var t = await db.Teams.AsNoTracking().FirstOrDefaultAsync(x => x.Id == tid);
            if (t is not null)
            {
                EditingTeamId = t.Id;
                TeamName = t.Name;
                TeamCode = t.Code;
                TeamParentId = t.ParentTeamId;
                TeamIsPublicFacing = t.IsPublicFacing;
            }
        }

        if (editPerson is int pid)
        {
            var p = await db.PersonTeams.AsNoTracking().FirstOrDefaultAsync(x => x.Id == pid);
            if (p is not null)
            {
                EditingPersonId = p.Id;
                PersonUsername = p.Username;
                PersonDomain = p.Domain;
                PersonDisplayName = p.DisplayName;
                PersonEmail = p.Email;
                PersonTeamId = p.TeamId;
            }
        }
    }

    public async Task<IActionResult> OnPostSaveTeamAsync()
    {
        if (string.IsNullOrWhiteSpace(TeamName))
        {
            TempData["Error"] = "Team name is required.";
            return RedirectToPage();
        }

        var name = TeamName.Trim();
        if (TeamParentId is int parentId)
        {
            if (EditingTeamId is int self && parentId == self)
            {
                TempData["Error"] = "A team cannot be its own parent.";
                return RedirectToPage(null, new { editTeam = EditingTeamId });
            }

            if (!await db.Teams.AnyAsync(t => t.Id == parentId))
            {
                TempData["Error"] = "Parent team not found.";
                return RedirectToPage();
            }

            if (EditingTeamId is int editId && await WouldCreateCycleAsync(editId, parentId))
            {
                TempData["Error"] = "That parent would create a cycle in the team hierarchy.";
                return RedirectToPage(null, new { editTeam = EditingTeamId });
            }
        }

        if (EditingTeamId is int id)
        {
            var team = await db.Teams.FindAsync(id);
            if (team is null)
            {
                TempData["Error"] = "Team not found.";
                return RedirectToPage();
            }

            team.Name = name;
            team.Code = NullIfEmpty(TeamCode);
            team.ParentTeamId = TeamParentId;
            team.IsPublicFacing = TeamIsPublicFacing;
            TempData["Message"] = $"Updated team “{team.Name}”.";
        }
        else
        {
            db.Teams.Add(new Team
            {
                Name = name,
                Code = NullIfEmpty(TeamCode),
                ParentTeamId = TeamParentId,
                IsPublicFacing = TeamIsPublicFacing
            });
            TempData["Message"] = $"Created team “{name}”.";
        }

        await db.SaveChangesAsync();
        return RedirectToPage();
    }

    public async Task<IActionResult> OnPostDeleteTeamAsync()
    {
        var team = await db.Teams
            .Include(t => t.Children)
            .Include(t => t.Members)
            .FirstOrDefaultAsync(t => t.Id == TeamId);
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

        if (team.Children.Count > 0)
        {
            TempData["Error"] = $"Cannot delete “{team.Name}” while it has child teams. Reassign or delete children first.";
            return RedirectToPage();
        }

        var machines = await db.Machines.Where(m => m.TeamId == team.Id).ToListAsync();
        foreach (var m in machines)
            m.TeamId = null;

        var links = await db.TeamAppListLinks.Where(l => l.TeamId == team.Id).ToListAsync();
        db.TeamAppListLinks.RemoveRange(links);

        // Clear optional primary-team metadata when that team is deleted
        var lists = await db.AppLists.Where(a => a.TeamId == team.Id).ToListAsync();
        foreach (var a in lists)
        {
            a.TeamId = null;
            a.IsTeamExcluded = false;
        }

        db.PersonTeams.RemoveRange(team.Members);
        db.Teams.Remove(team);
        await db.SaveChangesAsync();
        TempData["Message"] = $"Deleted team “{team.Name}”.";
        return RedirectToPage();
    }

    public async Task<IActionResult> OnPostAssignMachinesAsync()
    {
        if (!await db.Teams.AnyAsync(t => t.Id == AssignTeamId))
        {
            TempData["Error"] = "Team not found.";
            return RedirectToPage();
        }

        var selected = SelectedMachineIds.Distinct().ToHashSet();
        // Only touch machines on this team or newly selected — avoid loading the full fleet.
        var touched = await db.Machines
            .Where(m => m.TeamId == AssignTeamId || selected.Contains(m.Id))
            .ToListAsync();
        foreach (var m in touched)
        {
            if (selected.Contains(m.Id))
                m.TeamId = AssignTeamId;
            else if (m.TeamId == AssignTeamId)
                m.TeamId = null;
        }

        await db.SaveChangesAsync();
        TempData["Message"] = $"Updated machine assignments ({selected.Count} on team).";
        return RedirectToPage();
    }

    public async Task<IActionResult> OnPostLinkAppListAsync()
    {
        var list = await db.AppLists.FindAsync(LinkAppListId);
        if (list is null || !await db.Teams.AnyAsync(t => t.Id == LinkTeamId))
        {
            TempData["Error"] = "Team or app list not found.";
            return RedirectToPage();
        }

        var link = await db.TeamAppListLinks
            .FirstOrDefaultAsync(l => l.TeamId == LinkTeamId && l.AppListId == LinkAppListId);
        if (link is null)
        {
            db.TeamAppListLinks.Add(new TeamAppListLink
            {
                TeamId = LinkTeamId,
                AppListId = LinkAppListId,
                IsExcluded = LinkAsIgnored
            });
        }
        else
        {
            link.IsExcluded = LinkAsIgnored;
        }

        await db.SaveChangesAsync();
        TempData["Message"] = LinkAsIgnored
            ? $"Linked “{list.Name}” as do not track for the team."
            : $"Tracking “{list.Name}” for the team.";
        return RedirectToPage();
    }

    public async Task<IActionResult> OnPostSetAppListExcludedAsync(bool excluded)
    {
        var link = await db.TeamAppListLinks
            .Include(l => l.AppList)
            .FirstOrDefaultAsync(l => l.TeamId == LinkTeamId && l.AppListId == AppListId);
        if (link is null)
        {
            TempData["Error"] = "App list not linked to that team.";
            return RedirectToPage();
        }

        link.IsExcluded = excluded;
        await db.SaveChangesAsync();
        TempData["Message"] = excluded
            ? $"“{link.AppList.Name}” set to do not track."
            : $"“{link.AppList.Name}” set to actively tracking.";
        return RedirectToPage();
    }

    public async Task<IActionResult> OnPostUnlinkAppListAsync()
    {
        var link = await db.TeamAppListLinks
            .Include(l => l.AppList)
            .FirstOrDefaultAsync(l => l.TeamId == LinkTeamId && l.AppListId == AppListId);
        if (link is null)
        {
            TempData["Error"] = "App list not linked to that team.";
            return RedirectToPage();
        }

        var name = link.AppList.Name;
        db.TeamAppListLinks.Remove(link);
        await db.SaveChangesAsync();
        TempData["Message"] = $"Unlinked “{name}” from team.";
        return RedirectToPage();
    }

    public async Task<IActionResult> OnPostSavePersonAsync()
    {
        if (string.IsNullOrWhiteSpace(PersonUsername))
        {
            TempData["Error"] = "Username is required.";
            return RedirectToPage();
        }

        if (!await db.Teams.AnyAsync(t => t.Id == PersonTeamId))
        {
            TempData["Error"] = "Select a valid team.";
            return RedirectToPage();
        }

        var (username, domain) = SplitUser(PersonUsername, PersonDomain);

        if (EditingPersonId is int id)
        {
            var person = await db.PersonTeams.FindAsync(id);
            if (person is null)
            {
                TempData["Error"] = "Person assignment not found.";
                return RedirectToPage();
            }

            // Domain is no longer collected in the form; keep existing unless username embeds DOMAIN\user.
            if (domain is null && PersonUsername.IndexOf('\\') < 0)
                domain = person.Domain;

            person.Username = username;
            person.Domain = domain;
            person.DisplayName = NullIfEmpty(PersonDisplayName);
            person.Email = NullIfEmpty(PersonEmail);
            person.TeamId = PersonTeamId;
            TempData["Message"] = $"Updated assignment for {FormatUser(username, domain)}.";
        }
        else
        {
            db.PersonTeams.Add(new PersonTeam
            {
                Username = username,
                Domain = domain,
                DisplayName = NullIfEmpty(PersonDisplayName),
                Email = NullIfEmpty(PersonEmail),
                TeamId = PersonTeamId
            });
            TempData["Message"] = $"Assigned {FormatUser(username, domain)} to a team.";
        }

        await db.SaveChangesAsync();
        return RedirectToPage();
    }

    public async Task<IActionResult> OnPostDeletePersonAsync()
    {
        var person = await db.PersonTeams.FindAsync(PersonId);
        if (person is null)
        {
            TempData["Error"] = "Person assignment not found.";
            return RedirectToPage();
        }

        db.PersonTeams.Remove(person);
        await db.SaveChangesAsync();
        TempData["Message"] = $"Removed assignment for {FormatUser(person.Username, person.Domain)}.";
        return RedirectToPage();
    }

    public async Task<IActionResult> OnPostImportCsvAsync()
    {
        if (CsvFile is null || CsvFile.Length == 0)
        {
            TempData["Error"] = "Choose a CSV file to upload.";
            return RedirectToPage();
        }

        await using var stream = CsvFile.OpenReadStream();
        using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
        var text = await reader.ReadToEndAsync();
        var result = await ImportCsvAsync(text);
        if (result.Error is not null)
        {
            TempData["Error"] = result.Error;
            return RedirectToPage();
        }

        TempData["Message"] =
            $"CSV import complete: {result.TeamsCreated} team(s) created, {result.PeopleCreated} person(s) created, {result.PeopleUpdated} updated, {result.Skipped} skipped.";
        return RedirectToPage();
    }

    public IActionResult OnGetTemplate()
    {
        const string csv =
            """
            Username,Domain,DisplayName,Email,Team,ParentTeam
            jsmith,ARUP,Jane Smith,jane.smith@example.com,Digital,Buildings
            ajones,,Alex Jones,alex.jones@example.com,Structures,
            """;
        var bytes = Encoding.UTF8.GetPreamble().Concat(Encoding.UTF8.GetBytes(csv.Replace("\r\n", "\n").Replace("\n", "\r\n"))).ToArray();
        return File(bytes, "text/csv", "heimdall-teams-template.csv");
    }

    private async Task LoadAsync()
    {
        var teams = await db.Teams.AsNoTracking().OrderBy(t => t.Name).ToListAsync();
        var people = await db.PersonTeams.AsNoTracking().Include(p => p.Team).OrderBy(p => p.Username).ToListAsync();
        var sessions = await db.Sessions.AsNoTracking()
            .Select(s => new { s.Username, s.Domain })
            .ToListAsync();

        var byParent = teams.ToLookup(t => t.ParentTeamId);
        TeamTree = BuildTree(byParent, null, 0);
        TeamOptions = FlattenOptions(TeamTree);

        var sessionKeys = sessions
            .Select(s => NormalizeKey(s.Username, s.Domain))
            .Where(k => k.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(k => k, StringComparer.OrdinalIgnoreCase)
            .ToList();
        SessionUsernames = sessionKeys;

        var matchLookup = people
            .SelectMany(p => MatchKeys(p.Username, p.Domain).Select(k => (Key: k, Person: p)))
            .GroupBy(x => x.Key, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.First().Person, StringComparer.OrdinalIgnoreCase);

        People = people.Select(p =>
        {
            var matched = sessionKeys.Any(sk =>
                MatchKeys(p.Username, p.Domain).Any(mk => string.Equals(mk, sk, StringComparison.OrdinalIgnoreCase)));
            return new PersonRow(
                p.Id,
                p.Username,
                p.Domain,
                p.DisplayName,
                p.Email,
                p.TeamId,
                p.Team.Name,
                matched);
        }).ToList();

        // Surface session users that aren't assigned yet (for empty-state / awareness)
        _ = matchLookup;

        AllMachines = await db.Machines.AsNoTracking()
            .OrderBy(m => m.Hostname)
            .Select(m => new MachinePick(m.Id, m.Hostname, m.FriendlyName, m.TeamId))
            .ToListAsync();

        TeamMachines = TeamOptions.Select(t => new TeamMachinesBlock(
            t.Id,
            t.Label.TrimStart('—', ' '),
            AllMachines.Where(m => m.TeamId == t.Id).ToList())).ToList();

        var linksRaw = await db.TeamAppListLinks.AsNoTracking()
            .Include(l => l.AppList)
            .ThenInclude(a => a.Entries)
            .OrderBy(l => l.AppList.Name)
            .ToListAsync();

        var linkedPicks = linksRaw.Select(l =>
        {
            var entries = l.AppList.Entries
                .OrderBy(e => e.DisplayName ?? e.ProcessName)
                .Select(e => e.DisplayName != null && e.DisplayName != "" ? e.DisplayName : e.ProcessName)
                .ToList();
            return new
            {
                l.TeamId,
                Pick = new AppListPick(
                    l.AppListId,
                    l.AppList.Name,
                    l.TeamId,
                    l.IsExcluded,
                    entries.Count,
                    string.Join(", ", entries))
            };
        }).ToList();

        TeamAppLists = TeamOptions.Select(t => new TeamAppListsBlock(
            t.Id,
            t.Label.TrimStart('—', ' '),
            linkedPicks.Where(x => x.TeamId == t.Id && !x.Pick.IsTeamExcluded).Select(x => x.Pick).ToList(),
            linkedPicks.Where(x => x.TeamId == t.Id && x.Pick.IsTeamExcluded).Select(x => x.Pick).ToList())).ToList();

        LinkedAppListIdsByTeam = linksRaw
            .GroupBy(l => l.TeamId)
            .ToDictionary(g => g.Key, g => (IReadOnlyList<int>)g.Select(l => l.AppListId).ToList());

        LinkableAppLists = await db.AppLists.AsNoTracking()
            .Where(a => !a.IsAutoDiscovered)
            .OrderBy(a => a.Name)
            .Select(a => new AppListPick(a.Id, a.Name, a.TeamId, false, a.Entries.Count, ""))
            .ToListAsync();
    }

    private static List<TeamNode> BuildTree(ILookup<int?, Team> byParent, int? parentId, int depth)
    {
        return byParent[parentId]
            .OrderBy(t => t.Name, StringComparer.OrdinalIgnoreCase)
            .Select(t => new TeamNode(
                t.Id,
                t.Name,
                t.Code,
                t.ParentTeamId,
                t.IsPublicFacing,
                depth,
                BuildTree(byParent, t.Id, depth + 1)))
            .ToList();
    }

    private static List<TeamOption> FlattenOptions(IEnumerable<TeamNode> nodes)
    {
        var list = new List<TeamOption>();
        void Walk(IEnumerable<TeamNode> items)
        {
            foreach (var n in items)
            {
                var prefix = n.Depth == 0 ? "" : new string('—', n.Depth) + " ";
                list.Add(new TeamOption(n.Id, prefix + n.Name));
                Walk(n.Children);
            }
        }

        Walk(nodes);
        return list;
    }

    private async Task<bool> WouldCreateCycleAsync(int teamId, int proposedParentId)
    {
        var current = proposedParentId;
        var guard = 0;
        while (guard++ < 100)
        {
            if (current == teamId) return true;
            var parent = await db.Teams.AsNoTracking()
                .Where(t => t.Id == current)
                .Select(t => t.ParentTeamId)
                .FirstOrDefaultAsync();
            if (parent is null) return false;
            current = parent.Value;
        }

        return true;
    }

    private async Task<(int TeamsCreated, int PeopleCreated, int PeopleUpdated, int Skipped, string? Error)> ImportCsvAsync(string text)
    {
        var lines = text.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries);
        if (lines.Length == 0)
            return (0, 0, 0, 0, "CSV is empty.");

        var headerCells = ParseCsvLine(lines[0]);
        var headers = headerCells.Select(h => h.Trim().ToLowerInvariant()).ToList();
        var hasTeam = headers.Contains("team");
        var hasUsername = headers.Contains("username");
        if (!hasUsername || !hasTeam)
            return (0, 0, 0, 0, "CSV must include at least Username and Team columns. Optional: Domain, DisplayName, Email, ParentTeam, Code.");

        int Col(string name)
        {
            var i = headers.IndexOf(name);
            return i;
        }

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

        // Ensure all teams exist first (including parents)
        for (var i = 1; i < lines.Length; i++)
        {
            var cells = ParseCsvLine(lines[i]);
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

            teamsCreated += await EnsureTeamAsync(teamName, string.IsNullOrWhiteSpace(parentName) ? null : parentName, NullIfEmpty(code));
        }

        await db.SaveChangesAsync();

        for (var i = 1; i < lines.Length; i++)
        {
            var cells = ParseCsvLine(lines[i]);
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

            var (username, domain) = SplitUser(rawUser, NullIfEmpty(Cell(iDomain)));
            var existing = await FindPersonAsync(username, domain);
            if (existing is null)
            {
                db.PersonTeams.Add(new PersonTeam
                {
                    Username = username,
                    Domain = domain,
                    DisplayName = NullIfEmpty(Cell(iDisplay)),
                    Email = NullIfEmpty(Cell(iEmail)),
                    TeamId = team.Id
                });
                peopleCreated++;
            }
            else
            {
                existing.TeamId = team.Id;
                existing.Domain = domain ?? existing.Domain;
                var display = NullIfEmpty(Cell(iDisplay));
                var email = NullIfEmpty(Cell(iEmail));
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
            MatchKeys(p.Username, p.Domain).Any(k =>
                MatchKeys(username, domain).Any(mk => string.Equals(k, mk, StringComparison.OrdinalIgnoreCase))));
    }

    public static (string Username, string? Domain) SplitUser(string username, string? domain)
    {
        var u = username.Trim();
        if (u.Contains('\\'))
        {
            var parts = u.Split('\\', 2);
            return (parts[1].Trim(), string.IsNullOrWhiteSpace(parts[0]) ? NullIfEmpty(domain) : parts[0].Trim());
        }

        return (u, NullIfEmpty(domain));
    }

    public static IEnumerable<string> MatchKeys(string username, string? domain)
    {
        var (user, dom) = SplitUser(username, domain);
        if (string.IsNullOrWhiteSpace(user)) yield break;
        yield return user;
        if (!string.IsNullOrWhiteSpace(dom))
            yield return $"{dom}\\{user}";
    }

    public static string NormalizeKey(string username, string? domain)
    {
        var (user, dom) = SplitUser(username, domain);
        if (string.IsNullOrWhiteSpace(user)) return "";
        return string.IsNullOrWhiteSpace(dom) ? user : $"{dom}\\{user}";
    }

    public static string FormatUser(string username, string? domain) =>
        string.IsNullOrWhiteSpace(domain) ? username : $"{domain}\\{username}";

    private static string? NullIfEmpty(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static List<string> ParseCsvLine(string line)
    {
        var result = new List<string>();
        var sb = new StringBuilder();
        var inQuotes = false;
        for (var i = 0; i < line.Length; i++)
        {
            var c = line[i];
            if (inQuotes)
            {
                if (c == '"')
                {
                    if (i + 1 < line.Length && line[i + 1] == '"')
                    {
                        sb.Append('"');
                        i++;
                    }
                    else
                    {
                        inQuotes = false;
                    }
                }
                else
                {
                    sb.Append(c);
                }
            }
            else if (c == '"')
            {
                inQuotes = true;
            }
            else if (c == ',')
            {
                result.Add(sb.ToString());
                sb.Clear();
            }
            else
            {
                sb.Append(c);
            }
        }

        result.Add(sb.ToString());
        return result;
    }

    public record TeamNode(int Id, string Name, string? Code, int? ParentTeamId, bool IsPublicFacing, int Depth, IReadOnlyList<TeamNode> Children);
    public record TeamOption(int Id, string Label);
    public record PersonRow(
        int Id,
        string Username,
        string? Domain,
        string? DisplayName,
        string? Email,
        int TeamId,
        string TeamName,
        bool SeenInSessions);
    public record MachinePick(int Id, string Hostname, string? FriendlyName, int? TeamId);
    public record AppListPick(int Id, string Name, int? TeamId, bool IsTeamExcluded, int EntryCount, string AppsSummary = "");
    public record TeamMachinesBlock(int TeamId, string TeamName, IReadOnlyList<MachinePick> Machines);
    public record TeamAppListsBlock(
        int TeamId,
        string TeamName,
        IReadOnlyList<AppListPick> Active,
        IReadOnlyList<AppListPick> Ignored);
}
