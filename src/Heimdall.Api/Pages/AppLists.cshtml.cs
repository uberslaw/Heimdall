using Heimdall.Api.Data;
using Heimdall.Api.Services;
using Heimdall.Shared.Contracts;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;

namespace Heimdall.Api.Pages;

public class AppListsModel(HeimdallDbContext db, AppListService appLists) : PageModel
{
    public List<AppListRow> Lists { get; private set; } = [];
    public List<Team> Teams { get; private set; } = [];
    public List<AppListAuditLog> AuditLogs { get; private set; } = [];
    public IReadOnlyList<MachineHierarchy.RegionNode> Tree { get; private set; } = [];
    public List<Machine> AllMachines { get; private set; } = [];

    public AppListService.MachineAppListsView? Lookup { get; private set; }
    public AppListService.AnalysisResult? Analysis { get; private set; }
    public IReadOnlyList<AppListService.TeamAppListOption> TeamOptions { get; private set; } = [];
    public List<AppListService.ProposedApp> PendingProposals { get; private set; } = [];
    public string? FocusHostname { get; private set; }
    public AppAnalysisStatus? FocusStatus { get; private set; }

    [BindProperty] public int? EditId { get; set; }
    [BindProperty] public string ListName { get; set; } = "";
    [BindProperty] public int? TeamId { get; set; }
    [BindProperty] public string? Notes { get; set; }
    [BindProperty] public string ProcessesText { get; set; } = "";

    [BindProperty] public int ApplyAppListId { get; set; }
    [BindProperty] public List<string> SelectedRegions { get; set; } = [];
    [BindProperty] public List<string> SelectedOffices { get; set; } = [];
    [BindProperty] public List<string> SelectedMachines { get; set; } = [];
    [BindProperty] public bool ApplyGlobal { get; set; }

    [BindProperty] public string? LookupHostname { get; set; }
    [BindProperty] public string? AnalyzeHostname { get; set; }
    [BindProperty] public List<string> SelectedProcesses { get; set; } = [];
    [BindProperty] public int? ApplyTeamListId { get; set; }
    [BindProperty] public int? DefaultUploadTeamId { get; set; }
    [BindProperty] public IFormFile? UploadFile { get; set; }

    public async Task OnGetAsync(string? host = null, string? section = null)
    {
        await LoadAsync();
        if (!string.IsNullOrWhiteSpace(host))
        {
            FocusHostname = host.Trim();
            LookupHostname = FocusHostname;
            await LoadFocusAsync(FocusHostname);
        }
    }

    public async Task<IActionResult> OnPostSaveListAsync()
    {
        await LoadAsync();
        if (string.IsNullOrWhiteSpace(ListName))
        {
            TempData["Error"] = "List name is required.";
            return Page();
        }

        var entries = ParseProcessesText(ProcessesText);
        await appLists.CreateOrUpdateListAsync(EditId, ListName, TeamId, Notes, entries, HttpContext.RequestAborted);
        TempData["Message"] = EditId is null ? $"Created app list “{ListName}”." : $"Updated app list “{ListName}”.";
        return RedirectToPage(new { section = "lists" });
    }

    public async Task<IActionResult> OnPostUploadAsync()
    {
        await LoadAsync();
        if (UploadFile is null || UploadFile.Length == 0)
        {
            TempData["Error"] = "Choose a CSV or JSON file.";
            return Page();
        }

        await using var stream = UploadFile.OpenReadStream();
        var name = UploadFile.FileName ?? "";
        var (lists, entries) = name.EndsWith(".json", StringComparison.OrdinalIgnoreCase)
            ? await appLists.UploadJsonAsync(stream, DefaultUploadTeamId, HttpContext.RequestAborted)
            : await appLists.UploadCsvAsync(stream, DefaultUploadTeamId, HttpContext.RequestAborted);

        TempData["Message"] = $"Upload complete: {lists} list(s), {entries} entr(y/ies). CSV format: ProcessName or DisplayName,ProcessName[,Team][,ListName].";
        return RedirectToPage(new { section = "lists" });
    }

    public async Task<IActionResult> OnPostApplyAsync()
    {
        await LoadAsync();
        if (ApplyAppListId <= 0)
        {
            TempData["Error"] = "Pick an app list to apply.";
            return Page();
        }

        var scopes = BuildScopes();
        if (scopes.Count == 0)
        {
            TempData["Error"] = "Select Global and/or at least one region, office, or machine.";
            return Page();
        }

        await appLists.AssignAsync(ApplyAppListId, scopes, HttpContext.RequestAborted);
        TempData["Message"] = $"Applied app list to {scopes.Count} scope(s).";
        return RedirectToPage(new { section = "apply" });
    }

