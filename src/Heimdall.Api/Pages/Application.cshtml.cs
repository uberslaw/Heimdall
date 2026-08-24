using Heimdall.Api.Data;
using Heimdall.Api.Services;
using Heimdall.Shared.Contracts;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;

namespace Heimdall.Api.Pages;

public class ApplicationModel(
    StatsQueryService stats,
    HeimdallDbContext db,
    ProcessGroupService processGroups,
    AppListService appLists) : PageModel
{
    [BindProperty(SupportsGet = true)]
    public string? Process { get; set; }

    [BindProperty(SupportsGet = true)]
    public string Range { get; set; } = "7d";

    [BindProperty(SupportsGet = true)]
    public string? Hostname { get; set; }

    [BindProperty]
    public string? EditDescription { get; set; }

    [BindProperty]
    public string? EditCategory { get; set; }

    [BindProperty]
    public string? EditSubcategory { get; set; }

    [BindProperty]
    public string? EditGroup { get; set; }

    [BindProperty]
    public int AddAppListId { get; set; }

    [BindProperty]
    public int RemoveAppListId { get; set; }

    public ApplicationDetailSnapshot? Detail { get; private set; }
    public bool MissingProcess { get; private set; }
    public string RangeLabel { get; private set; } = "7 day";
    public int RangeDays { get; private set; } = 7;

    public IReadOnlyList<AppListService.AppListMembershipRow> ListMembership { get; private set; } = [];
    public IReadOnlyList<AppListService.AppListPickerRow> EditableLists { get; private set; } = [];
    public IReadOnlyList<string> CategoryOptions { get; private set; } = [];
    public IReadOnlyList<string> SubcategoryOptions { get; private set; } = [];
    public string GoogleSearchUrl { get; private set; } = "";

    public async Task OnGetAsync(CancellationToken ct)
    {
        await LoadAsync(ct);
    }

    public async Task<IActionResult> OnPostSaveIdentityAsync(CancellationToken ct)
    {
        var process = RequireProcess();
        if (process is null)
            return RedirectToPage(new { process = Process, range = Range, hostname = Hostname });

        var description = string.IsNullOrWhiteSpace(EditDescription) ? null : EditDescription.Trim();
        var category = string.IsNullOrWhiteSpace(EditCategory) ? null : EditCategory.Trim();
        var subcategory = string.IsNullOrWhiteSpace(EditSubcategory) ? null : EditSubcategory.Trim();

        var entries = await db.ProcessCatalogEntries
            .Where(e => e.ProcessName == process)
            .ToListAsync(ct);

        if (entries.Count == 0)
        {
            db.ProcessCatalogEntries.Add(new ProcessCatalogEntry
            {
                ProcessName = process,
                ExecutablePath = "",
                DisplayName = process,
                Description = description,
                Category = category,
                Subcategory = subcategory,
                FirstSeenUtc = DateTimeOffset.UtcNow,
                LastSeenUtc = DateTimeOffset.UtcNow,
                SeenCount = 1
            });
        }
        else
        {
            foreach (var e in entries)
            {
                e.Description = description;
                e.Category = category;
                e.Subcategory = subcategory;
            }
        }

        var assignment = await db.ProcessGroupAssignments
            .FirstOrDefaultAsync(a => a.ProcessName == process, ct);
        if (assignment is not null)
        {
            assignment.Description = description;
            assignment.UpdatedUtc = DateTimeOffset.UtcNow;
        }

        await db.SaveChangesAsync(ct);
        TempData["Message"] = "Saved description and category labels.";
        return RedirectToPage(new { process, range = Range, hostname = Hostname });
    }

    public async Task<IActionResult> OnPostSetClassificationAsync(CancellationToken ct)
    {
        var process = RequireProcess();
        if (process is null)
            return RedirectToPage(new { process = Process, range = Range, hostname = Hostname });

        if (!TryParseGroup(EditGroup, out var group))
        {
            TempData["Error"] = "Pick a classification (Core Windows, SOE, or Specialization).";
            return RedirectToPage(new { process, range = Range, hostname = Hostname });
        }

        await processGroups.AssignGroupsAsync([process], group, ct);
        await appLists.SyncSystemListsFromClassificationsAsync(ct);
        TempData["Message"] = $"Classification set to {ProcessClassification.GroupLabel(group)}.";
        return RedirectToPage(new { process, range = Range, hostname = Hostname });
    }

    public async Task<IActionResult> OnPostAddToListAsync(CancellationToken ct)
    {
        var process = RequireProcess();
        if (process is null)
            return RedirectToPage(new { process = Process, range = Range, hostname = Hostname });

        if (AddAppListId <= 0)
        {
            TempData["Error"] = "Pick an app list to add this process to.";
            return RedirectToPage(new { process, range = Range, hostname = Hostname });
        }

        try
        {
            var display = Detail?.DisplayName;
            // Detail may be null before Load; resolve display name cheaply.
            if (string.IsNullOrWhiteSpace(display))
            {
                display = (await db.ProcessCatalogEntries.AsNoTracking()
                        .Where(e => e.ProcessName == process && e.DisplayName != null)
                        .Select(e => new { e.DisplayName, e.LastSeenUtc })
                        .ToListAsync(ct))
                    .OrderByDescending(e => e.LastSeenUtc)
                    .Select(e => e.DisplayName)
                    .FirstOrDefault();
            }

            var added = await appLists.AddEntriesToListAsync(
                AddAppListId,
                [(process, display)],
                assignToHostname: null,
                ct);
            TempData["Message"] = added > 0
                ? "Added to app list."
                : "Already on that app list.";
        }
        catch (Exception ex)
        {
            TempData["Error"] = ex.Message;
        }

        return RedirectToPage(new { process, range = Range, hostname = Hostname });
    }

    public async Task<IActionResult> OnPostRemoveFromListAsync(CancellationToken ct)
    {
        var process = RequireProcess();
        if (process is null)
            return RedirectToPage(new { process = Process, range = Range, hostname = Hostname });

        if (RemoveAppListId <= 0)
        {
            TempData["Error"] = "Missing app list.";
            return RedirectToPage(new { process, range = Range, hostname = Hostname });
        }

        try
        {
            var removed = await appLists.RemoveEntriesFromListAsync(RemoveAppListId, [process], ct);
            TempData["Message"] = removed > 0
                ? "Removed from app list."
                : "Process was not on that list.";
        }
        catch (Exception ex)
        {
            TempData["Error"] = ex.Message;
        }

        return RedirectToPage(new { process, range = Range, hostname = Hostname });
    }

    private string? RequireProcess()
    {
        if (string.IsNullOrWhiteSpace(Process))
        {
            MissingProcess = true;
            return null;
        }

        return ConfigService.NormalizeProcessName(Process);
    }

    private async Task LoadAsync(CancellationToken ct)
    {
        var (key, label, days) = IndexModel.ResolveRange(Range);
        Range = key;
        RangeLabel = label;
        RangeDays = days;

        if (string.IsNullOrWhiteSpace(Process))
        {
            MissingProcess = true;
            return;
        }

        var process = ConfigService.NormalizeProcessName(Process);
        Process = process;
        var (fromUtc, toUtc) = IndexModel.ResolveRangeWindow(Range);

        Detail = await stats.QueryApplicationDetailAsync(
            process,
            fromUtc,
            toUtc,
            string.IsNullOrWhiteSpace(Hostname) ? null : Hostname.Trim(),
            ct);

        ListMembership = await appLists.GetMembershipForProcessAsync(process, ct);
        EditableLists = await appLists.GetEditableAppListsAsync(ct);

        var catalogCats = await db.ProcessCatalogEntries.AsNoTracking()
            .Where(e => e.Category != null || e.Subcategory != null)
            .Select(e => new { e.Category, e.Subcategory })
            .Take(5000)
            .ToListAsync(ct);
        CategoryOptions = MergeOptions(catalogCats.Select(c => c.Category), DefaultCategoryTaxonomy);
        SubcategoryOptions = MergeOptions(catalogCats.Select(c => c.Subcategory), DefaultSubcategoryTaxonomy);

        GoogleSearchUrl = BuildGoogleSearchUrl(Detail);
    }

    private static string BuildGoogleSearchUrl(ApplicationDetailSnapshot? d)
    {
        if (d is null) return "https://www.google.com/";
        var parts = new List<string>();
        if (!string.IsNullOrWhiteSpace(d.DisplayName)
            && !string.Equals(d.DisplayName, d.ProcessName, StringComparison.OrdinalIgnoreCase))
            parts.Add(d.DisplayName);
        parts.Add(d.ProcessName);
        if (!string.IsNullOrWhiteSpace(d.CompanyName))
            parts.Add(d.CompanyName!);
        if (!string.IsNullOrWhiteSpace(d.FileDescription)
            && !parts.Any(p => string.Equals(p, d.FileDescription, StringComparison.OrdinalIgnoreCase)))
            parts.Add(d.FileDescription!);
        parts.Add("software");
        var q = string.Join(' ', parts.Where(p => !string.IsNullOrWhiteSpace(p)).Distinct(StringComparer.OrdinalIgnoreCase));
        return "https://www.google.com/search?q=" + Uri.EscapeDataString(q);
    }

    private static bool TryParseGroup(string? raw, out AppGroup group)
    {
        group = AppGroup.Specialization;
        if (string.IsNullOrWhiteSpace(raw))
            return false;
        return Enum.TryParse(raw.Trim(), ignoreCase: true, out group);
    }

    private static IReadOnlyList<string> MergeOptions(IEnumerable<string?> fromCatalog, IEnumerable<string> seed) =>
        fromCatalog
            .Concat(seed)
            .Where(c => !string.IsNullOrWhiteSpace(c))
            .Select(c => c!.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(c => c, StringComparer.OrdinalIgnoreCase)
            .ToList();

    private static readonly string[] DefaultCategoryTaxonomy =
    [
        "CAD", "Core Windows", "Hardware & Drivers", "Productivity", "SOE", "Security", "Unclassified"
    ];

    private static readonly string[] DefaultSubcategoryTaxonomy =
    [
        "AI Assistant", "Admin Tool", "Antivirus/EDR", "Background Service", "Browser Component",
        "CAD Application", "Cloud Sync Client", "Collaboration/Video Conferencing", "IDE",
        "Installer/Updater", "License Manager", "Messaging App", "Office Suite Application",
        "Remote Access Client", "Text Editor", "Unclassified", "Utility App", "Web Browser"
    ];
}
