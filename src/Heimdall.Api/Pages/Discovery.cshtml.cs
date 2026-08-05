using System.Text.RegularExpressions;
using Heimdall.Api.Data;
using Heimdall.Api.Services;
using Heimdall.Shared.Contracts;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;

namespace Heimdall.Api.Pages;

/// <summary>
/// Discovery &amp; Classification: processes awaiting group approval (Core Windows / SOE / Specialization),
/// including pending import/AI suggestions. Friendly name is editable; path is read-only.
/// </summary>
public class DiscoveryModel(HeimdallDbContext db, ProcessCatalogService catalog, ProcessGroupService processGroups, AppListService appLists) : PageModel
{
    private static readonly Regex VersionPattern = new(@"\d+(\.\d+){1,3}", RegexOptions.Compiled);

    public List<DiscoveryRow> Rows { get; private set; } = [];
    public IReadOnlyList<string> CategoryOptions { get; private set; } = [];
    public IReadOnlyList<string> SubcategoryOptions { get; private set; } = [];
    public int TotalCount { get; private set; }
    public int UnclassifiedCount { get; private set; }
    public int PendingSuggestionCount { get; private set; }
    public int IgnoredCount { get; private set; }
    public int BlankPathCount { get; private set; }

    [BindProperty(SupportsGet = true)]
    public bool HideIgnored { get; set; } = true;

    [BindProperty(SupportsGet = true)]
    public bool BlankPathOnly { get; set; }

    /// <summary>When false (default), only unclassified rows and pending suggestions are listed.</summary>
    [BindProperty(SupportsGet = true)]
    public bool ShowClassified { get; set; }

    [BindProperty(SupportsGet = true)]
    public string? Q { get; set; }

    [BindProperty(SupportsGet = true)]
    public string Sort { get; set; } = "name";

    [BindProperty(SupportsGet = true)]
    public string Dir { get; set; } = "asc";

    public async Task OnGetAsync()
    {
        await LoadAsync();
    }

    public async Task<IActionResult> OnPostApproveAsync(int id)
    {
        var entry = await db.ProcessCatalogEntries.FindAsync([id], HttpContext.RequestAborted);
        if (entry is null)
        {
            TempData["Error"] = "Process not found — it may have been removed.";
            return RedirectToFilters();
        }

        if (entry.SuggestedGroup is null)
        {
            TempData["Error"] = "No pending suggestion to approve — use Set instead.";
            return RedirectToFilters();
        }

        var group = entry.SuggestedGroup.Value;
        await processGroups.AssignGroupsAsync([entry.ProcessName], group, HttpContext.RequestAborted);
        await catalog.ClearSuggestionsAsync([entry.ProcessName], HttpContext.RequestAborted);

        TempData["Message"] = $"Approved {entry.ProcessName} as {ProcessClassification.GroupLabel(group)}.";
        return RedirectToFilters();
    }

    public async Task<IActionResult> OnPostSetAsync(int id, string group, string? category, string? subcategory)
    {
        var entry = await db.ProcessCatalogEntries.FindAsync([id], HttpContext.RequestAborted);
        if (entry is null)
        {
            TempData["Error"] = "Process not found — it may have been removed.";
            return RedirectToFilters();
        }

        if (!ProcessGroupService.TryParseGroup(group, out var targetGroup))
        {
            TempData["Error"] = "Choose Core Windows, SOE, or Specialization.";
            return RedirectToFilters();
        }

        await processGroups.AssignGroupsAsync([entry.ProcessName], targetGroup, HttpContext.RequestAborted);

        // Re-load after AssignGroups (may have saved); update category fields on all name matches.
        // Empty dropdown ("—") clears Category/Subcategory.
        var cat = string.IsNullOrWhiteSpace(category) ? null : category.Trim();
        var sub = string.IsNullOrWhiteSpace(subcategory) ? null : subcategory.Trim();
        var entries = await db.ProcessCatalogEntries
            .Where(e => e.ProcessName == entry.ProcessName)
            .ToListAsync(HttpContext.RequestAborted);
        foreach (var e in entries)
        {
            e.Category = cat;
            e.Subcategory = sub;
            e.SuggestedGroup = null;
            e.SuggestionReason = null;
        }
        await db.SaveChangesAsync(HttpContext.RequestAborted);

        TempData["Message"] = $"Set {entry.ProcessName} to {ProcessClassification.GroupLabel(targetGroup)}.";
        return RedirectToFilters();
    }

