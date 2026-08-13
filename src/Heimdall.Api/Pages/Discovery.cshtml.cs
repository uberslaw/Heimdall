using System.Text.RegularExpressions;
using Heimdall.Api.Data;
using Heimdall.Api.Services;
using Heimdall.Shared;
using Heimdall.Shared.Contracts;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;

namespace Heimdall.Api.Pages;

/// <summary>
/// Discovery &amp; Classification: processes awaiting group approval (Core Windows / SOE / Specialization),
/// including pending import/AI suggestions. Friendly name is editable; path is read-only.
/// </summary>
public class DiscoveryModel(HeimdallDbContext db, ProcessCatalogService catalog, ProcessGroupService processGroups, AppListService appLists, SpecReviewService specReview) : PageModel
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

    public IReadOnlyList<SpecReviewService.ReviewAppRow> SpecPending { get; private set; } = [];
    public IReadOnlyList<SpecReviewService.UntamedAppRow> SpecUntamed { get; private set; } = [];
    public IReadOnlyList<SpecReviewService.StaleAlertRow> SpecStaleAlerts { get; private set; } = [];

    [BindProperty(SupportsGet = true)]
    public string Tab { get; set; } = "classify";

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
    public string Dir { get; set; } = "desc";

    public async Task OnGetAsync()
    {
        Tab = NormalizeTab(Tab);
        if (Tab == "spec-review")
        {
            var page = await specReview.GetReviewPageAsync(HttpContext.RequestAborted);
            SpecPending = page.Pending;
            SpecUntamed = page.Untamed;
            SpecStaleAlerts = page.Stale;
            return;
        }

        await LoadAsync(skipBackfill: TempData["SkipDiscoveryBackfill"] as string == "1");
    }

    public async Task<IActionResult> OnPostSpecContinueAsync(int reviewId)
    {
        try
        {
            await specReview.ContinueAsync(reviewId, HttpContext.RequestAborted);
            TempData["Message"] = "Continued tracking on the team primary list.";
        }
        catch (Exception ex)
        {
            TempData["Error"] = ex.Message;
        }

        return RedirectToPage(new { tab = "spec-review" });
    }

    public async Task<IActionResult> OnPostSpecIgnoreAsync(int reviewId)
    {
        try
        {
            await specReview.IgnoreAsync(reviewId, HttpContext.RequestAborted);
            TempData["Message"] = "Ignored for this team — removed from primary list (recover later from Applications).";
        }
        catch (Exception ex)
        {
            TempData["Error"] = ex.Message;
        }

        return RedirectToPage(new { tab = "spec-review" });
    }

    public async Task<IActionResult> OnPostSpecStaleKeepAsync(int alertId)
    {
        try
        {
            await specReview.ResolveStaleAlertAsync(alertId, keepSticky: true, HttpContext.RequestAborted);
            TempData["Message"] = "Kept network sticky link.";
        }
        catch (Exception ex)
        {
            TempData["Error"] = ex.Message;
        }

        return RedirectToPage(new { tab = "spec-review" });
    }

    public async Task<IActionResult> OnPostSpecStaleRemoveAsync(int alertId)
    {
        try
        {
            await specReview.ResolveStaleAlertAsync(alertId, keepSticky: false, HttpContext.RequestAborted);
            TempData["Message"] = "Removed / archived stale network Spec app.";
        }
        catch (Exception ex)
        {
            TempData["Error"] = ex.Message;
        }

        return RedirectToPage(new { tab = "spec-review" });
    }

    public async Task<IActionResult> OnPostApproveAsync(int id)
    {
        var entry = await db.ProcessCatalogEntries.FindAsync([id], HttpContext.RequestAborted);
        if (entry is null)
        {
            if (WantsAjax())
                return new JsonResult(new { ok = false, error = "Process not found — it may have been removed." }) { StatusCode = 404 };
            TempData["Error"] = "Process not found — it may have been removed.";
            return RedirectToFilters();
        }

        if (entry.SuggestedGroup is null)
        {
            if (WantsAjax())
                return new JsonResult(new { ok = false, error = "No pending suggestion to approve — use Set instead." }) { StatusCode = 400 };
            TempData["Error"] = "No pending suggestion to approve — use Set instead.";
            return RedirectToFilters();
        }

        var group = entry.SuggestedGroup.Value;
        var label = ProcessClassification.GroupLabel(group);
        var programNames = await catalog.GetProcessNamesInSameProgramAsync(
            entry.ExecutablePath, entry.ProcessName, HttpContext.RequestAborted);
        await processGroups.AssignGroupsAsync(programNames, group, HttpContext.RequestAborted);
        await catalog.ClearSuggestionsAsync(programNames, HttpContext.RequestAborted);
        await appLists.SyncSystemListsFromClassificationsAsync(HttpContext.RequestAborted);
        if (group == AppGroup.Specialization)
        {
            await EnableAdvertiseRdpForProcessNamesAsync(programNames, HttpContext.RequestAborted);
            await specReview.OnClassifiedAsSpecializationAsync(programNames, HttpContext.RequestAborted);
        }

        var program = ProgramInstallRoot.TryExtract(entry.ExecutablePath);
        var message = programNames.Count > 1 && program is not null
            ? $"Approved {entry.ProcessName} and {programNames.Count - 1} other process(es) in “{program.DisplayName}” as {label}."
            : $"Approved {entry.ProcessName} as {label}.";
        if (WantsAjax())
        {
            // Cheap counts for header; skip full catalog rebuild / HTML render.
            var pending = await db.ProcessCatalogEntries.AsNoTracking()
                .CountAsync(e => !e.Ignored && e.SuggestedGroup != null, HttpContext.RequestAborted);
            var ctx = await processGroups.BuildContextAsync(HttpContext.RequestAborted);
            var unclassified = await db.ProcessCatalogEntries.AsNoTracking()
                .Where(e => !e.Ignored)
                .Select(e => e.ProcessName)
                .Distinct()
                .ToListAsync(HttpContext.RequestAborted);
            var unclassifiedCount = unclassified.Count(n => ProcessCatalogService.NeedsClassification(n, ctx));

            return new JsonResult(new
            {
                ok = true,
                id,
                message,
                group = group.ToString(),
                groupLabel = label,
                allowAdvertiseRdp = group == AppGroup.Specialization,
                removeRow = !ShowClassified,
                pendingSuggestionCount = pending,
                unclassifiedCount
            });
        }

        TempData["Message"] = message;
        TempData["SkipDiscoveryBackfill"] = "1";
        return RedirectToFilters();
    }

    /// <summary>
    /// Approve pending suggestions in small batches (default 40). AJAX clients loop until <c>done</c>;
    /// avoids one giant request that can starve SQLite / hang Kestrel under hundreds of rows.
    /// Approves the global pending queue (not only currently visible/filtered rows). Never runs catalog backfill.
    /// </summary>
    public async Task<IActionResult> OnPostApproveAllAsync(int take = 40)
    {
        const int defaultTake = 40;
        const int maxTake = 80;
        take = Math.Clamp(take <= 0 ? defaultTake : take, 1, maxTake);
        var ct = HttpContext.RequestAborted;

        try
        {
            var batch = await db.ProcessCatalogEntries
                .Where(e => !e.Ignored && e.SuggestedGroup != null)
                .OrderBy(e => e.Id)
                .Take(take)
                .ToListAsync(ct);

            if (batch.Count == 0)
            {
                if (WantsAjax())
                {
                    return new JsonResult(new
                    {
                        ok = true,
                        done = true,
                        approved = 0,
                        remaining = 0,
                        approvedIds = Array.Empty<int>(),
                        pendingSuggestionCount = 0,
                        unclassifiedCount = await CountUnclassifiedAsync(ct),
                        message = "No pending suggestions to approve."
                    });
                }

                TempData["Error"] = "No pending suggestions to approve.";
                return RedirectToFilters();
            }

            var (approvedNames, approvedIds) = await ApplySuggestionBatchAsync(batch, ct);

            var remaining = await db.ProcessCatalogEntries.AsNoTracking()
                .CountAsync(e => !e.Ignored && e.SuggestedGroup != null, ct);
            var done = remaining == 0;
            var message = approvedNames == 1
                ? "Approved 1 pending suggestion."
                : $"Approved {approvedNames} pending suggestion(s)" + (done ? "." : $" ({remaining} remaining).");

            if (WantsAjax())
            {
                object? unclassifiedCount = done ? await CountUnclassifiedAsync(ct) : null;
                return new JsonResult(new
                {
                    ok = true,
                    done,
                    approved = approvedNames,
                    remaining,
                    approvedIds,
                    pendingSuggestionCount = remaining,
                    unclassifiedCount,
                    message
                });
            }

            // Non-JS: drain further batches in this request with a hard ceiling (no infinite loop).
            var totalApproved = approvedNames;
            for (var i = 0; remaining > 0 && i < 24; i++)
            {
                var more = await db.ProcessCatalogEntries
                    .Where(e => !e.Ignored && e.SuggestedGroup != null)
                    .OrderBy(e => e.Id)
                    .Take(take)
                    .ToListAsync(ct);
                if (more.Count == 0) break;

                var (moreNames, _) = await ApplySuggestionBatchAsync(more, ct);
                totalApproved += moreNames;
                remaining = await db.ProcessCatalogEntries.AsNoTracking()
                    .CountAsync(e => !e.Ignored && e.SuggestedGroup != null, ct);
            }

            TempData["Message"] = remaining == 0
                ? (totalApproved == 1 ? "Approved 1 pending suggestion." : $"Approved {totalApproved} pending suggestions.")
                : $"Approved {totalApproved} suggestion(s); {remaining} still pending — click Approve all again.";
            TempData["SkipDiscoveryBackfill"] = "1";
            return RedirectToFilters();
        }
        catch (Exception ex) when (WantsAjax())
        {
            return new JsonResult(new
            {
                ok = false,
                error = "Approve-all batch failed: " + ex.Message
            })
            { StatusCode = 500 };
        }
    }

    /// <summary>
    /// Approve suggested groups for the selected catalog row IDs (AJAX batch; max 80).
    /// </summary>
    public async Task<IActionResult> OnPostBatchApproveAsync(int[]? ids)
    {
        var ct = HttpContext.RequestAborted;
        var idList = NormalizeIdList(ids);
        if (idList.Count == 0)
            return new JsonResult(new { ok = false, error = "Select at least one row." }) { StatusCode = 400 };

        try
        {
            var batch = await db.ProcessCatalogEntries
                .Where(e => idList.Contains(e.Id) && !e.Ignored && e.SuggestedGroup != null)
                .ToListAsync(ct);
            if (batch.Count == 0)
                return new JsonResult(new { ok = false, error = "No pending suggestions on the selected rows." }) { StatusCode = 400 };

            var (approvedNames, approvedIds) = await ApplySuggestionBatchAsync(batch, ct);
            var pending = await db.ProcessCatalogEntries.AsNoTracking()
                .CountAsync(e => !e.Ignored && e.SuggestedGroup != null, ct);
            var unclassifiedCount = await CountUnclassifiedAsync(ct);
            return new JsonResult(new
            {
                ok = true,
                approved = approvedNames,
                approvedIds,
                pendingSuggestionCount = pending,
                unclassifiedCount,
                removeRow = !ShowClassified,
                message = approvedNames == 1
                    ? "Approved 1 selected suggestion."
                    : $"Approved {approvedNames} selected suggestion(s)."
            });
        }
        catch (Exception ex)
        {
            return new JsonResult(new { ok = false, error = "Batch approve failed: " + ex.Message }) { StatusCode = 500 };
        }
    }

    /// <summary>
    /// Set Classification (+ optional Category/Subcategory) for selected rows (AJAX; max 80).
    /// </summary>
    public async Task<IActionResult> OnPostBatchSetAsync(int[]? ids, string group, string? category, string? subcategory)
    {
        var ct = HttpContext.RequestAborted;
        var idList = NormalizeIdList(ids);
        if (idList.Count == 0)
            return new JsonResult(new { ok = false, error = "Select at least one row." }) { StatusCode = 400 };

        if (!ProcessGroupService.TryParseGroup(group, out var targetGroup))
            return new JsonResult(new { ok = false, error = "Choose Core Windows, SOE, or Specialization." }) { StatusCode = 400 };

        try
        {
            var entries = await db.ProcessCatalogEntries
                .Where(e => idList.Contains(e.Id) && !e.Ignored)
                .ToListAsync(ct);
            if (entries.Count == 0)
                return new JsonResult(new { ok = false, error = "No editable rows in the selection." }) { StatusCode = 400 };

            var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var e in entries)
            {
                foreach (var n in await catalog.GetProcessNamesInSameProgramAsync(e.ExecutablePath, e.ProcessName, ct))
                    names.Add(n);
            }
            var nameList = names.ToList();
            await processGroups.AssignGroupsAsync(nameList, targetGroup, ct);
            await appLists.SyncSystemListsFromClassificationsAsync(ct);
            if (targetGroup == AppGroup.Specialization)
                await specReview.OnClassifiedAsSpecializationAsync(nameList, ct);

            var cat = string.IsNullOrWhiteSpace(category) ? null : category.Trim();
            var sub = string.IsNullOrWhiteSpace(subcategory) ? null : subcategory.Trim();
            // Stamp all path variants for each name (same as single Set).
            var allForNames = await db.ProcessCatalogEntries
                .Where(e => nameList.Contains(e.ProcessName))
                .ToListAsync(ct);
            foreach (var e in allForNames)
            {
                e.Category = cat;
                e.Subcategory = sub;
                e.SuggestedGroup = null;
                e.SuggestionReason = null;
                if (targetGroup == AppGroup.Specialization)
                    e.AllowAdvertiseRdp = true;
            }
            await db.SaveChangesAsync(ct);
            db.ChangeTracker.Clear();

            var ctx = await processGroups.BuildContextAsync(ct);
            var pending = await db.ProcessCatalogEntries.AsNoTracking()
                .CountAsync(e => !e.Ignored && e.SuggestedGroup != null, ct);
            var unclassifiedCount = await CountUnclassifiedAsync(ct);
            var label = ProcessClassification.GroupLabel(targetGroup);
            var affectedIds = allForNames.Select(e => e.Id).Distinct().ToArray();
            var removeIds = !ShowClassified
                ? allForNames.Where(e => !ProcessCatalogService.NeedsClassification(e.ProcessName, ctx)).Select(e => e.Id).Distinct().ToArray()
                : Array.Empty<int>();

            return new JsonResult(new
            {
                ok = true,
                updated = nameList.Count,
                approvedIds = affectedIds,
                removeIds,
                group = targetGroup.ToString(),
                groupLabel = label,
                category = cat,
                subcategory = sub,
                allowAdvertiseRdp = targetGroup == AppGroup.Specialization,
                pendingSuggestionCount = pending,
                unclassifiedCount,
                removeRow = !ShowClassified,
                message = nameList.Count == 1
                    ? $"Set 1 process to {label}."
                    : $"Set {nameList.Count} processes (including same-program siblings) to {label}."
            });
        }
        catch (Exception ex)
        {
            return new JsonResult(new { ok = false, error = "Batch set failed: " + ex.Message }) { StatusCode = 500 };
        }
    }

    /// <summary>
    /// Apply Category and/or Subcategory to selected rows without changing Classification (AJAX; max 80).
    /// </summary>
    public async Task<IActionResult> OnPostBatchTaxonomyAsync(int[]? ids, string? category, string? subcategory, bool setCategory = true, bool setSubcategory = true)
    {
        var ct = HttpContext.RequestAborted;
        var idList = NormalizeIdList(ids);
        if (idList.Count == 0)
            return new JsonResult(new { ok = false, error = "Select at least one row." }) { StatusCode = 400 };
        if (!setCategory && !setSubcategory)
            return new JsonResult(new { ok = false, error = "Nothing to apply." }) { StatusCode = 400 };

        try
        {
            var entries = await db.ProcessCatalogEntries
                .Where(e => idList.Contains(e.Id) && !e.Ignored)
                .ToListAsync(ct);
            if (entries.Count == 0)
                return new JsonResult(new { ok = false, error = "No editable rows in the selection." }) { StatusCode = 400 };

            var cat = string.IsNullOrWhiteSpace(category) ? null : category.Trim();
            var sub = string.IsNullOrWhiteSpace(subcategory) ? null : subcategory.Trim();
            foreach (var e in entries)
            {
                if (setCategory) e.Category = cat;
                if (setSubcategory) e.Subcategory = sub;
            }
            await db.SaveChangesAsync(ct);

            return new JsonResult(new
            {
                ok = true,
                updated = entries.Count,
                updatedIds = entries.Select(e => e.Id).ToArray(),
                category = setCategory ? cat : null,
                subcategory = setSubcategory ? sub : null,
                setCategory,
                setSubcategory,
                message = entries.Count == 1
                    ? "Updated Category/Subcategory on 1 row."
                    : $"Updated Category/Subcategory on {entries.Count} rows."
            });
        }
        catch (Exception ex)
        {
            return new JsonResult(new { ok = false, error = "Batch taxonomy failed: " + ex.Message }) { StatusCode = 500 };
        }
    }

    /// <summary>
    /// Assign suggested groups then clear suggestions for every path variant of those process names.
    /// Returns distinct name count + all catalog IDs whose suggestion was cleared.
    /// </summary>
    private async Task<(int ApprovedNames, int[] ApprovedIds)> ApplySuggestionBatchAsync(
        List<ProcessCatalogEntry> batch, CancellationToken ct)
    {
        var expandedByGroup = new Dictionary<AppGroup, HashSet<string>>();
        foreach (var e in batch)
        {
            if (e.SuggestedGroup is null) continue;
            if (!expandedByGroup.TryGetValue(e.SuggestedGroup.Value, out var set))
            {
                set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                expandedByGroup[e.SuggestedGroup.Value] = set;
            }
            foreach (var n in await catalog.GetProcessNamesInSameProgramAsync(e.ExecutablePath, e.ProcessName, ct))
                set.Add(n);
        }

        var nameSet = expandedByGroup.Values.SelectMany(s => s).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        var specNames = new List<string>();
        foreach (var (group, names) in expandedByGroup)
        {
            var list = names.ToList();
            await processGroups.AssignGroupsAsync(list, group, ct);
            if (group == AppGroup.Specialization)
                specNames.AddRange(list);
        }

        // Clear every path variant for these names (AssignGroups is name-scoped).
        var toClear = await db.ProcessCatalogEntries
            .Where(e => nameSet.Contains(e.ProcessName) && (e.SuggestedGroup != null || e.SuggestionReason != null))
            .ToListAsync(ct);
        foreach (var e in toClear)
        {
            e.SuggestedGroup = null;
            e.SuggestionReason = null;
        }
        if (toClear.Count > 0)
            await db.SaveChangesAsync(ct);

        db.ChangeTracker.Clear();
        await appLists.SyncSystemListsFromClassificationsAsync(ct);
        if (specNames.Count > 0)
        {
            var specDistinct = specNames.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            await EnableAdvertiseRdpForProcessNamesAsync(specDistinct, ct);
            await specReview.OnClassifiedAsSpecializationAsync(specDistinct, ct);
        }
        return (nameSet.Count, toClear.Select(e => e.Id).Distinct().ToArray());
    }

    private static List<int> NormalizeIdList(int[]? ids) =>
        (ids ?? [])
            .Where(id => id > 0)
            .Distinct()
            .Take(80)
            .ToList();

    private async Task EnableAdvertiseRdpForProcessNamesAsync(IEnumerable<string> processNames, CancellationToken ct)
    {
        var names = processNames
            .Where(n => !string.IsNullOrWhiteSpace(n))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (names.Count == 0) return;

        var entries = await db.ProcessCatalogEntries
            .Where(e => names.Contains(e.ProcessName) && !e.AllowAdvertiseRdp)
            .ToListAsync(ct);
        if (entries.Count == 0) return;
        foreach (var e in entries)
            e.AllowAdvertiseRdp = true;
        await db.SaveChangesAsync(ct);
    }

    private async Task<int> CountUnclassifiedAsync(CancellationToken ct)
    {
        var ctx = await processGroups.BuildContextAsync(ct);
        var names = await db.ProcessCatalogEntries.AsNoTracking()
            .Where(e => !e.Ignored)
            .Select(e => e.ProcessName)
            .Distinct()
            .ToListAsync(ct);
        return names.Count(n => ProcessCatalogService.NeedsClassification(n, ctx));
    }

    public async Task<IActionResult> OnPostSetAsync(int id, string group, string? category, string? subcategory)
    {
        var entry = await db.ProcessCatalogEntries.FindAsync([id], HttpContext.RequestAborted);
        if (entry is null)
        {
            if (WantsAjax())
                return new JsonResult(new { ok = false, error = "Process not found — it may have been removed." }) { StatusCode = 404 };
            TempData["Error"] = "Process not found — it may have been removed.";
            return RedirectToFilters();
        }

        if (!ProcessGroupService.TryParseGroup(group, out var targetGroup))
        {
            if (WantsAjax())
                return new JsonResult(new { ok = false, error = "Choose Core Windows, SOE, or Specialization." }) { StatusCode = 400 };
            TempData["Error"] = "Choose Core Windows, SOE, or Specialization.";
            return RedirectToFilters();
        }

        var programNames = await catalog.GetProcessNamesInSameProgramAsync(
            entry.ExecutablePath, entry.ProcessName, HttpContext.RequestAborted);
        await processGroups.AssignGroupsAsync(programNames, targetGroup, HttpContext.RequestAborted);
        await appLists.SyncSystemListsFromClassificationsAsync(HttpContext.RequestAborted);
        if (targetGroup == AppGroup.Specialization)
            await specReview.OnClassifiedAsSpecializationAsync(programNames, HttpContext.RequestAborted);

        // Re-load after AssignGroups (may have saved); update category fields on all name matches.
        // Empty dropdown ("—") clears Category/Subcategory.
        var cat = string.IsNullOrWhiteSpace(category) ? null : category.Trim();
        var sub = string.IsNullOrWhiteSpace(subcategory) ? null : subcategory.Trim();
        var entries = await db.ProcessCatalogEntries
            .Where(e => programNames.Contains(e.ProcessName))
            .ToListAsync(HttpContext.RequestAborted);
        foreach (var e in entries)
        {
            e.Category = cat;
            e.Subcategory = sub;
            e.SuggestedGroup = null;
            e.SuggestionReason = null;
            if (targetGroup == AppGroup.Specialization)
                e.AllowAdvertiseRdp = true;
        }
        await db.SaveChangesAsync(HttpContext.RequestAborted);

        var label = ProcessClassification.GroupLabel(targetGroup);
        var program = ProgramInstallRoot.TryExtract(entry.ExecutablePath);
        var message = programNames.Count > 1 && program is not null
            ? $"Set {entry.ProcessName} and {programNames.Count - 1} other process(es) in “{program.DisplayName}” to {label}."
            : $"Set {entry.ProcessName} to {label}.";
        if (WantsAjax())
        {
            var pending = await db.ProcessCatalogEntries.AsNoTracking()
                .CountAsync(e => !e.Ignored && e.SuggestedGroup != null, HttpContext.RequestAborted);
            var ctx = await processGroups.BuildContextAsync(HttpContext.RequestAborted);
            var unclassified = await db.ProcessCatalogEntries.AsNoTracking()
                .Where(e => !e.Ignored)
                .Select(e => e.ProcessName)
                .Distinct()
                .ToListAsync(HttpContext.RequestAborted);
            var unclassifiedCount = unclassified.Count(n => ProcessCatalogService.NeedsClassification(n, ctx));
            var stillNeeds = ProcessCatalogService.NeedsClassification(entry.ProcessName, ctx);

            return new JsonResult(new
            {
                ok = true,
                id,
                message,
                group = targetGroup.ToString(),
                allowAdvertiseRdp = targetGroup == AppGroup.Specialization,
                groupLabel = label,
                category = cat,
                subcategory = sub,
                removeRow = !ShowClassified && !stillNeeds,
                pendingSuggestionCount = pending,
                unclassifiedCount
            });
        }

        TempData["Message"] = message;
        TempData["SkipDiscoveryBackfill"] = "1";
        return RedirectToFilters();
    }

    public async Task<IActionResult> OnPostSetAdvertiseRdpAsync(int id, bool allow)
    {
        var ct = HttpContext.RequestAborted;
        var entry = await db.ProcessCatalogEntries.FindAsync([id], ct);
        if (entry is null)
            return new JsonResult(new { ok = false, error = "Process not found — it may have been removed." }) { StatusCode = 404 };

        entry.AllowAdvertiseRdp = allow;
        await db.SaveChangesAsync(ct);
        return new JsonResult(new
        {
            ok = true,
            id,
            allowAdvertiseRdp = allow,
            message = allow
                ? $"Advertise RDP on for {entry.ProcessName}."
                : $"Advertise RDP off for {entry.ProcessName}."
        });
    }

    public async Task<IActionResult> OnPostBatchAdvertiseRdpAsync(int[]? ids, bool allow)
    {
        var ct = HttpContext.RequestAborted;
        var idList = NormalizeIdList(ids);
        if (idList.Count == 0)
            return new JsonResult(new { ok = false, error = "Select at least one row." }) { StatusCode = 400 };

        var entries = await db.ProcessCatalogEntries
            .Where(e => idList.Contains(e.Id) && !e.Ignored)
            .ToListAsync(ct);
        if (entries.Count == 0)
            return new JsonResult(new { ok = false, error = "No editable rows in the selection." }) { StatusCode = 400 };

        foreach (var e in entries)
            e.AllowAdvertiseRdp = allow;
        await db.SaveChangesAsync(ct);

        return new JsonResult(new
        {
            ok = true,
            updated = entries.Count,
            updatedIds = entries.Select(e => e.Id).ToArray(),
            allowAdvertiseRdp = allow,
            message = allow
                ? (entries.Count == 1 ? "Advertise RDP on for 1 row." : $"Advertise RDP on for {entries.Count} rows.")
                : (entries.Count == 1 ? "Advertise RDP off for 1 row." : $"Advertise RDP off for {entries.Count} rows.")
        });
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
        TempData["SkipDiscoveryBackfill"] = "1";
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
        TempData["SkipDiscoveryBackfill"] = "1";
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
            tab = Tab,
            hideIgnored = HideIgnored,
            blankPathOnly = BlankPathOnly,
            showClassified = ShowClassified,
            q = Q,
            sort = Sort,
            dir = Dir
        });

    private static string NormalizeTab(string? tab) =>
        string.Equals(tab, "spec-review", StringComparison.OrdinalIgnoreCase) ? "spec-review" : "classify";

    private bool WantsAjax() =>
        string.Equals(Request.Headers.Accept.ToString(), "application/json", StringComparison.OrdinalIgnoreCase)
        || string.Equals(Request.Query["ajax"], "1", StringComparison.OrdinalIgnoreCase)
        || Request.Headers.XRequestedWith == "XMLHttpRequest";

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

    private async Task LoadAsync(bool skipBackfill = false)
    {
        // Backfill scans ProcessRuns + inventories + full catalog upsert/suggestion pool — seconds on a warm catalog.
        // Skip after Approve/Set redirects; AJAX Approve never hits this path.
        if (!skipBackfill)
        {
            await catalog.PurgeIneligibleEntriesAsync(HttpContext.RequestAborted);
            await catalog.BackfillFromDiscoveriesAsync(HttpContext.RequestAborted);
        }
        else
        {
            // Still strip temp/.tmp junk even when skipping the expensive backfill.
            await catalog.PurgeIneligibleEntriesAsync(HttpContext.RequestAborted);
        }

        var entries = await catalog.GetAllAsync(HttpContext.RequestAborted);
        var ctx = await processGroups.BuildContextAsync(HttpContext.RequestAborted);

        TotalCount = entries.Count;
        BlankPathCount = entries.Count(e => string.IsNullOrWhiteSpace(e.ExecutablePath));
        IgnoredCount = entries.Count(e => e.Ignored);
        // Reuse ctx — CountNeedingClassificationAsync would BuildContext + re-query names.
        UnclassifiedCount = entries
            .Where(e => !e.Ignored)
            .Select(e => e.ProcessName)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Count(n => ProcessCatalogService.NeedsClassification(n, ctx));
        PendingSuggestionCount = entries.Count(e => !e.Ignored && e.SuggestedGroup is not null);

        CategoryOptions = MergeOptionLists(
            entries.Select(e => e.Category),
            DefaultCategoryTaxonomy);
        SubcategoryOptions = MergeOptionLists(
            entries.Select(e => e.Subcategory),
            DefaultSubcategoryTaxonomy);

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
                || (ProgramInstallRoot.TryGetDisplayName(e.ExecutablePath)?.Contains(q, StringComparison.OrdinalIgnoreCase) ?? false)
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

            var program = ProgramInstallRoot.TryExtract(e.ExecutablePath);
            return new DiscoveryRow(
                e.Id,
                displayName,
                e.ProcessName,
                e.ExecutablePath,
                program?.Key,
                program?.DisplayName,
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
                e.SuggestionReason,
                e.AllowAdvertiseRdp,
                e.CompanyName);
        });

        var asc = !string.Equals(Dir, "desc", StringComparison.OrdinalIgnoreCase);
        var sortKey = (Sort ?? "name").Trim().ToLowerInvariant();
        Rows = (sortKey switch
            {
                "path" => asc
                    ? rows.OrderBy(r => r.Path, StringComparer.OrdinalIgnoreCase).ThenBy(r => r.Name, StringComparer.OrdinalIgnoreCase)
                    : rows.OrderByDescending(r => r.Path, StringComparer.OrdinalIgnoreCase).ThenBy(r => r.Name, StringComparer.OrdinalIgnoreCase),
                "program" => asc
                    ? rows.OrderBy(r => r.ProgramDisplay ?? "\uFFFF", StringComparer.OrdinalIgnoreCase).ThenBy(r => r.Name, StringComparer.OrdinalIgnoreCase)
                    : rows.OrderByDescending(r => r.ProgramDisplay ?? "", StringComparer.OrdinalIgnoreCase).ThenBy(r => r.Name, StringComparer.OrdinalIgnoreCase),
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
                    ? rows.OrderBy(r => r.ProgramDisplay ?? "\uFFFF", StringComparer.OrdinalIgnoreCase)
                        .ThenBy(r => r.Name, StringComparer.OrdinalIgnoreCase)
                        .ThenBy(r => r.Path, StringComparer.OrdinalIgnoreCase)
                    : rows.OrderByDescending(r => r.ProgramDisplay ?? "", StringComparer.OrdinalIgnoreCase)
                        .ThenByDescending(r => r.Name, StringComparer.OrdinalIgnoreCase)
                        .ThenBy(r => r.Path, StringComparer.OrdinalIgnoreCase)
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
        string? ProgramKey,
        string? ProgramDisplay,
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
        string? SuggestionReason,
        bool AllowAdvertiseRdp,
        string? CompanyName);

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
