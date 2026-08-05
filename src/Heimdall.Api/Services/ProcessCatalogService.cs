using System.Text.Json;
using Heimdall.Api.Data;
using Heimdall.Shared;
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
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true };

    private sealed class HostSightingRecord
    {
        public DateTimeOffset LastSeenUtc { get; set; }
        public int Count { get; set; }
    }

    public sealed record CatalogItem(
        string ProcessName,
        string? ExecutablePath,
        string? DisplayName,
        string? FileVersion = null,
        string? ProductVersion = null,
        string? CompanyName = null,
        string? FileDescription = null);

    public sealed record UpsertSummary(int NewCount, int UpdatedCount, IReadOnlyList<string> NewProcessNames);

    /// <summary>Unique (ProcessName + ExecutablePath) counts across discovery sources (may overlap).</summary>
    public sealed record DiscoverySourceStats(
        int UniqueFromProcessRuns,
        int UniqueFromInventories,
        int UniqueFromAppLists,
        int UniqueFromAssignments,
        int UniqueCombined,
        int MissingFromCatalog,
        int CatalogBlankPathCount);

    /// <summary>Catalog status for UI: totals, gap vs discovery sources, and path coverage.</summary>
    public sealed record CatalogStatus(
        int TotalCount,
        int UnclassifiedCount,
        int DiscoverySourceCount,
        int MissingFromCatalog,
        int BlankPathCount,
        bool ShowBackfill);

    /// <summary>Count unique processes sitting in discovery tables but not yet in ProcessCatalogEntries.</summary>
    public async Task<DiscoverySourceStats> GetDiscoverySourceStatsAsync(CancellationToken ct = default)
    {
        var keys = await CollectDiscoveryKeysAsync(ct);
        var catalog = await db.ProcessCatalogEntries.AsNoTracking().ToListAsync(ct);
        var missing = CountDiscoveryKeysMissingFromCatalog(keys.Combined, catalog);
        var blankPaths = catalog.Count(e => string.IsNullOrWhiteSpace(e.ExecutablePath));
        return new DiscoverySourceStats(
            keys.ProcessRuns.Count,
            keys.Inventories.Count,
            keys.AppLists.Count,
            keys.Assignments.Count,
            keys.Combined.Count,
            missing,
            blankPaths);
    }

    public async Task<CatalogStatus> GetCatalogStatusAsync(CancellationToken ct = default)
    {
        var total = await CountAsync(ct);
        var unclassified = await CountNeedingClassificationAsync(ct);
        var stats = await GetDiscoverySourceStatsAsync(ct);
        return new CatalogStatus(
            total,
            unclassified,
            stats.UniqueCombined,
            stats.MissingFromCatalog,
            stats.CatalogBlankPathCount,
            stats.MissingFromCatalog > 0);
    }

    public Task<int> CountBlankPathAsync(CancellationToken ct = default) =>
        db.ProcessCatalogEntries.CountAsync(e => e.ExecutablePath == "" || e.ExecutablePath == null, ct);

    /// <summary>
    /// Result of searching ProcessRuns / machine inventories / sibling catalog rows for blank ExecutablePath entries.
    /// </summary>
    public sealed record MissingPathResolveResult(
        int Considered,
        int Filled,
        int Merged,
        int Ambiguous,
        int Unresolved,
        IReadOnlyList<string> HostsNeedingInventory);

    /// <summary>
    /// Fill blank catalog ExecutablePath values by reusing paths already reported by agents
    /// (ProcessRuns, DiscoveredInventoryJson) or present on another catalog row for the same process name.
    /// Unambiguous (single canonical path) → fill or merge into an existing pathed row.
    /// Multiple distinct paths → leave blank (Ambiguous). Returns hostnames that still need a fresh inventory.
    /// </summary>
    public async Task<MissingPathResolveResult> ResolveMissingPathsAsync(
        IEnumerable<int>? catalogIds = null,
        CancellationToken ct = default)
    {
        var idFilter = catalogIds?.ToHashSet();
        var blankQuery = db.ProcessCatalogEntries.Where(e => e.ExecutablePath == "" || e.ExecutablePath == null);
        if (idFilter is { Count: > 0 })
            blankQuery = blankQuery.Where(e => idFilter.Contains(e.Id));

        var blanks = await blankQuery.ToListAsync(ct);
        if (blanks.Count == 0)
            return new MissingPathResolveResult(0, 0, 0, 0, 0, []);

        var names = blanks.Select(e => e.ProcessName).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        var pathIndex = await BuildReportedPathIndexAsync(names, ct);

        var siblings = await db.ProcessCatalogEntries
            .Where(e => names.Contains(e.ProcessName) && e.ExecutablePath != null && e.ExecutablePath != "")
            .ToListAsync(ct);
        var siblingsByName = siblings
            .GroupBy(e => e.ProcessName, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.ToList(), StringComparer.OrdinalIgnoreCase);

        var identityMap = BuildIdentityMap(await db.ProcessCatalogEntries
            .Where(e => names.Contains(e.ProcessName))
            .ToListAsync(ct));

        var filled = 0;
        var merged = 0;
        var ambiguous = 0;
        var unresolvedHosts = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var blank in blanks.ToList())
        {
            pathIndex.TryGetValue(blank.ProcessName, out var candidates);
            candidates ??= [];

            // Include sibling catalog paths as candidates (another row already has a location).
            if (siblingsByName.TryGetValue(blank.ProcessName, out var sibs))
            {
                foreach (var s in sibs)
                {
                    if (ReferenceEquals(s, blank) || string.IsNullOrWhiteSpace(s.ExecutablePath))
                        continue;
                    candidates.Add(new ReportedPath(s.ExecutablePath.Trim(), s.LastSeenUtc, "catalog"));
                }
            }

            var byCanonical = candidates
                .Where(c => !string.IsNullOrWhiteSpace(c.Path))
                .GroupBy(c => CatalogPathNormalizer.Normalize(c.Path), StringComparer.OrdinalIgnoreCase)
                .Where(g => g.Key.Length > 0)
                .ToList();

            if (byCanonical.Count == 0)
            {
                foreach (var host in GetSeenHostnames(blank))
                    unresolvedHosts.Add(host);
                continue;
            }

            if (byCanonical.Count > 1)
            {
                ambiguous++;
                foreach (var host in GetSeenHostnames(blank))
                    unresolvedHosts.Add(host);
                continue;
            }

            var best = byCanonical[0].OrderByDescending(c => c.SeenUtc).First();
            var chosenPath = best.Path.Trim();
            var newKey = IdentityKey(blank.ProcessName, chosenPath);

            if (identityMap.TryGetValue(newKey, out var existing) && !ReferenceEquals(existing, blank))
            {
                if (MergeEntryInto(blank, existing))
                {
                    db.ProcessCatalogEntries.Remove(blank);
                    identityMap.Remove(IdentityKey(blank.ProcessName, blank.ExecutablePath));
                    merged++;
                }
                continue;
            }

            // Prefer merging into a sibling that shares the canonical path even if raw path differs slightly.
            if (siblingsByName.TryGetValue(blank.ProcessName, out var pathSiblings))
            {
                var canon = CatalogPathNormalizer.Normalize(chosenPath);
                var siblingMatch = pathSiblings
                    .Where(s => !ReferenceEquals(s, blank)
                                && string.Equals(CatalogPathNormalizer.Normalize(s.ExecutablePath), canon, StringComparison.OrdinalIgnoreCase))
                    .OrderByDescending(s => s.LastSeenUtc)
                    .FirstOrDefault();
                if (siblingMatch is not null)
                {
                    if (MergeEntryInto(blank, siblingMatch))
                    {
                        db.ProcessCatalogEntries.Remove(blank);
                        identityMap.Remove(IdentityKey(blank.ProcessName, ""));
                        merged++;
                    }
                    continue;
                }
            }

            var oldKey = IdentityKey(blank.ProcessName, blank.ExecutablePath);
            identityMap.Remove(oldKey);
            blank.ExecutablePath = chosenPath;
            identityMap[newKey] = blank;
            filled++;
        }

        if (filled + merged > 0)
        {
            var extraMerged = await MergeDuplicateCatalogEntriesAsync(ct);
            merged += extraMerged;
            await db.SaveChangesAsync(ct);
        }

        var stillBlank = await blankQuery.CountAsync(ct);
        // Recompute unresolved hosts from remaining blanks when we filled some.
        if (filled + merged > 0)
        {
            unresolvedHosts.Clear();
            var remaining = await blankQuery.AsNoTracking().ToListAsync(ct);
            foreach (var e in remaining)
            {
                pathIndex.TryGetValue(e.ProcessName, out var cands);
                var hasAny = cands is { Count: > 0 } && cands.Any(c => !string.IsNullOrWhiteSpace(c.Path));
                if (!hasAny || (cands!.GroupBy(c => CatalogPathNormalizer.Normalize(c.Path)).Count(g => g.Key.Length > 0) != 1))
                {
                    foreach (var host in GetSeenHostnames(e))
                        unresolvedHosts.Add(host);
                }
            }
        }

        return new MissingPathResolveResult(
            blanks.Count,
            filled,
            merged,
            ambiguous,
            stillBlank,
            unresolvedHosts.OrderBy(h => h, StringComparer.OrdinalIgnoreCase).ToList());
    }

    private sealed record ReportedPath(string Path, DateTimeOffset SeenUtc, string Source);

    private async Task<Dictionary<string, List<ReportedPath>>> BuildReportedPathIndexAsync(
        IReadOnlyList<string> processNames,
        CancellationToken ct)
    {
        var index = new Dictionary<string, List<ReportedPath>>(StringComparer.OrdinalIgnoreCase);
        if (processNames.Count == 0)
            return index;

        void Add(string rawName, string? rawPath, DateTimeOffset seenUtc, string source)
        {
            var name = ConfigService.NormalizeProcessName(rawName);
            if (name.Length == 0 || string.IsNullOrWhiteSpace(rawPath))
                return;
            if (!index.TryGetValue(name, out var list))
            {
                list = [];
                index[name] = list;
            }
            list.Add(new ReportedPath(rawPath.Trim(), seenUtc, source));
        }

        var nameSet = processNames.ToHashSet(StringComparer.OrdinalIgnoreCase);

        // Load path-bearing runs and filter in memory so casing differences still match.
        var runs = await db.ProcessRuns.AsNoTracking()
            .Where(r => r.ExecutablePath != null && r.ExecutablePath != "")
            .Select(r => new { r.ProcessName, r.ExecutablePath, r.LastSeenAtUtc })
            .ToListAsync(ct);
        foreach (var r in runs)
        {
            if (!nameSet.Contains(ConfigService.NormalizeProcessName(r.ProcessName)))
                continue;
            Add(r.ProcessName, r.ExecutablePath, r.LastSeenAtUtc, "process-run");
        }

        var machines = await db.Machines.AsNoTracking()
            .Where(m => m.DiscoveredInventoryJson != null && m.DiscoveredInventoryJson != "")
            .Select(m => new { m.DiscoveredInventoryJson, m.LastSeenUtc })
            .ToListAsync(ct);
        foreach (var m in machines)
        {
            var seen = m.LastSeenUtc == default ? DateTimeOffset.UtcNow : m.LastSeenUtc;
            foreach (var snap in DeserializeInventorySnapshot(m.DiscoveredInventoryJson))
            {
                if (!nameSet.Contains(ConfigService.NormalizeProcessName(snap.ProcessName)))
                    continue;
                Add(snap.ProcessName, snap.ExecutablePath, seen, "inventory");
            }
        }

        return index;
    }

    /// <summary>
    /// Upsert catalog rows from all historical discovery sources: ProcessRuns, machine inventory JSON,
    /// AppList entries, and ProcessGroupAssignments. Safe to run repeatedly (idempotent merge).
    /// </summary>
    public async Task<UpsertSummary> BackfillFromDiscoveriesAsync(CancellationToken ct = default)
    {
        var batches = await CollectDiscoveryBatchesAsync(ct);
        var totalNew = 0;
        var totalUpdated = 0;
        var allNewNames = new List<string>();

        foreach (var (hostname, items) in batches)
        {
            if (items.Count == 0) continue;
            var result = await UpsertAsync(items, hostname, "discovery backfill", incrementSeenOnUpdate: false, ct);
            totalNew += result.NewCount;
            totalUpdated += result.UpdatedCount;
            allNewNames.AddRange(result.NewProcessNames);
        }

        var merged = await MergeDuplicateCatalogEntriesAsync(ct);
        if (merged > 0)
        {
            totalUpdated += merged;
            await db.SaveChangesAsync(ct);
        }

        return new UpsertSummary(totalNew, totalUpdated, allNewNames);
    }

    /// <summary>Insert/refresh catalog rows for a batch of observed processes; flags + logs newly-identified ones.</summary>
    public Task<UpsertSummary> UpsertAsync(
        IEnumerable<CatalogItem> items,
        string? hostname,
        string sourceLabel,
        CancellationToken ct = default) =>
        UpsertAsync(items, hostname, sourceLabel, incrementSeenOnUpdate: true, ct);

    public async Task<UpsertSummary> UpsertAsync(
        IEnumerable<CatalogItem> items,
        string? hostname,
        string sourceLabel,
        bool incrementSeenOnUpdate,
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
        var existingMap = BuildIdentityMap(existingForNames);
        var canonicalMap = BuildCanonicalMap(existingForNames);

        var ctx = await processGroups.BuildContextAsync(ct);
        // Pool used for "similar item already classified" suggestions — POC-scale table, safe to load in full.
        var pool = await db.ProcessCatalogEntries.AsNoTracking().ToListAsync(ct);

        var newCount = 0;
        var updatedCount = 0;
        var newNames = new List<string>();
        var newWithSuggestion = 0;

        foreach (var item in normalized)
        {
            var row = ResolveExistingRow(item, existingForNames, existingMap, canonicalMap);
            if (row is not null)
            {
                row.LastSeenUtc = now;
                if (incrementSeenOnUpdate)
                    row.SeenCount++;
                RecordHostname(row, hostname, now, incrementSeenOnUpdate);
                TryFillExecutablePath(row, item.ExecutablePath, existingMap, canonicalMap);
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
            RecordHostname(entry, hostname, now, incrementSeenCount: true);

            if (NeedsClassification(item.ProcessName, ctx))
            {
                var (group, reason) = SuggestClassification(item, pool, ctx);
                entry.SuggestedGroup = group;
                entry.SuggestionReason = reason;
                if (group is not null) newWithSuggestion++;
            }

            db.ProcessCatalogEntries.Add(entry);
            pool.Add(entry);
            existingForNames.Add(entry);
            RegisterEntry(entry, existingMap, canonicalMap);
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
            .Where(e => !e.Ignored)
            .Select(e => e.ProcessName)
            .Distinct()
            .ToListAsync(ct);
        return names.Count(n => NeedsClassification(n, ctx));
    }

    /// <summary>Hostnames that have reported this catalog entry (from SeenHostnamesJson).</summary>
    public static IReadOnlyList<string> GetSeenHostnames(ProcessCatalogEntry entry) =>
        DeserializeHostSightings(entry.SeenHostnamesJson).Keys.OrderBy(h => h, StringComparer.OrdinalIgnoreCase).ToList();

    private static void RecordHostname(ProcessCatalogEntry row, string? hostname, DateTimeOffset now, bool incrementSeenCount)
    {
        if (string.IsNullOrWhiteSpace(hostname)) return;
        row.LastSeenHostname = hostname.Trim();
        if (string.IsNullOrWhiteSpace(row.FirstSeenHostname))
            row.FirstSeenHostname = hostname.Trim();

        var sightings = DeserializeHostSightings(row.SeenHostnamesJson);
        var key = hostname.Trim();
        if (sightings.TryGetValue(key, out var existing))
        {
            existing.LastSeenUtc = now;
            if (incrementSeenCount)
                existing.Count++;
        }
        else
            sightings[key] = new HostSightingRecord { LastSeenUtc = now, Count = 1 };

        row.SeenHostnamesJson = SerializeHostSightings(sightings);
    }

    private static Dictionary<string, HostSightingRecord> DeserializeHostSightings(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return new Dictionary<string, HostSightingRecord>(StringComparer.OrdinalIgnoreCase);
        try
        {
            return JsonSerializer.Deserialize<Dictionary<string, HostSightingRecord>>(json, JsonOptions)
                   ?? new Dictionary<string, HostSightingRecord>(StringComparer.OrdinalIgnoreCase);
        }
        catch
        {
            return new Dictionary<string, HostSightingRecord>(StringComparer.OrdinalIgnoreCase);
        }
    }

    private static string SerializeHostSightings(Dictionary<string, HostSightingRecord> sightings) =>
        JsonSerializer.Serialize(sightings, JsonOptions);

    public async Task<IReadOnlyList<ProcessCatalogEntry>> GetForProcessNamesAsync(IEnumerable<string> processNames, CancellationToken ct = default)
    {
        var names = processNames.Select(ConfigService.NormalizeProcessName).Where(n => n.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        if (names.Count == 0) return [];
        return await db.ProcessCatalogEntries.AsNoTracking().Where(e => names.Contains(e.ProcessName)).ToListAsync(ct);
    }

    public Task<List<ProcessCatalogEntry>> GetAllAsync(CancellationToken ct = default) =>
        db.ProcessCatalogEntries.AsNoTracking().OrderBy(e => e.ProcessName).ThenBy(e => e.ExecutablePath).ToListAsync(ct);

    /// <summary>
    /// Apply Description/Category/Subcategory/DisplayName/SuggestedGroup from a classification CSV import
    /// onto matching catalog rows. Uses the same identity resolution as Upsert (exact → canonical → blank-path),
    /// and when the CSV path is empty, updates every catalog row for that process name.
    /// </summary>
    public async Task<int> ApplyImportMetadataAsync(IEnumerable<ProcessGroupService.CsvImportedRow> rows, CancellationToken ct = default)
    {
        var list = rows.ToList();
        if (list.Count == 0)
            return 0;

        var names = list.Select(r => r.ProcessName).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        var entries = await db.ProcessCatalogEntries.Where(e => names.Contains(e.ProcessName)).ToListAsync(ct);
        var byName = entries
            .GroupBy(e => e.ProcessName, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.ToList(), StringComparer.OrdinalIgnoreCase);
        var identityMap = BuildIdentityMap(entries);
        var canonicalMap = BuildCanonicalMap(entries);
        var updated = 0;

        foreach (var row in list)
        {
            if (!byName.TryGetValue(row.ProcessName, out var existingForNames) || existingForNames.Count == 0)
                continue;

            var item = new CatalogItem(row.ProcessName, row.ExecutablePath, row.DisplayName);
            var resolved = ResolveExistingRow(item, existingForNames, identityMap, canonicalMap);

            List<ProcessCatalogEntry> targets;
            if (string.IsNullOrWhiteSpace(row.ExecutablePath))
            {
                // Name-only CSV rows: stamp metadata onto every path variant of that process.
                targets = existingForNames;
            }
            else if (resolved is not null)
            {
                targets = [resolved];
            }
            else
            {
                // Path in CSV didn't resolve (e.g. DriverStore hash skew before canonical match) —
                // still apply classification metadata to every catalog row for that process name.
                targets = existingForNames;
            }

            foreach (var entry in targets)
            {
                var changed = false;
                if (row.DisplayName is not null && !string.Equals(entry.DisplayName, row.DisplayName, StringComparison.Ordinal))
                {
                    entry.DisplayName = row.DisplayName;
                    changed = true;
                }
                if (row.Description is not null && !string.Equals(entry.Description, row.Description, StringComparison.Ordinal))
                {
                    entry.Description = row.Description;
                    changed = true;
                }
                if (row.Category is not null && !string.Equals(entry.Category, row.Category, StringComparison.Ordinal))
                {
                    entry.Category = row.Category;
                    changed = true;
                }
                if (row.Subcategory is not null && !string.Equals(entry.Subcategory, row.Subcategory, StringComparison.Ordinal))
                {
                    entry.Subcategory = row.Subcategory;
                    changed = true;
                }
                if (row.Group is not null && entry.SuggestedGroup != row.Group)
                {
                    entry.SuggestedGroup = row.Group;
                    entry.SuggestionReason = "Imported classification CSV";
                    changed = true;
                }
                if (changed)
                    updated++;
            }
        }

        if (updated > 0)
            await db.SaveChangesAsync(ct);

        return updated;
    }

    /// <summary>Clear pending suggestions after a human Approve/Set.</summary>
    public async Task ClearSuggestionsAsync(IEnumerable<string> processNames, CancellationToken ct = default)
    {
        var names = processNames
            .Select(ConfigService.NormalizeProcessName)
            .Where(n => n.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (names.Count == 0) return;

        var entries = await db.ProcessCatalogEntries
            .Where(e => names.Contains(e.ProcessName) && (e.SuggestedGroup != null || e.SuggestionReason != null))
            .ToListAsync(ct);
        if (entries.Count == 0) return;

        foreach (var e in entries)
        {
            e.SuggestedGroup = null;
            e.SuggestionReason = null;
        }
        await db.SaveChangesAsync(ct);
    }

    private sealed record DiscoveryKeySets(
        HashSet<(string Name, string Path)> ProcessRuns,
        HashSet<(string Name, string Path)> Inventories,
        HashSet<(string Name, string Path)> AppLists,
        HashSet<(string Name, string Path)> Assignments,
        HashSet<(string Name, string Path)> Combined);

    private async Task<DiscoveryKeySets> CollectDiscoveryKeysAsync(CancellationToken ct)
    {
        var processRuns = new HashSet<(string, string)>(KeyComparer);
        var inventories = new HashSet<(string, string)>(KeyComparer);
        var appLists = new HashSet<(string, string)>(KeyComparer);
        var assignments = new HashSet<(string, string)>(KeyComparer);
        var combined = new HashSet<(string, string)>(KeyComparer);

        var runs = await db.ProcessRuns.AsNoTracking()
            .Select(r => new { r.ProcessName, r.ExecutablePath })
            .ToListAsync(ct);
        foreach (var r in runs)
            AddKey(processRuns, combined, r.ProcessName, r.ExecutablePath);

        var machines = await db.Machines.AsNoTracking()
            .Where(m => m.DiscoveredInventoryJson != null && m.DiscoveredInventoryJson != "")
            .Select(m => m.DiscoveredInventoryJson)
            .ToListAsync(ct);
        foreach (var json in machines)
        {
            foreach (var snap in DeserializeInventorySnapshot(json))
                AddKey(inventories, combined, snap.ProcessName, snap.ExecutablePath);
        }

        var listEntries = await db.AppListEntries.AsNoTracking()
            .Select(e => new { e.ProcessName, e.DisplayName })
            .ToListAsync(ct);
        foreach (var e in listEntries)
            AddKey(appLists, combined, e.ProcessName, null, e.DisplayName);

        var assigned = await db.ProcessGroupAssignments.AsNoTracking()
            .Select(a => new { a.ProcessName, a.DisplayName })
            .ToListAsync(ct);
        foreach (var a in assigned)
            AddKey(assignments, combined, a.ProcessName, null, a.DisplayName);

        return new DiscoveryKeySets(processRuns, inventories, appLists, assignments, combined);
    }

    private async Task<List<(string? Hostname, List<CatalogItem> Items)>> CollectDiscoveryBatchesAsync(CancellationToken ct)
    {
        var byHost = new Dictionary<string, List<CatalogItem>>(StringComparer.OrdinalIgnoreCase);
        const string GlobalKey = "";

        var runs = await db.ProcessRuns.AsNoTracking()
            .Select(r => new { r.ProcessName, r.ExecutablePath, Hostname = r.Machine.Hostname })
            .ToListAsync(ct);
        foreach (var r in runs)
            AddItem(byHost, r.Hostname, r.ProcessName, r.ExecutablePath);

        var machines = await db.Machines.AsNoTracking()
            .Where(m => m.DiscoveredInventoryJson != null && m.DiscoveredInventoryJson != "")
            .Select(m => new { m.Hostname, m.DiscoveredInventoryJson })
            .ToListAsync(ct);
        foreach (var m in machines)
        {
            foreach (var snap in DeserializeInventorySnapshot(m.DiscoveredInventoryJson))
                AddItem(byHost, m.Hostname, snap.ProcessName, snap.ExecutablePath, snap.DisplayName);
        }

        var listEntries = await db.AppListEntries.AsNoTracking()
            .Select(e => new { e.ProcessName, e.DisplayName })
            .ToListAsync(ct);
        foreach (var e in listEntries)
            AddItem(byHost, GlobalKey, e.ProcessName, null, e.DisplayName);

        var assigned = await db.ProcessGroupAssignments.AsNoTracking()
            .Select(a => new { a.ProcessName, a.DisplayName })
            .ToListAsync(ct);
        foreach (var a in assigned)
            AddItem(byHost, GlobalKey, a.ProcessName, null, a.DisplayName);

        return byHost.Select(kv => (string.IsNullOrEmpty(kv.Key) ? null : kv.Key, kv.Value)).ToList();
    }

    private static void AddKey(
        HashSet<(string Name, string Path)> target,
        HashSet<(string Name, string Path)> combined,
        string rawName,
        string? rawPath,
        string? _ = null)
    {
        var name = ConfigService.NormalizeProcessName(rawName);
        if (name.Length == 0) return;
        var path = string.IsNullOrWhiteSpace(rawPath) ? "" : rawPath.Trim();
        var key = (name, path);
        target.Add(key);
        combined.Add(key);
    }

    private static void AddItem(
        Dictionary<string, List<CatalogItem>> byHost,
        string hostKey,
        string rawName,
        string? rawPath,
        string? displayName = null)
    {
        var name = ConfigService.NormalizeProcessName(rawName);
        if (name.Length == 0) return;
        var path = string.IsNullOrWhiteSpace(rawPath) ? null : rawPath.Trim();
        var display = string.IsNullOrWhiteSpace(displayName) ? null : displayName.Trim();
        if (!byHost.TryGetValue(hostKey, out var list))
        {
            list = [];
            byHost[hostKey] = list;
        }
        list.Add(new CatalogItem(name, path, display));
    }

    private static List<InventorySnapshotRow> DeserializeInventorySnapshot(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return [];
        try
        {
            return JsonSerializer.Deserialize<List<InventorySnapshotRow>>(json, JsonOptions) ?? [];
        }
        catch
        {
            return [];
        }
    }

    private sealed record InventorySnapshotRow(
        string ProcessName,
        string? DisplayName,
        string? ExecutablePath,
        string? Source);

    private static Dictionary<(string Name, string Path), ProcessCatalogEntry> BuildIdentityMap(IEnumerable<ProcessCatalogEntry> entries)
    {
        var map = new Dictionary<(string, string), ProcessCatalogEntry>(KeyComparer);
        foreach (var e in entries)
            map[IdentityKey(e.ProcessName, e.ExecutablePath)] = e;
        return map;
    }

    private static Dictionary<(string Name, string CanonicalPath), ProcessCatalogEntry> BuildCanonicalMap(IEnumerable<ProcessCatalogEntry> entries)
    {
        var map = new Dictionary<(string, string), ProcessCatalogEntry>(CanonicalKeyComparer);
        foreach (var e in entries)
        {
            if (string.IsNullOrWhiteSpace(e.ExecutablePath)) continue;
            var canonical = CatalogPathNormalizer.Normalize(e.ExecutablePath);
            if (canonical.Length == 0) continue;
            map[(e.ProcessName.ToLowerInvariant(), canonical)] = e;
        }
        return map;
    }

    private static void RegisterEntry(
        ProcessCatalogEntry entry,
        Dictionary<(string Name, string Path), ProcessCatalogEntry> identityMap,
        Dictionary<(string Name, string CanonicalPath), ProcessCatalogEntry> canonicalMap)
    {
        identityMap[IdentityKey(entry.ProcessName, entry.ExecutablePath)] = entry;
        if (!string.IsNullOrWhiteSpace(entry.ExecutablePath))
        {
            var canonical = CatalogPathNormalizer.Normalize(entry.ExecutablePath);
            if (canonical.Length > 0)
                canonicalMap[(entry.ProcessName.ToLowerInvariant(), canonical)] = entry;
        }
    }

    private static (string Name, string Path) IdentityKey(string processName, string? executablePath) =>
        (ConfigService.NormalizeProcessName(processName).ToLowerInvariant(),
         string.IsNullOrWhiteSpace(executablePath) ? "" : executablePath.Trim().ToLowerInvariant());

    /// <summary>
    /// Match order: exact name+path → same name + canonical path (volatile folders) → blank-path coalesce.
    /// </summary>
    private static ProcessCatalogEntry? ResolveExistingRow(
        CatalogItem item,
        List<ProcessCatalogEntry> existingForNames,
        Dictionary<(string Name, string Path), ProcessCatalogEntry> identityMap,
        Dictionary<(string Name, string CanonicalPath), ProcessCatalogEntry> canonicalMap)
    {
        if (identityMap.TryGetValue(IdentityKey(item.ProcessName, item.ExecutablePath), out var exact))
            return exact;

        if (!string.IsNullOrWhiteSpace(item.ExecutablePath))
        {
            var canonical = CatalogPathNormalizer.Normalize(item.ExecutablePath);
            if (canonical.Length > 0 &&
                canonicalMap.TryGetValue((item.ProcessName.ToLowerInvariant(), canonical), out var byCanonical))
                return byCanonical;

            if (identityMap.TryGetValue((item.ProcessName.ToLowerInvariant(), ""), out var blank))
                return blank;
        }
        else
        {
            var pathed = existingForNames
                .Where(e => string.Equals(e.ProcessName, item.ProcessName, StringComparison.OrdinalIgnoreCase)
                            && !string.IsNullOrWhiteSpace(e.ExecutablePath))
                .ToList();
            if (pathed.Count == 1)
                return pathed[0];

            if (identityMap.TryGetValue((item.ProcessName.ToLowerInvariant(), ""), out var blank))
                return blank;
        }

        return null;
    }

    private static void TryFillExecutablePath(
        ProcessCatalogEntry row,
        string? incomingPath,
        Dictionary<(string Name, string Path), ProcessCatalogEntry> identityMap,
        Dictionary<(string Name, string CanonicalPath), ProcessCatalogEntry> canonicalMap)
    {
        if (string.IsNullOrWhiteSpace(incomingPath) || !string.IsNullOrWhiteSpace(row.ExecutablePath))
            return;

        var trimmed = incomingPath.Trim();
        var newKey = IdentityKey(row.ProcessName, trimmed);
        if (identityMap.TryGetValue(newKey, out var conflict) && !ReferenceEquals(conflict, row))
            return;

        var oldKey = IdentityKey(row.ProcessName, row.ExecutablePath);
        identityMap.Remove(oldKey);
        row.ExecutablePath = trimmed;
        identityMap[newKey] = row;

        var canonical = CatalogPathNormalizer.Normalize(trimmed);
        if (canonical.Length > 0)
            canonicalMap[(row.ProcessName.ToLowerInvariant(), canonical)] = row;
    }

    /// <summary>Merge blank-path and canonical-path duplicates left from before normalization.</summary>
    public async Task<int> MergeDuplicateCatalogEntriesAsync(CancellationToken ct = default)
    {
        var entries = await db.ProcessCatalogEntries.ToListAsync(ct);
        var merged = 0;

        foreach (var group in entries.GroupBy(e => e.ProcessName, StringComparer.OrdinalIgnoreCase))
        {
            var rows = group.ToList();
            var blank = rows.Where(e => string.IsNullOrWhiteSpace(e.ExecutablePath)).ToList();
            var pathed = rows.Where(e => !string.IsNullOrWhiteSpace(e.ExecutablePath)).ToList();

            if (blank.Count > 0 && pathed.Count == 1)
            {
                var keeper = pathed[0];
                foreach (var b in blank)
                {
                    if (MergeEntryInto(b, keeper))
                    {
                        db.ProcessCatalogEntries.Remove(b);
                        entries.Remove(b);
                        merged++;
                    }
                }
            }

            pathed = rows.Where(e => !string.IsNullOrWhiteSpace(e.ExecutablePath) && entries.Contains(e)).ToList();
            foreach (var canonGroup in pathed
                         .GroupBy(e => CatalogPathNormalizer.Normalize(e.ExecutablePath), StringComparer.OrdinalIgnoreCase)
                         .Where(g => g.Key.Length > 0 && g.Count() > 1))
            {
                var keeper = canonGroup.OrderBy(e => e.FirstSeenUtc).First();
                foreach (var dup in canonGroup.Where(e => e.Id != keeper.Id))
                {
                    if (MergeEntryInto(dup, keeper))
                    {
                        db.ProcessCatalogEntries.Remove(dup);
                        entries.Remove(dup);
                        merged++;
                    }
                }
            }
        }

        return merged;
    }

    private static bool MergeEntryInto(ProcessCatalogEntry source, ProcessCatalogEntry target)
    {
        if (source.Id == target.Id)
            return false;

        target.SeenCount += source.SeenCount;
        if (source.FirstSeenUtc < target.FirstSeenUtc)
        {
            target.FirstSeenUtc = source.FirstSeenUtc;
            target.FirstSeenHostname ??= source.FirstSeenHostname;
        }
        if (source.LastSeenUtc > target.LastSeenUtc)
        {
            target.LastSeenUtc = source.LastSeenUtc;
            target.LastSeenHostname = source.LastSeenHostname ?? target.LastSeenHostname;
        }

        if (string.IsNullOrWhiteSpace(target.DisplayName) && !string.IsNullOrWhiteSpace(source.DisplayName))
            target.DisplayName = source.DisplayName;
        target.FileVersion ??= source.FileVersion;
        target.ProductVersion ??= source.ProductVersion;
        target.CompanyName ??= source.CompanyName;
        target.FileDescription ??= source.FileDescription;
        target.ManualVersion ??= source.ManualVersion;
        if (string.IsNullOrWhiteSpace(target.Description) && !string.IsNullOrWhiteSpace(source.Description))
            target.Description = source.Description;
        target.Category ??= source.Category;
        target.Subcategory ??= source.Subcategory;
        target.SuggestedGroup ??= source.SuggestedGroup;
        target.SuggestionReason ??= source.SuggestionReason;

        var targetSightings = DeserializeHostSightings(target.SeenHostnamesJson);
        foreach (var (host, sighting) in DeserializeHostSightings(source.SeenHostnamesJson))
        {
            if (targetSightings.TryGetValue(host, out var existing))
            {
                existing.Count += sighting.Count;
                if (sighting.LastSeenUtc > existing.LastSeenUtc)
                    existing.LastSeenUtc = sighting.LastSeenUtc;
            }
            else
                targetSightings[host] = sighting;
        }
        target.SeenHostnamesJson = SerializeHostSightings(targetSightings);

        return true;
    }

    private static int CountDiscoveryKeysMissingFromCatalog(
        HashSet<(string Name, string Path)> discoveryKeys,
        IReadOnlyList<ProcessCatalogEntry> catalog)
    {
        var identityMap = BuildIdentityMap(catalog);
        var canonicalMap = BuildCanonicalMap(catalog);
        var byName = catalog.GroupBy(e => e.ProcessName, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.ToList(), StringComparer.OrdinalIgnoreCase);

        var missing = 0;
        foreach (var key in discoveryKeys)
        {
            if (identityMap.ContainsKey(key))
                continue;

            if (key.Path.Length > 0)
            {
                var canonical = CatalogPathNormalizer.Normalize(key.Path);
                if (canonical.Length > 0 && canonicalMap.ContainsKey((key.Name.ToLowerInvariant(), canonical)))
                    continue;
            }
            else if (byName.TryGetValue(key.Name, out var rows))
            {
                var pathed = rows.Where(e => !string.IsNullOrWhiteSpace(e.ExecutablePath)).ToList();
                if (pathed.Count == 1)
                    continue;
                if (rows.Any(e => string.IsNullOrWhiteSpace(e.ExecutablePath)))
                    continue;
            }

            missing++;
        }

        return missing;
    }

    private static readonly IEqualityComparer<(string Name, string Path)> KeyComparer =
        EqualityComparer<(string, string)>.Create((a, b) =>
            string.Equals(a.Item1, b.Item1, StringComparison.OrdinalIgnoreCase) &&
            string.Equals(a.Item2, b.Item2, StringComparison.OrdinalIgnoreCase),
            t => HashCode.Combine(t.Item1.ToLowerInvariant(), t.Item2.ToLowerInvariant()));

    private static readonly IEqualityComparer<(string Name, string CanonicalPath)> CanonicalKeyComparer =
        EqualityComparer<(string, string)>.Create((a, b) =>
            string.Equals(a.Item1, b.Item1, StringComparison.OrdinalIgnoreCase) &&
            string.Equals(a.Item2, b.Item2, StringComparison.OrdinalIgnoreCase),
            t => HashCode.Combine(t.Item1.ToLowerInvariant(), t.Item2));
}