    public async Task<IActionResult> OnPostSaveNameAsync(int id, string? name)
    {
        var entry = await db.ProcessCatalogEntries.FindAsync([id], HttpContext.RequestAborted);
        if (entry is null)
            return new JsonResult(new { ok = false, error = "Not found" }) { StatusCode = 404 };

        entry.DisplayName = string.IsNullOrWhiteSpace(name) ? null : name.Trim();
        await db.SaveChangesAsync(HttpContext.RequestAborted);

        var display = string.IsNullOrWhiteSpace(entry.DisplayName) ? entry.ProcessName : entry.DisplayName!;
        if (string.Equals(Request.Headers.Accept.ToString(), "application/json", StringComparison.OrdinalIgnoreCase)
            || string.Equals(Request.Query["ajax"], "1", StringComparison.OrdinalIgnoreCase)
            || Request.Headers.XRequestedWith == "XMLHttpRequest")
        {
            return new JsonResult(new { ok = true, name = display });
        }

        TempData["Message"] = $"Renamed to {display}.";
        return RedirectToFilters();
    }

    public async Task<IActionResult> OnPostSaveAllAsync()
    {
        var edits = ParseEditsFromForm();
        if (edits.Count == 0)
        {
            TempData["Error"] = "Nothing to save.";
            return RedirectToFilters();
        }

        var ids = edits.Select(e => e.Id).ToList();
        var entries = await db.ProcessCatalogEntries.Where(e => ids.Contains(e.Id)).ToListAsync(HttpContext.RequestAborted);
        var entryMap = entries.ToDictionary(e => e.Id);
        var saved = 0;

        foreach (var edit in edits)
        {
            if (!entryMap.TryGetValue(edit.Id, out var entry))
                continue;

            entry.DisplayName = string.IsNullOrWhiteSpace(edit.Name) ? null : edit.Name.Trim();
            entry.Description = string.IsNullOrWhiteSpace(edit.Description) ? null : edit.Description.Trim();
            entry.Category = string.IsNullOrWhiteSpace(edit.Category) ? null : edit.Category.Trim();
            entry.Subcategory = string.IsNullOrWhiteSpace(edit.Subcategory) ? null : edit.Subcategory.Trim();
            entry.ManualVersion = string.IsNullOrWhiteSpace(edit.Version) ? null : edit.Version.Trim();
            saved++;
        }

        if (saved > 0)
            await db.SaveChangesAsync(HttpContext.RequestAborted);

        TempData["Message"] = saved == 1 ? "Saved 1 process." : $"Saved {saved} processes.";
        return RedirectToFilters();
    }

    public async Task<IActionResult> OnPostUnignoreAsync(int id)
    {
        var entry = await db.ProcessCatalogEntries.FindAsync([id], HttpContext.RequestAborted);
        if (entry is null)
        {
            TempData["Error"] = "Process not found — it may have been removed.";
            return RedirectToFilters();
        }

        entry.Ignored = false;
        await db.SaveChangesAsync(HttpContext.RequestAborted);
        TempData["Message"] = $"Restored {entry.ProcessName}.";
        return RedirectToFilters();
    }

