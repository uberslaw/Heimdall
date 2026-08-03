using Heimdall.Api.Data;
using Heimdall.Shared.Contracts;
using Microsoft.EntityFrameworkCore;

namespace Heimdall.Api.Services;

/// <summary>
/// Maintains the central catalog of every unique process ever discovered (ProcessName + ExecutablePath
/// is the identity — the same executable name at a different path is tracked as a separate entry).
/// Flags newly-discovered processes and suggests a classification for ones that look related to
/// something already classified, using simple, documented heuristics (see SuggestClassification).
/// </summary>
public sealed class ProcessCatalogService(HeimdallDbContext db, ProcessGroupService processGroups)
{
    public sealed record CatalogItem(
        string ProcessName,
        string? ExecutablePath,
        string? DisplayName,
        string? FileVersion = null,
        string? ProductVersion = null,
        string? CompanyName = null,
        string? FileDescription = null);

    public sealed record UpsertSummary(int NewCount, int UpdatedCount, IReadOnlyList<string> NewProcessNames);

    /// <summary>Insert/refresh catalog rows for a batch of observed processes; flags + logs newly-identified ones.</summary>
    public async Task<UpsertSummary> UpsertAsync(
        IEnumerable<CatalogItem> items,
        string? hostname,
        string sourceLabel,
        CancellationToken ct = default)
    {
        var now = DateTimeOffset.UtcNow;
        var normalized = items
            .Select(i => new CatalogItem(
                ConfigService.NormalizeProcessName(i.ProcessName),
                string.IsNullOrWhiteSpace(i.ExecutablePath) ? "" : i.ExecutablePath.Trim(),
                string.IsNullOrWhiteSpace(i.DisplayName) ? null : i.DisplayName.Trim(),
                NullIfEmpty(i.FileVersion), NullIfEmpty(i.ProductVersion), NullIfEmpty(i.CompanyName), NullIfEmpty(i.FileDescription)))
            .Where(i => i.ProcessName.Length > 0)
            .GroupBy(i => (i.ProcessName.ToLowerInvariant(), i.ExecutablePath!.ToLowerInvariant()))
            .Select(g => g.First())
            .ToList();

        if (normalized.Count == 0)
            return new UpsertSummary(0, 0, []);

        var names = normalized.Select(i => i.ProcessName).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        var existingForNames = await db.ProcessCatalogEntries
            .Where(e => names.Contains(e.ProcessName))
            .ToListAsync(ct);
        var existingMap = existingForNames.ToDictionary(e => (e.ProcessName.ToLowerInvariant(), e.ExecutablePath.ToLowerInvariant()));

        var ctx = await processGroups.BuildContextAsync(ct);
        // Pool used for "similar item already classified" suggestions — POC-scale table, safe to load in full.
        var pool = await db.ProcessCatalogEntries.AsNoTracking().ToListAsync(ct);

        var newCount = 0;
        var updatedCount = 0;
        var newNames = new List<string>();
        var newWithSuggestion = 0;

        foreach (var item in normalized)
        {
            var key = (item.ProcessName.ToLowerInvariant(), item.ExecutablePath!.ToLowerInvariant());
            if (existingMap.TryGetValue(key, out var row))
            {
                row.LastSeenUtc = now;
                row.SeenCount++;
                if (!string.IsNullOrWhiteSpace(hostname))
                    row.LastSeenHostname = hostname;
                if (string.IsNullOrWhiteSpace(row.DisplayName) && item.DisplayName is not null) row.DisplayName = item.DisplayName;
                if (string.IsNullOrWhiteSpace(row.FileVersion) && item.FileVersion is not null) row.FileVersion = item.FileVersion;
                if (string.IsNullOrWhiteSpace(row.ProductVersion) && item.ProductVersion is not null) row.ProductVersion = item.ProductVersion;
                if (string.IsNullOrWhiteSpace(row.CompanyName) && item.CompanyName is not null) row.CompanyName = item.CompanyName;
                if (string.IsNullOrWhiteSpace(row.FileDescription) && item.FileDescription is not null) row.FileDescription = item.FileDescription;
                updatedCount++;
                continue;
            }

            var entry = new ProcessCatalogEntry
            {
                ProcessName = item.ProcessName,
                ExecutablePath = item.ExecutablePath!,
                DisplayName = item.DisplayName,
                FileVersion = item.FileVersion,
                ProductVersion = item.ProductVersion,
                CompanyName = item.CompanyName,
                FileDescription = item.FileDescription,
                FirstSeenUtc = now,
                LastSeenUtc = now,
                SeenCount = 1,
                FirstSeenHostname = hostname,
                LastSeenHostname = hostname
            };

            if (NeedsClassification(item.ProcessName, ctx))
            {
                var (group, reason) = SuggestClassification(item, pool, ctx);
                entry.SuggestedGroup = group;
                entry.SuggestionReason = reason;
                if (group is not null) newWithSuggestion++;
            }

            db.ProcessCatalogEntries.Add(entry);
            pool.Add(entry);
            existingMap[key] = entry;
            newCount++;
            newNames.Add(item.ProcessName);
        }

        if (newCount > 0 || updatedCount > 0)
            await db.SaveChangesAsync(ct);

        if (newCount > 0)
        {
            var preview = string.Join(", ", newNames.Take(15)) + (newNames.Count > 15 ? ", …" : "");
            await processGroups.AuditCatalogAsync(
                "catalog-new",
                $"Catalog: {newCount} new process(es) identified from {sourceLabel}" +
                (string.IsNullOrWhiteSpace(hostname) ? "" : $" on {hostname}") +
                $" — need classifying ({newWithSuggestion} with a suggested group): [{preview}]",
                hostname, ct);
        }

        return new UpsertSummary(newCount, updatedCount, newNames);
    }

