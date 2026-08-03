using System.Text.RegularExpressions;
using Heimdall.Api.Data;
using Heimdall.Api.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;

namespace Heimdall.Api.Pages;

/// <summary>
/// Discovery: every process in the central ProcessCatalog (including not-yet-classified ones),
/// with a manually-editable friendly name and version, plus how many machines it was seen on and
/// how often people actually run it (see ProcessRunMetrics.AverageWeeklyUsers).
/// </summary>
public class DiscoveryModel(HeimdallDbContext db, ProcessCatalogService catalog, ProcessGroupService processGroups) : PageModel
{
    private static readonly Regex VersionPattern = new(@"\d+(\.\d+){1,3}", RegexOptions.Compiled);

    public List<DiscoveryRow> Rows { get; private set; } = [];
    public int TotalCount { get; private set; }
    public int UnclassifiedCount { get; private set; }
    public int IgnoredCount { get; private set; }

    [BindProperty(SupportsGet = true)]
    public bool HideIgnored { get; set; }

    [BindProperty(SupportsGet = true)]
    public bool BlankPathOnly { get; set; }

    public int BlankPathCount { get; private set; }

    public async Task OnGetAsync()
    {
        await LoadAsync();
    }

    public async Task<IActionResult> OnPostSaveAllAsync(bool hideIgnored)
    {
        var edits = ParseEditsFromForm();
        if (edits.Count == 0)
        {
            TempData["Error"] = "Nothing to save.";
            return RedirectToPage(new { hideIgnored, blankPathOnly = BlankPathOnly });
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
        return RedirectToPage(new { hideIgnored, blankPathOnly = BlankPathOnly });
    }

    public async Task<IActionResult> OnPostIgnoreAsync(int id, bool hideIgnored)
    {
        var entry = await db.ProcessCatalogEntries.FindAsync([id], HttpContext.RequestAborted);
        if (entry is null)
        {
            TempData["Error"] = "Process not found — it may have been removed.";
            return RedirectToPage(new { hideIgnored, blankPathOnly = BlankPathOnly });
        }

        entry.Ignored = true;
        await db.SaveChangesAsync(HttpContext.RequestAborted);
        TempData["Message"] = $"Ignored {entry.ProcessName}.";
        return RedirectToPage(new { hideIgnored, blankPathOnly = BlankPathOnly });
    }

    public async Task<IActionResult> OnPostUnignoreAsync(int id, bool hideIgnored)
    {
        var entry = await db.ProcessCatalogEntries.FindAsync([id], HttpContext.RequestAborted);
        if (entry is null)
        {
            TempData["Error"] = "Process not found — it may have been removed.";
            return RedirectToPage(new { hideIgnored, blankPathOnly = BlankPathOnly });
        }

        entry.Ignored = false;
        await db.SaveChangesAsync(HttpContext.RequestAborted);
        TempData["Message"] = $"Restored {entry.ProcessName}.";
        return RedirectToPage(new { hideIgnored, blankPathOnly = BlankPathOnly });
    }

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

        var ctx = await processGroups.BuildContextAsync(HttpContext.RequestAborted);

        if (HideIgnored)
            entries = entries.Where(e => !e.Ignored).ToList();
        if (BlankPathOnly)
            entries = entries.Where(e => string.IsNullOrWhiteSpace(e.ExecutablePath)).ToList();

        var allRuns = await db.ProcessRuns.AsNoTracking().ToListAsync(HttpContext.RequestAborted);
        var runsByKey = allRuns
            .GroupBy(r => CatalogKey(r.ProcessName, r.ExecutablePath))
            .ToDictionary(g => g.Key, g => (IReadOnlyList<ProcessRun>)g.ToList());

        Rows = entries.Select(e =>
        {
            var key = CatalogKey(e.ProcessName, e.ExecutablePath);
            var runs = runsByKey.TryGetValue(key, out var list) ? list : Array.Empty<ProcessRun>();
            var (versionDisplay, versionSource) = ResolveVersion(e);

            return new DiscoveryRow(
                e.Id,
                string.IsNullOrWhiteSpace(e.DisplayName) ? e.ProcessName : e.DisplayName!,
                e.ProcessName,
                e.ExecutablePath,
                e.Description,
                e.Category,
                e.Subcategory,
                versionDisplay,
                versionSource,
                e.ManualVersion,
                ProcessCatalogService.GetSeenHostnames(e),
                ProcessRunMetrics.AverageWeeklyUsers(runs),
                e.Ignored,
                !e.Ignored && ProcessCatalogService.NeedsClassification(e.ProcessName, ctx));
        })
        .OrderByDescending(r => r.AvgWeeklyUsers)
        .ThenBy(r => r.Name, StringComparer.OrdinalIgnoreCase)
        .ToList();
    }

    private static (string Name, string Path) CatalogKey(string processName, string? executablePath) =>
        (ConfigService.NormalizeProcessName(processName).ToLowerInvariant(),
         (executablePath ?? "").Trim().ToLowerInvariant());

    private static (string? Display, string Source) ResolveVersion(ProcessCatalogEntry e)
    {
        if (!string.IsNullOrWhiteSpace(e.FileVersion))
            return (e.FileVersion, "agent (file version)");
        if (!string.IsNullOrWhiteSpace(e.ProductVersion))
            return (e.ProductVersion, "agent (product version)");
        if (!string.IsNullOrWhiteSpace(e.ManualVersion))
            return (e.ManualVersion, "manual");

        var derived = DeriveVersionFromPath(e.ExecutablePath);
        return derived is null ? (null, "unknown") : (derived, "derived from path");
    }

    public static string? DeriveVersionFromPath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
            return null;
        var matches = VersionPattern.Matches(path);
        return matches.Count > 0 ? matches[^1].Value : null;
    }

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
        double AvgWeeklyUsers,
        bool Ignored,
        bool Unclassified);

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