    /// <summary>
    /// Search ProcessRuns / inventories / sibling catalog rows for blank ExecutablePath entries.
    /// Optionally request agent inventory for hosts that still lack a path.
    /// </summary>
    public async Task<IActionResult> OnPostFindPathsAsync(bool requestInventory = true)
    {
        var result = await catalog.ResolveMissingPathsAsync(ct: HttpContext.RequestAborted);
        var inventoryRequested = 0;
        if (requestInventory && result.HostsNeedingInventory.Count > 0)
            inventoryRequested = await RequestInventoriesAsync(result.HostsNeedingInventory, max: 25);

        TempData["Message"] = FormatPathResolveMessage(result, inventoryRequested);
        if (result.Considered > 0 && result.Filled + result.Merged == 0 && inventoryRequested == 0)
        {
            TempData["Message"] = null;
            TempData["Error"] = result.Ambiguous > 0
                ? $"{result.Ambiguous} process(es) have multiple reported paths — leave blank or use Request Inventory on App Lists for a specific machine."
                : "No agent-reported paths found yet. Request Inventory on App Lists for machines that run these processes.";
        }

        return RedirectToFilters();
    }

    /// <summary>Resolve path for a single blank catalog row.</summary>
    public async Task<IActionResult> OnPostFindPathAsync(int id, bool requestInventory = true)
    {
        var entry = await db.ProcessCatalogEntries.AsNoTracking()
            .FirstOrDefaultAsync(e => e.Id == id, HttpContext.RequestAborted);
        if (entry is null)
        {
            TempData["Error"] = "Process not found — it may have been removed.";
            return RedirectToFilters();
        }

        if (!string.IsNullOrWhiteSpace(entry.ExecutablePath))
        {
            TempData["Message"] = $"{entry.ProcessName} already has a path.";
            return RedirectToFilters();
        }

        var result = await catalog.ResolveMissingPathsAsync([id], HttpContext.RequestAborted);
        var inventoryRequested = 0;
        if (requestInventory && result.HostsNeedingInventory.Count > 0)
            inventoryRequested = await RequestInventoriesAsync(result.HostsNeedingInventory, max: 5);

        if (result.Filled + result.Merged == 0 && inventoryRequested == 0)
        {
            TempData["Error"] = result.Ambiguous > 0
                ? $"{entry.ProcessName} was reported at multiple paths — cannot pick one automatically."
                : $"No path found for {entry.ProcessName} in process samples or inventories yet.";
        }
        else
        {
            TempData["Message"] = FormatPathResolveMessage(result, inventoryRequested, entry.ProcessName);
        }

        return RedirectToFilters();
    }

    private async Task<int> RequestInventoriesAsync(IReadOnlyList<string> hosts, int max)
    {
        var requested = 0;
        foreach (var host in hosts.Take(max))
        {
            try
            {
                await appLists.RequestAgentInventoryAsync(host, HttpContext.RequestAborted);
                requested++;
            }
            catch
            {
                // Machine may have been removed; skip.
            }
        }
        return requested;
    }

    private static string FormatPathResolveMessage(
        ProcessCatalogService.MissingPathResolveResult result,
        int inventoryRequested,
        string? singleName = null)
    {
        var parts = new List<string>();
        if (result.Filled > 0)
            parts.Add(result.Filled == 1 ? "filled 1 path" : $"filled {result.Filled} paths");
        if (result.Merged > 0)
            parts.Add(result.Merged == 1 ? "merged 1 duplicate" : $"merged {result.Merged} duplicates");
        if (result.Ambiguous > 0)
            parts.Add($"{result.Ambiguous} ambiguous");
        if (result.Unresolved > 0 && result.Filled + result.Merged > 0)
            parts.Add($"{result.Unresolved} still missing");
        if (inventoryRequested > 0)
            parts.Add($"requested inventory on {inventoryRequested} machine(s)");

        if (parts.Count == 0)
        {
            return singleName is null
                ? "No missing paths could be resolved from existing agent data."
                : $"No path found for {singleName} from existing agent data.";
        }

        var prefix = singleName is null ? "Path search" : $"Path search for {singleName}";
        return $"{prefix}: {string.Join(", ", parts)}.";
    }

    private IActionResult RedirectToFilters() =>
        RedirectToPage(new
        {
            hideIgnored = HideIgnored,
            blankPathOnly = BlankPathOnly,
            showClassified = ShowClassified,
            q = Q,
            sort = Sort,
            dir = Dir
        });

