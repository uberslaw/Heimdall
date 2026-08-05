using Heimdall.Api.Data;
using Heimdall.Api.Services;
using Heimdall.Shared.Contracts;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;

namespace Heimdall.Api.Pages;

public class AppListsModel(HeimdallDbContext db, AppListService appLists, ProcessGroupService processGroups, ProcessCatalogService catalog) : PageModel
{
    public List<AppListRow> Lists { get; private set; } = [];
    public List<Team> Teams { get; private set; } = [];
    public IReadOnlyList<MachineHierarchy.RegionNode> Tree { get; private set; } = [];
    public List<Machine> AllMachines { get; private set; } = [];
    public int CatalogTotalCount { get; private set; }
    public int CatalogUnclassifiedCount { get; private set; }
    public int CatalogDiscoverySourceCount { get; private set; }
    public int CatalogMissingFromDiscoveryCount { get; private set; }
    public int CatalogBlankPathCount { get; private set; }
    public bool ShowCatalogBackfill { get; private set; }

    public AppListService.MachineAppListsView? Lookup { get; private set; }
    public AppListService.AnalysisResult? Analysis { get; private set; }
    public IReadOnlyList<AppListService.TeamAppListOption> TeamOptions { get; private set; } = [];
    public List<AppListService.ProposedApp> PendingProposals { get; private set; } = [];
    public IReadOnlyList<AppListService.ClassifiedProcessRow> MachineInventory { get; private set; } = [];
    public bool HasAutoDiscoveredList { get; private set; }
    public int AutoDiscoveredEntryCount { get; private set; }
    public string? FocusHostname { get; private set; }
    public AppAnalysisStatus? FocusStatus { get; private set; }
    public bool FocusPendingInventory { get; private set; }

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
    [BindProperty] public List<string> SelectedGroupProcesses { get; set; } = [];
    [BindProperty] public int? ApplyTeamListId { get; set; }
    [BindProperty] public int? DefaultUploadTeamId { get; set; }
    [BindProperty] public IFormFile? UploadFile { get; set; }
    [BindProperty] public IFormFile? ClassificationCsvFile { get; set; }
    [BindProperty] public List<int> SelectedListIds { get; set; } = [];

    public async Task OnGetAsync(string? host = null, string? section = null)
    {
        // Discovery-gap scan walks ProcessRuns + inventories — only when the CSV/catalog panel needs it,
        // or on a bare landing (no host/section). Machine-focus and lists/apply redirects skip it.
        var scanGap = string.Equals(section, "csv-classifications", StringComparison.OrdinalIgnoreCase)
            || (string.IsNullOrWhiteSpace(host) && string.IsNullOrWhiteSpace(section));
        await LoadAsync(scanDiscoveryGap: scanGap);
        if (!string.IsNullOrWhiteSpace(host))
        {
            FocusHostname = host.Trim();
            LookupHostname = FocusHostname;
            try
            {
                await LoadFocusAsync(FocusHostname);
            }
            catch (Exception ex)
            {
                TempData["Error"] = $"Lookup failed for {FocusHostname}: {ex.Message}";
            }
        }
    }

    public async Task<IActionResult> OnPostSaveListAsync()
    {
        if (string.IsNullOrWhiteSpace(ListName))
        {
            TempData["Error"] = "List name is required.";
            await LoadAsync();
            return Page();
        }

        var entries = ParseProcessesText(ProcessesText);
        await appLists.CreateOrUpdateListAsync(EditId, ListName, TeamId, Notes, entries, HttpContext.RequestAborted);
        TempData["Message"] = EditId is null ? $"Created app list “{ListName}”." : $"Updated app list “{ListName}”.";
        return RedirectToPage(new { section = "lists" });
    }