    /// <summary>True when a process has no explicit user classification and defaults to the "unknown" Specialization bucket.</summary>
    public static bool NeedsClassification(string processName, ProcessClassificationContext ctx) =>
        !ctx.UserAssignments.ContainsKey(processName) &&
        ProcessClassification.Classify(processName, ctx).Group == AppGroup.Specialization;

    /// <summary>
    /// Heuristics (kept intentionally simple, documented for the Help page):
    ///  1. Same install folder as another process that already has a confident classification.
    ///  2. Same publisher (CompanyName from file version info) as another confidently-classified process.
    /// Only ever a suggestion — never auto-applied; a human still confirms via the classification CSV or group buttons.
    /// </summary>
    private static (AppGroup? Group, string? Reason) SuggestClassification(
        CatalogItem item, List<ProcessCatalogEntry> pool, ProcessClassificationContext ctx)
    {
        var dir = TryGetDirectory(item.ExecutablePath);
        if (dir is not null)
        {
            var match = pool.FirstOrDefault(p =>
                !string.Equals(p.ProcessName, item.ProcessName, StringComparison.OrdinalIgnoreCase) &&
                string.Equals(TryGetDirectory(p.ExecutablePath), dir, StringComparison.OrdinalIgnoreCase) &&
                ResolvedGroup(p.ProcessName, ctx) is not null);
            if (match is not null)
            {
                var group = ResolvedGroup(match.ProcessName, ctx)!.Value;
                return (group, $"Same install folder as “{match.ProcessName}” ({ProcessClassification.GroupLabel(group)}).");
            }
        }

        if (!string.IsNullOrWhiteSpace(item.CompanyName))
        {
            var match = pool.FirstOrDefault(p =>
                !string.Equals(p.ProcessName, item.ProcessName, StringComparison.OrdinalIgnoreCase) &&
                string.Equals(p.CompanyName, item.CompanyName, StringComparison.OrdinalIgnoreCase) &&
                ResolvedGroup(p.ProcessName, ctx) is not null);
            if (match is not null)
            {
                var group = ResolvedGroup(match.ProcessName, ctx)!.Value;
                return (group, $"Same publisher ({item.CompanyName}) as “{match.ProcessName}” ({ProcessClassification.GroupLabel(group)}).");
            }
        }

        return (null, null);
    }

    /// <summary>A group is "confident" when a human assigned it, or it's a recognised Core Windows / SOE catalog match.</summary>
    private static AppGroup? ResolvedGroup(string processName, ProcessClassificationContext ctx)
    {
        if (ctx.UserAssignments.TryGetValue(processName, out var g))
            return g;
        var classified = ProcessClassification.Classify(processName, ctx).Group;
        return classified == AppGroup.Specialization ? null : classified;
    }

    private static string? TryGetDirectory(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return null;
        try { return Path.GetDirectoryName(path); }
        catch { return null; }
    }

    private static string? NullIfEmpty(string? s) => string.IsNullOrWhiteSpace(s) ? null : s.Trim();

    public Task<int> CountAsync(CancellationToken ct = default) => db.ProcessCatalogEntries.CountAsync(ct);

    public async Task<int> CountNeedingClassificationAsync(CancellationToken ct = default)
    {
        var ctx = await processGroups.BuildContextAsync(ct);
        var names = await db.ProcessCatalogEntries.AsNoTracking()
            .Select(e => e.ProcessName)
            .Distinct()
            .ToListAsync(ct);
        return names.Count(n => NeedsClassification(n, ctx));
    }

    public async Task<IReadOnlyList<ProcessCatalogEntry>> GetForProcessNamesAsync(IEnumerable<string> processNames, CancellationToken ct = default)
    {
        var names = processNames.Select(ConfigService.NormalizeProcessName).Where(n => n.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        if (names.Count == 0) return [];
        return await db.ProcessCatalogEntries.AsNoTracking().Where(e => names.Contains(e.ProcessName)).ToListAsync(ct);
    }

    public Task<List<ProcessCatalogEntry>> GetAllAsync(CancellationToken ct = default) =>
        db.ProcessCatalogEntries.AsNoTracking().OrderBy(e => e.ProcessName).ThenBy(e => e.ExecutablePath).ToListAsync(ct);
}