    private List<DiscoveryEditInput> ParseEditsFromForm()
    {
        var edits = new List<DiscoveryEditInput>();
        var index = 0;
        while (Request.Form.TryGetValue($"Edits[{index}].Id", out var idVal) && int.TryParse(idVal, out var id))
        {
            edits.Add(new DiscoveryEditInput
            {
                Id = id,
                Name = Request.Form[$"Edits[{index}].Name"],
                Description = Request.Form[$"Edits[{index}].Description"],
                Category = Request.Form[$"Edits[{index}].Category"],
                Subcategory = Request.Form[$"Edits[{index}].Subcategory"],
                Version = Request.Form[$"Edits[{index}].Version"]
            });
            index++;
        }
        return edits;
    }

    private async Task LoadAsync()
    {
        await catalog.BackfillFromDiscoveriesAsync(HttpContext.RequestAborted);

        var entries = await catalog.GetAllAsync(HttpContext.RequestAborted);
        TotalCount = entries.Count;
        BlankPathCount = entries.Count(e => string.IsNullOrWhiteSpace(e.ExecutablePath));
        IgnoredCount = entries.Count(e => e.Ignored);
        UnclassifiedCount = await catalog.CountNeedingClassificationAsync(HttpContext.RequestAborted);
        PendingSuggestionCount = entries.Count(e => !e.Ignored && e.SuggestedGroup is not null);

        CategoryOptions = MergeOptionLists(
            entries.Select(e => e.Category),
            DefaultCategoryTaxonomy);
        SubcategoryOptions = MergeOptionLists(
            entries.Select(e => e.Subcategory),
            DefaultSubcategoryTaxonomy);

        var ctx = await processGroups.BuildContextAsync(HttpContext.RequestAborted);

        if (HideIgnored)
            entries = entries.Where(e => !e.Ignored).ToList();
        if (BlankPathOnly)
            entries = entries.Where(e => string.IsNullOrWhiteSpace(e.ExecutablePath)).ToList();

        if (!ShowClassified)
        {
            entries = entries.Where(e =>
            {
                var needs = !e.Ignored && ProcessCatalogService.NeedsClassification(e.ProcessName, ctx);
                var pending = e.SuggestedGroup is not null;
                return needs || pending;
            }).ToList();
        }

        if (!string.IsNullOrWhiteSpace(Q))
        {
            var q = Q.Trim();
            entries = entries.Where(e =>
                (e.DisplayName?.Contains(q, StringComparison.OrdinalIgnoreCase) ?? false)
                || e.ProcessName.Contains(q, StringComparison.OrdinalIgnoreCase)
                || e.ExecutablePath.Contains(q, StringComparison.OrdinalIgnoreCase)
                || (e.Category?.Contains(q, StringComparison.OrdinalIgnoreCase) ?? false)
                || (e.Subcategory?.Contains(q, StringComparison.OrdinalIgnoreCase) ?? false)
                || (e.Description?.Contains(q, StringComparison.OrdinalIgnoreCase) ?? false)
            ).ToList();
        }

        var rows = entries.Select(e =>
        {
            var (versionDisplay, versionSource) = ResolveVersion(e);
            var needs = !e.Ignored && ProcessCatalogService.NeedsClassification(e.ProcessName, ctx);
            var displayName = string.IsNullOrWhiteSpace(e.DisplayName) ? e.ProcessName : e.DisplayName!;

            return new DiscoveryRow(
                e.Id,
                displayName,
                e.ProcessName,
                e.ExecutablePath,
                e.Description,
                e.Category,
                e.Subcategory,
                versionDisplay,
                versionSource,
                e.ManualVersion,
                ProcessCatalogService.GetSeenHostnames(e),
                e.Ignored,
                needs,
                e.SuggestedGroup,
                e.SuggestionReason);
        });

        var asc = !string.Equals(Dir, "desc", StringComparison.OrdinalIgnoreCase);
        var sortKey = (Sort ?? "name").Trim().ToLowerInvariant();
        Rows = (sortKey switch
            {
                "path" => asc
                    ? rows.OrderBy(r => r.Path, StringComparer.OrdinalIgnoreCase).ThenBy(r => r.Name, StringComparer.OrdinalIgnoreCase)
                    : rows.OrderByDescending(r => r.Path, StringComparer.OrdinalIgnoreCase).ThenBy(r => r.Name, StringComparer.OrdinalIgnoreCase),
                "version" => asc
                    ? rows.OrderBy(r => r.VersionDisplay ?? "", StringComparer.OrdinalIgnoreCase).ThenBy(r => r.Name, StringComparer.OrdinalIgnoreCase)
                    : rows.OrderByDescending(r => r.VersionDisplay ?? "", StringComparer.OrdinalIgnoreCase).ThenBy(r => r.Name, StringComparer.OrdinalIgnoreCase),
                "category" => asc
                    ? rows.OrderBy(r => r.Category ?? "", StringComparer.OrdinalIgnoreCase).ThenBy(r => r.Name, StringComparer.OrdinalIgnoreCase)
                    : rows.OrderByDescending(r => r.Category ?? "", StringComparer.OrdinalIgnoreCase).ThenBy(r => r.Name, StringComparer.OrdinalIgnoreCase),
                "subcategory" => asc
                    ? rows.OrderBy(r => r.Subcategory ?? "", StringComparer.OrdinalIgnoreCase).ThenBy(r => r.Name, StringComparer.OrdinalIgnoreCase)
                    : rows.OrderByDescending(r => r.Subcategory ?? "", StringComparer.OrdinalIgnoreCase).ThenBy(r => r.Name, StringComparer.OrdinalIgnoreCase),
                "classification" => asc
                    ? rows.OrderBy(r => r.SuggestedGroup?.ToString() ?? "").ThenBy(r => r.Name, StringComparer.OrdinalIgnoreCase)
                    : rows.OrderByDescending(r => r.SuggestedGroup?.ToString() ?? "").ThenBy(r => r.Name, StringComparer.OrdinalIgnoreCase),
                "installs" => asc
                    ? rows.OrderBy(r => r.SeenHosts.Count).ThenBy(r => r.Name, StringComparer.OrdinalIgnoreCase)
                    : rows.OrderByDescending(r => r.SeenHosts.Count).ThenBy(r => r.Name, StringComparer.OrdinalIgnoreCase),
                _ => asc
                    ? rows.OrderBy(r => r.Name, StringComparer.OrdinalIgnoreCase).ThenBy(r => r.Path, StringComparer.OrdinalIgnoreCase)
                    : rows.OrderByDescending(r => r.Name, StringComparer.OrdinalIgnoreCase).ThenBy(r => r.Path, StringComparer.OrdinalIgnoreCase)
            })
            .ToList();
    }