    public async Task<IActionResult> OnPostUploadAsync()
    {
        if (UploadFile is null || UploadFile.Length == 0)
        {
            TempData["Error"] = "Choose a CSV or JSON file.";
            await LoadAsync();
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
        if (ApplyAppListId <= 0)
        {
            TempData["Error"] = "Pick an app list to apply.";
            await LoadAsync();
            return Page();
        }

        // Tree is needed to expand region/office selections into covered hosts.
        await LoadShellAsync();
        var scopes = BuildScopes();
        if (scopes.Count == 0)
        {
            TempData["Error"] = "Select Global and/or at least one region, office, or machine.";
            await LoadCatalogStatusAsync();
            await LoadListsAsync();
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

    public async Task<IActionResult> OnPostAnalyzeAsync()
    {
        // Shell + lists only — skip discovery-gap scan on this round-trip.
        await LoadAsync(scanDiscoveryGap: false);
        var host = (AnalyzeHostname ?? LookupHostname)?.Trim();
        if (string.IsNullOrWhiteSpace(host))
        {
            TempData["Error"] = "Pick a machine to analyze.";
            return Page();
        }

        FocusHostname = host;
        LookupHostname = host;
        try
        {
            Analysis = await appLists.AnalyzeMachineAsync(host, null, requestAgentInventoryIfEmpty: true, HttpContext.RequestAborted);
            await LoadFocusAsync(host);

            var catalogNote = Analysis.NewCatalogCount > 0
                ? $" {Analysis.NewCatalogCount} new process(es) added to the catalog and flagged for classification."
                : "";
            TempData["Message"] = (Analysis.queuedForAgent
                ? $"Analysis queued for {host}. Agent will upload process inventory on next cycle (~5 min config refresh + ~1 min upload); then approve here."
                : $"Analysis ready for {host}: {Analysis.Proposals.Count} app(s) pending approval — nothing new is tracked until you approve. If inventory looks incomplete, use Request full inventory.")
                + catalogNote;
        }
        catch (Exception ex)
        {
            TempData["Error"] = $"Analyze failed for {host}: {ex.Message}";
        }
        return Page();
    }

    public async Task<IActionResult> OnPostRequestInventoryAsync()
    {
        await LoadAsync(scanDiscoveryGap: false);
        var host = (AnalyzeHostname ?? LookupHostname)?.Trim();
        if (string.IsNullOrWhiteSpace(host))
        {
            TempData["Error"] = "Pick a machine.";
            return Page();
        }

        FocusHostname = host;
        LookupHostname = host;
        try
        {
            await appLists.RequestAgentInventoryAsync(host, HttpContext.RequestAborted);
            await LoadFocusAsync(host);
            TempData["Message"] =
                $"Full inventory requested for {host}. Agent picks this up on next config refresh (~5 min), then uploads (~1 min). Re-select {host} above (or reload the page) once the Pending inventory badge clears.";
        }
        catch (Exception ex)
        {
            TempData["Error"] = $"Inventory request failed for {host}: {ex.Message}";
        }
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
            await LoadAsync(scanDiscoveryGap: false);
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

    public async Task<IActionResult> OnPostMoveToCoreWindowsAsync()
        => await MoveSelectedGroupsAsync(AppGroup.CoreWindows);

    public async Task<IActionResult> OnPostMoveToSoeAsync()
        => await MoveSelectedGroupsAsync(AppGroup.Soe);

    public async Task<IActionResult> OnPostMoveToSpecializationAsync()
        => await MoveSelectedGroupsAsync(AppGroup.Specialization);

    public async Task<IActionResult> OnPostCleanupDiscoveredAsync()
    {
        var host = (AnalyzeHostname ?? LookupHostname)?.Trim();
        if (string.IsNullOrWhiteSpace(host))
        {
            TempData["Error"] = "Pick a machine first.";
            return RedirectToPage();
        }

        var (removed, remaining) = await processGroups.CleanupDiscoveredListAsync(host, HttpContext.RequestAborted);
        if (removed == 0)
            TempData["Message"] = remaining == 0
                ? $"No auto-discovered list found for {host}."
                : $"No Core Windows or SOE entries to remove from “Discovered on {host}” ({remaining} specialization app(s) remain).";
        else
            TempData["Message"] = $"Removed {removed} Core Windows / SOE entr(y/ies) from “Discovered on {host}”. {remaining} specialization app(s) remain tracked.";

        return RedirectToPage(new { host });
    }

    public async Task<IActionResult> OnGetExportMachineCsvAsync(string host)
    {
        if (string.IsNullOrWhiteSpace(host))
        {
            TempData["Error"] = "Pick a machine to export.";
            return RedirectToPage();
        }

        host = host.Trim();
        var rows = await processGroups.BuildMachineExportRowsAsync(host, HttpContext.RequestAborted);
        if (rows.Count == 0)
        {
            TempData["Error"] = $"No process inventory for {host}. Run Analyze or wait for agent data.";
            return RedirectToPage(new { host });
        }

        var bytes = ProcessGroupService.RenderCsv(rows);
        var safeHost = string.Concat(host.Where(ch => char.IsLetterOrDigit(ch) || ch is '-' or '_'));
        return File(bytes, "text/csv; charset=utf-8", $"heimdall-apps-{safeHost}.csv");
    }

    public async Task<IActionResult> OnGetExportAllCsvAsync()
    {
        var rows = await processGroups.BuildGlobalExportRowsAsync(HttpContext.RequestAborted);
        var bytes = ProcessGroupService.RenderCsv(rows);
        return File(bytes, "text/csv; charset=utf-8", "heimdall-classified-processes.csv");
    }

    public async Task<IActionResult> OnGetExportCatalogCsvAsync()
    {
        await catalog.BackfillFromDiscoveriesAsync(HttpContext.RequestAborted);
        var entries = await catalog.GetAllAsync(HttpContext.RequestAborted);
        var sb = new System.Text.StringBuilder();
        sb.AppendLine("ProcessName,ExecutablePath,DisplayName,FileVersion,ProductVersion,CompanyName,FileDescription,SeenCount,FirstSeenUtc,LastSeenUtc,FirstSeenHost,LastSeenHost,SeenOnHosts,Ignored,SuggestedGroup,SuggestionReason");
        foreach (var e in entries)
        {
            var seenHosts = string.Join("; ", ProcessCatalogService.GetSeenHostnames(e));
            sb.AppendLine(string.Join(",",
                CsvCell(e.ProcessName), CsvCell(e.ExecutablePath), CsvCell(e.DisplayName ?? ""),
                CsvCell(e.FileVersion ?? ""), CsvCell(e.ProductVersion ?? ""), CsvCell(e.CompanyName ?? ""), CsvCell(e.FileDescription ?? ""),
                e.SeenCount.ToString(), CsvCell(e.FirstSeenUtc.ToString("u")), CsvCell(e.LastSeenUtc.ToString("u")),
                CsvCell(e.FirstSeenHostname ?? ""), CsvCell(e.LastSeenHostname ?? ""), CsvCell(seenHosts),
                e.Ignored ? "yes" : "",
                CsvCell(e.SuggestedGroup?.ToString() ?? ""), CsvCell(e.SuggestionReason ?? "")));
        }
        return File(System.Text.Encoding.UTF8.GetBytes(sb.ToString()), "text/csv; charset=utf-8", "heimdall-process-catalog.csv");
    }

    public async Task<IActionResult> OnPostBackfillCatalogAsync()
    {
        var result = await catalog.BackfillFromDiscoveriesAsync(HttpContext.RequestAborted);
        TempData["Message"] = result.NewCount + result.UpdatedCount == 0
            ? "Catalog is already up to date with discovery sources."
            : result.NewCount == 0
                ? $"Catalog backfill complete: {result.UpdatedCount} existing entries refreshed (none newly added)."
                : $"Catalog backfill complete: {result.NewCount} newly added, {result.UpdatedCount} existing refreshed.";
        var host = (AnalyzeHostname ?? LookupHostname)?.Trim();
        return RedirectToPage(string.IsNullOrWhiteSpace(host) ? new { section = "csv-classifications" } : new { host, section = "csv-classifications" });
    }

    private static string CsvCell(string value)
    {
        if (string.IsNullOrEmpty(value)) return "";
        return value.Contains(',') || value.Contains('"') || value.Contains('\n')
            ? $"\"{value.Replace("\"", "\"\"")}\""
            : value;
    }

    /// <summary>Export checked rows from the machine inventory table. format: applist-csv | applist-json | classification-csv | classification-json.</summary>
    public async Task<IActionResult> OnPostExportSelectedInventoryAsync(string? format)
    {
        var host = (AnalyzeHostname ?? LookupHostname)?.Trim();
        if (string.IsNullOrWhiteSpace(host) || SelectedGroupProcesses.Count == 0)
        {
            TempData["Error"] = "Select at least one process in the inventory table to export.";
            return RedirectToPage(new { host });
        }

        var inventory = await appLists.GetMachineInventoryAsync(host, HttpContext.RequestAborted);
        var selected = inventory.Where(r => SelectedGroupProcesses.Contains(r.ProcessName, StringComparer.OrdinalIgnoreCase)).ToList();
        if (selected.Count == 0)
        {
            TempData["Error"] = "No matching processes found to export.";
            return RedirectToPage(new { host });
        }

        return await BuildSelectionExportAsync(
            selected.Select(r => (r.ProcessName, r.ExecutablePath, (string?)r.DisplayName)).ToList(),
            $"Exported from {host}", $"heimdall-inventory-{SafeFileToken(host)}", format);
    }

    /// <summary>Export checked rows from the pending-approval proposals table. format: applist-csv | applist-json | classification-csv | classification-json.</summary>
    public async Task<IActionResult> OnPostExportSelectedProposalsAsync(string? format)
    {
        var host = (AnalyzeHostname ?? LookupHostname)?.Trim();
        if (string.IsNullOrWhiteSpace(host) || SelectedProcesses.Count == 0)
        {
            TempData["Error"] = "Select at least one proposed app to export.";
            return RedirectToPage(new { host });
        }

        var lookup = await appLists.GetEffectiveForHostAsync(host, HttpContext.RequestAborted);
        var selected = lookup.PendingProposals.Where(p => SelectedProcesses.Contains(p.ProcessName, StringComparer.OrdinalIgnoreCase)).ToList();
        if (selected.Count == 0)
        {
            TempData["Error"] = "No matching proposals found to export.";
            return RedirectToPage(new { host });
        }

        return await BuildSelectionExportAsync(
            selected.Select(p => (p.ProcessName, p.ExecutablePath, (string?)p.DisplayName)).ToList(),
            $"Discovered on {host}", $"heimdall-proposals-{SafeFileToken(host)}", format);
    }

    /// <summary>Export checked rows from Existing lists. format: csv | json | classification-csv.</summary>
    public async Task<IActionResult> OnPostExportSelectedListsAsync(string? format)
    {
        if (SelectedListIds.Count == 0)
        {
            TempData["Error"] = "Select at least one app list to export.";
            return RedirectToPage(new { section = "lists" });
        }

        var lists = await db.AppLists.AsNoTracking().Include(l => l.Entries)
            .Where(l => SelectedListIds.Contains(l.Id))
            .ToListAsync(HttpContext.RequestAborted);
        if (lists.Count == 0)
        {
            TempData["Error"] = "Selected app lists not found.";
            return RedirectToPage(new { section = "lists" });
        }

        var rows = lists.SelectMany(l => l.Entries.Select(e => (ListName: l.Name, e.ProcessName, e.DisplayName))).ToList();
        if (string.Equals(format, "classification-csv", StringComparison.OrdinalIgnoreCase))
        {
            var exportRows = await processGroups.BuildExportRowsForAsync(
                rows.Select(r => (r.ProcessName, (string?)null, r.DisplayName)), HttpContext.RequestAborted);
            return File(ProcessGroupService.RenderCsv(exportRows), "text/csv; charset=utf-8", "heimdall-lists-classification.csv");
        }
        if (string.Equals(format, "json", StringComparison.OrdinalIgnoreCase))
            return File(AppListService.RenderUploadJson(rows), "application/json", "heimdall-lists-export.json");

        return File(AppListService.RenderUploadCsv(rows), "text/csv; charset=utf-8", "heimdall-lists-export.csv");
    }

    private async Task<IActionResult> BuildSelectionExportAsync(
        IReadOnlyList<(string ProcessName, string? ExecutablePath, string? DisplayName)> selected,
        string listName,
        string fileBaseName,
        string? format)
    {
        switch (format)
        {
            case "classification-json":
                return File(ProcessGroupService.RenderJson(await processGroups.BuildExportRowsForAsync(selected, HttpContext.RequestAborted)),
                    "application/json", $"{fileBaseName}.json");
            case "applist-json":
                return File(AppListService.RenderUploadJson(selected.Select(s => (s.ProcessName, s.DisplayName)), listName),
                    "application/json", $"{fileBaseName}-applist.json");
            case "applist-csv":
                return File(AppListService.RenderUploadCsv(selected.Select(s => (s.ProcessName, s.DisplayName)), listName),
                    "text/csv; charset=utf-8", $"{fileBaseName}-applist.csv");
            default:
                return File(ProcessGroupService.RenderCsv(await processGroups.BuildExportRowsForAsync(selected, HttpContext.RequestAborted)),
                    "text/csv; charset=utf-8", $"{fileBaseName}.csv");
        }
    }

    private static string SafeFileToken(string value) =>
        string.Concat(value.Where(ch => char.IsLetterOrDigit(ch) || ch is '-' or '_'));

    public async Task<IActionResult> OnPostImportClassificationsAsync()
    {
        if (ClassificationCsvFile is null || ClassificationCsvFile.Length == 0)
        {
            TempData["Error"] = "Choose a classification CSV file.";
            var host = (AnalyzeHostname ?? LookupHostname)?.Trim();
            return RedirectToPage(string.IsNullOrWhiteSpace(host) ? null : new { host });
        }

        const long maxBytes = 10 * 1024 * 1024;
        if (ClassificationCsvFile.Length > maxBytes)
        {
            TempData["Error"] = "CSV file is too large (max 10 MB). Split into smaller files.";
            var host = (AnalyzeHostname ?? LookupHostname)?.Trim();
            return RedirectToPage(string.IsNullOrWhiteSpace(host) ? null : new { host });
        }

        await using var stream = ClassificationCsvFile.OpenReadStream();
        ProcessGroupService.CsvImportResult result;
        try
        {
            result = await processGroups.ImportCsvAsync(stream, HttpContext.RequestAborted);
        }
        catch (Exception ex)
        {
            TempData["Error"] = $"Import failed: {ex.Message}";
            var host = (AnalyzeHostname ?? LookupHostname)?.Trim();
            return RedirectToPage(string.IsNullOrWhiteSpace(host) ? null : new { host });
        }

        var newCatalogCount = 0;
        var metaApplied = 0;
        if (result.ImportedRows is { Count: > 0 })
        {
            var catalogResult = await catalog.UpsertAsync(
                result.ImportedRows.Select(r => new ProcessCatalogService.CatalogItem(r.ProcessName, r.ExecutablePath, r.DisplayName)),
                null, "classification CSV import", HttpContext.RequestAborted);
            newCatalogCount = catalogResult.NewCount;
            metaApplied = await catalog.ApplyImportMetadataAsync(result.ImportedRows, HttpContext.RequestAborted);
        }

        var summary = $"Import complete: {result.Updated} suggestion row(s), {result.Skipped} skipped.";
        if (metaApplied > 0)
            summary += $" Applied Category/Subcategory/Group suggestions to {metaApplied} catalog entr(ies).";
        if (newCatalogCount > 0)
            summary += $" {newCatalogCount} new process(es) added to the catalog.";
        summary += " Review and Approve on Discovery & Classification.";
        if (result.Errors.Count > 0)
        {
            var preview = string.Join("; ", result.Errors.Take(5));
            if (result.Errors.Count > 5)
                preview += $" … and {result.Errors.Count - 5} more.";
            summary += $" {result.Errors.Count} issue(s): {preview}";
        }
        TempData["Message"] = summary;

        var redirectHost = (AnalyzeHostname ?? LookupHostname)?.Trim();
        return RedirectToPage(string.IsNullOrWhiteSpace(redirectHost) ? null : new { host = redirectHost });
    }

    private async Task<IActionResult> MoveSelectedGroupsAsync(AppGroup targetGroup)
    {
        if (SelectedGroupProcesses.Count == 0)
        {
            TempData["Error"] = "Select at least one process in the inventory table.";
            return RedirectToPage(new { host = FocusHostname ?? LookupHostname });
        }

        var count = await processGroups.AssignGroupsAsync(SelectedGroupProcesses, targetGroup, HttpContext.RequestAborted);
        await catalog.ClearSuggestionsAsync(SelectedGroupProcesses, HttpContext.RequestAborted);
        var label = ProcessClassification.GroupLabel(targetGroup);
        TempData["Message"] = $"Moved {count} process(es) to {label}. Re-run Analyze to refresh pending proposals.";

        var host = (AnalyzeHostname ?? LookupHostname)?.Trim();
        return RedirectToPage(string.IsNullOrWhiteSpace(host) ? null : new { host });
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
        MachineInventory = await appLists.GetMachineInventoryAsync(hostname, HttpContext.RequestAborted);
        FocusStatus = Lookup.AnalysisStatus;
        AnalyzeHostname = hostname;

        var machine = await db.Machines.AsNoTracking().FirstOrDefaultAsync(m => m.Hostname == hostname, HttpContext.RequestAborted);
        FocusPendingInventory = machine?.PendingAppAnalysis == true;

        var discoveredList = await db.AppLists.AsNoTracking()
            .Include(a => a.Entries)
            .FirstOrDefaultAsync(a => a.Name == $"Discovered on {hostname}" && a.IsAutoDiscovered, HttpContext.RequestAborted);
        HasAutoDiscoveredList = discoveredList is not null;
        AutoDiscoveredEntryCount = discoveredList?.Entries.Count ?? 0;
    }

    private async Task LoadAsync(bool scanDiscoveryGap = true)
    {
        await LoadShellAsync();
        await LoadListsAsync();
        await LoadCatalogStatusAsync(scanDiscoveryGap);
    }

    /// <summary>Teams, machines, and hierarchy tree — needed for apply scopes and machine lookup.</summary>
    private async Task LoadShellAsync()
    {
        Teams = await db.Teams.AsNoTracking().OrderBy(t => t.Name).ToListAsync(HttpContext.RequestAborted);
        AllMachines = await db.Machines.AsNoTracking().OrderBy(m => m.Hostname).ToListAsync(HttpContext.RequestAborted);
        foreach (var m in AllMachines)
            MachineHierarchy.EnsureDefaults(m);
        Tree = MachineHierarchy.BuildTree(AllMachines);
    }

    private async Task LoadListsAsync()
    {
        Lists = (await db.AppLists.AsNoTracking()
            .Include(a => a.Team)
            .Include(a => a.Entries)
            .Include(a => a.Assignments)
            .ToListAsync(HttpContext.RequestAborted))
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
    }

    /// <summary>
    /// Catalog banner stats. Never auto-backfill — that re-ran full discovery Upsert on every visit
    /// (same class of bug as Discovery Approve rebuilding the catalog after each micro-action).
    /// User clicks Backfill Discovered explicitly.
    /// </summary>
    private async Task LoadCatalogStatusAsync(bool scanDiscoveryGap = true)
    {
        if (scanDiscoveryGap)
        {
            var catalogStatus = await catalog.GetCatalogStatusAsync(HttpContext.RequestAborted);
            CatalogTotalCount = catalogStatus.TotalCount;
            CatalogUnclassifiedCount = catalogStatus.UnclassifiedCount;
            CatalogDiscoverySourceCount = catalogStatus.DiscoverySourceCount;
            CatalogMissingFromDiscoveryCount = catalogStatus.MissingFromCatalog;
            CatalogBlankPathCount = catalogStatus.BlankPathCount;
            ShowCatalogBackfill = catalogStatus.ShowBackfill;
            return;
        }

        CatalogTotalCount = await catalog.CountAsync(HttpContext.RequestAborted);
        CatalogBlankPathCount = await catalog.CountBlankPathAsync(HttpContext.RequestAborted);
        CatalogUnclassifiedCount = 0;
        CatalogDiscoverySourceCount = 0;
        CatalogMissingFromDiscoveryCount = 0;
        ShowCatalogBackfill = false;
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

    public static string GroupLabel(AppGroup group) => ProcessClassification.GroupLabel(group);

    public static string StatusLabel(AppListService.InventoryStatus status) => status switch
    {
        AppListService.InventoryStatus.Tracked => "Tracked",
        AppListService.InventoryStatus.Proposed => "Proposed",
        AppListService.InventoryStatus.Available => "Available",
        _ => "Excluded"
    };
}