    public async Task<IActionResult> OnPostUnassignAsync(int assignmentId)
    {
        await appLists.UnassignAsync(assignmentId, HttpContext.RequestAborted);
        TempData["Message"] = "Assignment removed.";
        return RedirectToPage(new { section = "apply" });
    }

    public async Task<IActionResult> OnPostLookupAsync()
    {
        await LoadAsync();
        if (string.IsNullOrWhiteSpace(LookupHostname))
        {
            TempData["Error"] = "Pick a machine.";
            return Page();
        }
        FocusHostname = LookupHostname.Trim();
        await LoadFocusAsync(FocusHostname);
        return Page();
    }

    public async Task<IActionResult> OnPostAnalyzeAsync()
    {
        await LoadAsync();
        var host = (AnalyzeHostname ?? LookupHostname)?.Trim();
        if (string.IsNullOrWhiteSpace(host))
        {
            TempData["Error"] = "Pick a machine to analyze.";
            return Page();
        }

        Analysis = await appLists.AnalyzeMachineAsync(host, null, requestAgentInventoryIfEmpty: true, HttpContext.RequestAborted);
        FocusHostname = host;
        LookupHostname = host;
        await LoadFocusAsync(host);

        TempData["Message"] = Analysis.queuedForAgent
            ? $"Analysis queued for {host}. Agent will upload process inventory on next cycle; then approve here."
            : $"Analysis ready for {host}: {Analysis.Proposals.Count} app(s) pending approval — nothing new is tracked until you approve.";
        return Page();
    }

    public async Task<IActionResult> OnPostApproveAllAsync()
    {
        var host = (AnalyzeHostname ?? LookupHostname)?.Trim();
        if (string.IsNullOrWhiteSpace(host))
        {
            TempData["Error"] = "Missing hostname.";
            return RedirectToPage();
        }
        await appLists.ApproveAsync(host, null, HttpContext.RequestAborted);
        TempData["Message"] = $"Approved all proposed apps for {host}. Machine-scoped list now tracking.";
        return RedirectToPage(new { host });
    }

    public async Task<IActionResult> OnPostApproveSelectedAsync()
    {
        var host = (AnalyzeHostname ?? LookupHostname)?.Trim();
        if (string.IsNullOrWhiteSpace(host))
        {
            TempData["Error"] = "Missing hostname.";
            return RedirectToPage();
        }
        if (SelectedProcesses.Count == 0)
        {
            TempData["Error"] = "Check at least one app, or use Approve all.";
            await LoadAsync();
            FocusHostname = host;
            await LoadFocusAsync(host);
            return Page();
        }
        await appLists.ApproveAsync(host, SelectedProcesses, HttpContext.RequestAborted);
        TempData["Message"] = $"Approved {SelectedProcesses.Count} selected app(s) for {host}.";
        return RedirectToPage(new { host });
    }

    public async Task<IActionResult> OnPostApplyTeamAsync()
    {
        var host = (AnalyzeHostname ?? LookupHostname)?.Trim();
        if (string.IsNullOrWhiteSpace(host) || ApplyTeamListId is not int listId)
        {
            TempData["Error"] = "Pick a machine and a team app list.";
            return RedirectToPage();
        }
        await appLists.ApplyTeamListAsync(host, listId, HttpContext.RequestAborted);
        TempData["Message"] = $"Applied team app list to {host}. Only that list’s apps are tracked on this machine (auto-discovered proposals discarded).";
        return RedirectToPage(new { host });
    }

    public async Task<IActionResult> OnPostDismissAsync()
    {
        var host = (AnalyzeHostname ?? LookupHostname)?.Trim();
        if (string.IsNullOrWhiteSpace(host))
        {
            TempData["Error"] = "Missing hostname.";
            return RedirectToPage();
        }
        await appLists.DismissAnalysisAsync(host, HttpContext.RequestAborted);
        TempData["Message"] = $"Dismissed analysis for {host}. Discovered apps will not be tracked.";
        return RedirectToPage(new { host });
    }