    private static (string? Display, string Source) ResolveVersion(ProcessCatalogEntry e)
    {
        if (!string.IsNullOrWhiteSpace(e.FileVersion))
            return (e.FileVersion, "file");
        if (!string.IsNullOrWhiteSpace(e.ProductVersion))
            return (e.ProductVersion, "product");
        if (!string.IsNullOrWhiteSpace(e.ManualVersion))
            return (e.ManualVersion, "manual");

        var derived = DeriveVersionFromPath(e.ExecutablePath);
        return derived is null ? (null, "unknown") : (derived, "path");
    }

    public static string? DeriveVersionFromPath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
            return null;
        var matches = VersionPattern.Matches(path);
        return matches.Count > 0 ? matches[^1].Value : null;
    }

    public static string GroupLabel(AppGroup group) => ProcessClassification.GroupLabel(group);

    /// <summary>Seed taxonomy from the standard classification CSV so dropdowns work before/after import.</summary>
    private static readonly string[] DefaultCategoryTaxonomy =
    [
        "CAD", "Core Windows", "Hardware & Drivers", "Productivity", "SOE", "Security", "Unclassified"
    ];

    private static readonly string[] DefaultSubcategoryTaxonomy =
    [
        ".NET Framework Host", ".NET Runtime Host", "AI Assistant", "Admin Tool", "Antivirus/EDR",
        "Application Control Agent", "Application Suite Launcher", "Asset Tracking Agent",
        "Audio Driver Control App", "Audio Driver Service", "Audio Licensing Service", "Audio Subsystem",
        "Background Helper", "Background Service", "Bluetooth Driver Service", "Browser Component",
        "Bundled Runtime", "CAD Application", "Cloud Collaboration Component", "Cloud Storage Client",
        "Cloud Sync Client", "Cloud Sync Component", "Collaboration/Video Conferencing",
        "Companion Device Service", "Compiler Service", "Connectivity Driver Service",
        "Container/Virtualization Component", "Content Delivery Agent", "Crash Reporting Component",
        "Crash/Error Reporting Service", "DLP Agent", "DNS Security Client", "Database Service",
        "Device Association Service", "Device Management Agent", "Document Template Client",
        "Driver Framework Host", "Driver Service", "Driver/Control Panel App", "EDR Agent",
        "EDR Agent Component", "Encryption Management Agent", "Firmware Update Service",
        "Firmware/Chipset Service", "Font Rendering Service", "IDE", "Identity Broker",
        "Identity/License Service", "Installer/Updater", "Internal Business Application",
        "JavaScript Runtime", "Kernel/System Process", "License Manager", "License Manager Component",
        "License Verification", "License/Access Service", "MFA/Device Trust Client",
        "Management Platform Service", "Messaging App", "Messaging Service", "Microsoft Store Component",
        "Network Discovery Service", "Network Monitoring Agent", "Notification Helper",
        "OEM Companion App", "OEM Companion App Component", "OEM Diagnostic Helper",
        "OS Background Host", "OS Background Service", "Office Suite Application",
        "Package Manager Service", "Peripheral Companion App", "Peripheral Control",
        "Power Management Service", "Print Client", "Print Spooler", "Print Spooler Component",
        "Remote Access Client", "Remote Support Client", "Screen Capture Tool",
        "Screen Capture Tool Component", "Scripting Host", "Secure Web Gateway Client",
        "Security Kernel Component", "Sensor Driver Service", "Shared Updater Component",
        "Shell Component", "Software Asset Management Agent", "System Monitoring Agent",
        "Systray Helper", "Telemetry Agent", "Telemetry/Monitoring", "Terminal", "Terminal Component",
        "Text Editor", "Transaction Coordinator", "Unclassified", "Update Notification UI",
        "Update Service", "Updater", "Utility App", "Utility App Component", "Utility Suite",
        "VPN Client", "VPN/Network Security Client", "Vendor Application Service",
        "Vendor Platform Service", "Virtualization Service", "Vulnerability Management Agent",
        "WMI Provider Service", "Web Browser", "Windows Security Center", "Wireless Driver Service",
        "Wireless Presentation Client", "Zero Trust Access Client"
    ];

    private static IReadOnlyList<string> MergeOptionLists(IEnumerable<string?> fromCatalog, IEnumerable<string> seed) =>
        fromCatalog
            .Concat(seed)
            .Where(c => !string.IsNullOrWhiteSpace(c))
            .Select(c => c!.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(c => c, StringComparer.OrdinalIgnoreCase)
            .ToList();

    public record DiscoveryRow(
        int Id,
        string Name,
        string ExeName,
        string Path,
        string? Description,
        string? Category,
        string? Subcategory,
        string? VersionDisplay,
        string VersionSource,
        string? ManualVersion,
        IReadOnlyList<string> SeenHosts,
        bool Ignored,
        bool Unclassified,
        AppGroup? SuggestedGroup,
        string? SuggestionReason);

    public sealed class DiscoveryEditInput
    {
        public int Id { get; set; }
        public string? Name { get; set; }
        public string? Description { get; set; }
        public string? Category { get; set; }
        public string? Subcategory { get; set; }
        public string? Version { get; set; }
    }
}