    public async Task<IActionResult> OnGetEditAsync(int id)
    {
        await LoadAsync();
        var list = await db.AppLists.AsNoTracking().Include(a => a.Entries).FirstOrDefaultAsync(a => a.Id == id);
        if (list is null)
        {
            TempData["Error"] = "List not found.";
            return RedirectToPage();
        }
        EditId = list.Id;
        ListName = list.Name;
        TeamId = list.TeamId;
        Notes = list.Notes;
        ProcessesText = string.Join(Environment.NewLine,
            list.Entries.Select(e => string.IsNullOrWhiteSpace(e.DisplayName)
                ? e.ProcessName
                : $"{e.DisplayName},{e.ProcessName}"));
        return Page();
    }

    private async Task LoadFocusAsync(string hostname)
    {
        Lookup = await appLists.GetEffectiveForHostAsync(hostname, HttpContext.RequestAborted);
        TeamOptions = await appLists.GetTeamListsForHostAsync(hostname, HttpContext.RequestAborted);
        PendingProposals = Lookup.PendingProposals.ToList();
        FocusStatus = Lookup.AnalysisStatus;
        AnalyzeHostname = hostname;
    }

    private async Task LoadAsync()
    {
        Teams = await db.Teams.AsNoTracking().OrderBy(t => t.Name).ToListAsync();
        AllMachines = await db.Machines.AsNoTracking().OrderBy(m => m.Hostname).ToListAsync();
        foreach (var m in AllMachines)
            MachineHierarchy.EnsureDefaults(m);
        Tree = MachineHierarchy.BuildTree(AllMachines);

        Lists = (await db.AppLists.AsNoTracking()
            .Include(a => a.Team)
            .Include(a => a.Entries)
            .Include(a => a.Assignments)
            .ToListAsync())
            .OrderBy(a => a.Name, StringComparer.OrdinalIgnoreCase)
            .Select(a => new AppListRow(
                a.Id,
                a.Name,
                a.Team?.Name,
                a.Entries.Count,
                a.Assignments.Count(x => x.IsEnabled),
                a.IsAutoDiscovered,
                a.UpdatedUtc))
            .ToList();

        AuditLogs = (await db.AppListAuditLogs.AsNoTracking().ToListAsync())
            .OrderByDescending(a => a.Utc)
            .Take(100)
            .ToList();
    }

    private List<(ConfigScope Scope, string? ScopeValue)> BuildScopes()
    {
        var scopes = new List<(ConfigScope, string?)>();
        if (ApplyGlobal)
            scopes.Add((ConfigScope.All, null));

        foreach (var r in SelectedRegions.Where(s => !string.IsNullOrWhiteSpace(s)))
            scopes.Add((ConfigScope.Region, r.Trim()));

        foreach (var o in SelectedOffices.Where(s => !string.IsNullOrWhiteSpace(s)))
        {
            var region = o.Contains('/') ? o.Split('/')[0] : null;
            if (region is not null && SelectedRegions.Contains(region, StringComparer.OrdinalIgnoreCase))
                continue;
            scopes.Add((ConfigScope.Office, o.Trim()));
        }

        var covered = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var region in Tree)
        {
            var regionSelected = SelectedRegions.Contains(region.Name, StringComparer.OrdinalIgnoreCase);
            foreach (var office in region.Offices)
            {
                var officeKey = $"{region.Name}/{office.Name}";
                if (regionSelected || SelectedOffices.Contains(officeKey, StringComparer.OrdinalIgnoreCase))
                    foreach (var m in office.Machines)
                        covered.Add(m.Hostname);
            }
        }

        foreach (var host in SelectedMachines.Where(s => !string.IsNullOrWhiteSpace(s)))
        {
            if (covered.Contains(host)) continue;
            scopes.Add((ConfigScope.Machine, host.Trim()));
        }

        return scopes;
    }

    private static List<(string ProcessName, string? DisplayName)> ParseProcessesText(string text)
    {
        var result = new List<(string, string?)>();
        foreach (var raw in text.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (raw.Contains(','))
            {
                var parts = raw.Split(',', 2);
                var left = parts[0].Trim();
                var right = parts[1].Trim();
                // DisplayName,ProcessName or ProcessName only with trailing comma noise
                if (right.Length > 0)
                    result.Add((right, left));
                else
                    result.Add((left, null));
            }
            else
            {
                result.Add((raw, null));
            }
        }
        return result;
    }

    public record AppListRow(int Id, string Name, string? TeamName, int EntryCount, int AssignmentCount, bool IsAutoDiscovered, DateTimeOffset UpdatedUtc);
}
