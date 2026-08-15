using System.Text.Json;
using Heimdall.Api.Data;
using Heimdall.Shared;
using Heimdall.Shared.Contracts;
using Microsoft.EntityFrameworkCore;

namespace Heimdall.Api.Services;

/// <summary>
/// Specialization discovery review: auto-add new Spec path+exe to a team's primary AppList (by process name
/// for sampling), queue Continue/Ignore on Discovery, reconcile presence, archive when gone fleet-wide,
/// and flag sticky network paths unseen for 12 months.
/// </summary>
public sealed class SpecReviewService(
    HeimdallDbContext db,
    AppListService appLists,
    ProcessCatalogService catalog,
    ProcessGroupService processGroups)
{
    public const string ArchiveSystemKey = "SpecializationArchive";
    public const string ArchiveListName = "Specialization Archive";
    private static readonly TimeSpan StaleNetworkAge = TimeSpan.FromDays(365);

    private static readonly JsonSerializerOptions InventoryJsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    public sealed record HostPresence(string Hostname, string? TeamName, int? TeamId, DateTimeOffset? LastSeenUtc);

    public sealed record ReviewAppRow(
        int ReviewId,
        int? CatalogEntryId,
        string ProcessName,
        string ExecutablePath,
        string? DisplayName,
        int TeamId,
        string TeamName,
        DateTimeOffset CreatedUtc,
        bool AutoAdded,
        IReadOnlyList<HostPresence> Hosts);

    public sealed record UntamedAppRow(
        int CatalogEntryId,
        string ProcessName,
        string ExecutablePath,
        string? DisplayName,
        IReadOnlyList<HostPresence> Hosts);

    public sealed record StaleAlertRow(
        int AlertId,
        int? CatalogEntryId,
        string ProcessName,
        string ExecutablePath,
        string? DisplayName,
        DateTimeOffset LastSeenUtc,
        DateTimeOffset FirstFlaggedUtc);

    /// <summary>After inventory / ingest sightings: enqueue Spec review + auto-add for team machines.</summary>
    public async Task ProcessSightingsAsync(
        string? hostname,
        IEnumerable<ProcessCatalogService.CatalogItem> items,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(hostname))
            return;

        var host = hostname.Trim();
        var machine = await db.Machines.AsNoTracking()
            .FirstOrDefaultAsync(m => m.Hostname == host, ct);
        var teamId = machine?.TeamId;

        var ctx = await processGroups.BuildContextAsync(ct);
        var normalized = items
            .Select(i => (
                ProcessName: ConfigService.NormalizeProcessName(i.ProcessName),
                Path: string.IsNullOrWhiteSpace(i.ExecutablePath) ? "" : i.ExecutablePath.Trim(),
                Display: string.IsNullOrWhiteSpace(i.DisplayName) ? null : i.DisplayName.Trim()))
            .Where(i => i.ProcessName.Length > 0 && DiscoveryCatalogFilter.IsEligible(i.ProcessName, i.Path))
            .GroupBy(i => (i.ProcessName.ToLowerInvariant(), i.Path.ToLowerInvariant()))
            .Select(g => g.First())
            .ToList();

        if (normalized.Count == 0)
            return;

        var names = normalized.Select(n => n.ProcessName).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        var catalogRows = await db.ProcessCatalogEntries.AsNoTracking()
            .Where(e => names.Contains(e.ProcessName))
            .ToListAsync(ct);

        foreach (var item in normalized)
        {
            if (!IsSpecializationCandidate(item.ProcessName, ctx))
                continue;

            var entry = FindCatalogEntry(catalogRows, item.ProcessName, item.Path);
            if (entry is null || entry.Ignored)
                continue;

            if (teamId is int tid)
                await EnsureReviewForTeamAsync(entry, tid, item.Display ?? entry.DisplayName, ct);
            // No-team hosts: visibility only on Spec review "Machines not in a Team" — no auto-add.
        }
    }

    /// <summary>When processes are explicitly classified as Specialization, enqueue for every team that has seen them.</summary>
    public async Task OnClassifiedAsSpecializationAsync(IEnumerable<string> processNames, CancellationToken ct = default)
    {
        var names = processNames
            .Select(ConfigService.NormalizeProcessName)
            .Where(n => n.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (names.Count == 0)
            return;

        var entries = await db.ProcessCatalogEntries.AsNoTracking()
            .Where(e => names.Contains(e.ProcessName) && !e.Ignored)
            .ToListAsync(ct);
        if (entries.Count == 0)
            return;

        var machines = await db.Machines.AsNoTracking()
            .Where(m => m.TeamId != null)
            .Select(m => new { m.Hostname, m.TeamId })
            .ToListAsync(ct);
        var hostToTeam = machines
            .GroupBy(m => m.Hostname, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.First().TeamId!.Value, StringComparer.OrdinalIgnoreCase);

        foreach (var entry in entries)
        {
            var hosts = ProcessCatalogService.GetSeenHostnames(entry);
            var teamIds = hosts
                .Where(h => hostToTeam.ContainsKey(h))
                .Select(h => hostToTeam[h])
                .Distinct()
                .ToList();
            foreach (var teamId in teamIds)
                await EnsureReviewForTeamAsync(entry, teamId, entry.DisplayName, ct);
        }
    }

    public async Task ContinueAsync(int reviewId, CancellationToken ct = default)
    {
        var item = await db.SpecReviewItems.FirstOrDefaultAsync(r => r.Id == reviewId, ct)
            ?? throw new InvalidOperationException("Review item not found.");
        if (item.Status is SpecReviewStatuses.Ignored or SpecReviewStatuses.Archived)
            throw new InvalidOperationException("Cannot continue an ignored/archived review.");

        // Ensure still on primary list (idempotent).
        var primaryId = await GetPrimaryAppListIdAsync(item.TeamId, ct);
        if (primaryId is int listId)
        {
            await appLists.AddEntriesToListAsync(
                listId,
                [(item.ProcessName, item.DisplayName)],
                assignToHostname: null,
                ct);
            item.AutoAddedToPrimaryList = true;
        }

        item.Status = SpecReviewStatuses.Continued;
        item.DecidedUtc = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(ct);
    }

    public async Task IgnoreAsync(int reviewId, CancellationToken ct = default)
    {
        var item = await db.SpecReviewItems.FirstOrDefaultAsync(r => r.Id == reviewId, ct)
            ?? throw new InvalidOperationException("Review item not found.");

        var primaryId = await GetPrimaryAppListIdAsync(item.TeamId, ct);
        if (primaryId is int listId)
            await RemoveProcessFromListAsync(listId, item.ProcessName, ct);

        item.Status = SpecReviewStatuses.Ignored;
        item.DecidedUtc = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(ct);
    }

    public async Task<(IReadOnlyList<ReviewAppRow> Pending, IReadOnlyList<UntamedAppRow> Untamed, IReadOnlyList<StaleAlertRow> Stale)>
        GetReviewPageAsync(CancellationToken ct = default)
    {
        var pending = await db.SpecReviewItems.AsNoTracking()
            .Include(r => r.Team)
            .Where(r => r.Status == SpecReviewStatuses.Pending)
            .OrderBy(r => r.ProcessName)
            .ThenBy(r => r.ExecutablePath)
            .ThenBy(r => r.Team.Name)
            .ToListAsync(ct);

        var machines = await db.Machines.AsNoTracking()
            .Include(m => m.Team)
            .Select(m => new { m.Hostname, m.TeamId, TeamName = m.Team != null ? m.Team.Name : (string?)null })
            .ToListAsync(ct);

        var hostMeta = machines.ToDictionary(
            m => m.Hostname,
            m => (m.TeamId, m.TeamName),
            StringComparer.OrdinalIgnoreCase);

        var catalogIds = pending.Where(p => p.CatalogEntryId is not null).Select(p => p.CatalogEntryId!.Value).Distinct().ToList();
        var names = pending.Select(p => p.ProcessName).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        var catalogRows = await db.ProcessCatalogEntries.AsNoTracking()
            .Where(e => catalogIds.Contains(e.Id) || names.Contains(e.ProcessName))
            .ToListAsync(ct);

        var pendingRows = new List<ReviewAppRow>();
        foreach (var r in pending)
        {
            var entry = r.CatalogEntryId is int cid
                ? catalogRows.FirstOrDefault(e => e.Id == cid)
                : FindCatalogEntry(catalogRows, r.ProcessName, r.ExecutablePath);
            var hosts = BuildHostPresences(entry, hostMeta);
            pendingRows.Add(new ReviewAppRow(
                r.Id,
                entry?.Id ?? r.CatalogEntryId,
                r.ProcessName,
                r.ExecutablePath,
                r.DisplayName ?? entry?.DisplayName,
                r.TeamId,
                r.Team.Name,
                r.CreatedUtc,
                r.AutoAddedToPrimaryList,
                hosts));
        }

        // Machines not in a Team: Spec candidates on hosts with no TeamId — visibility only.
        var ctx = await processGroups.BuildContextAsync(ct);
        var untamedHosts = machines.Where(m => m.TeamId is null).Select(m => m.Hostname)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var untamed = new List<UntamedAppRow>();
        if (untamedHosts.Count > 0)
        {
            var allSpecish = await db.ProcessCatalogEntries.AsNoTracking()
                .Where(e => !e.Ignored && e.ExecutablePath != null)
                .ToListAsync(ct);
            foreach (var e in allSpecish)
            {
                if (!IsSpecializationCandidate(e.ProcessName, ctx))
                    continue;
                var sightings = ProcessCatalogService.GetHostSightingMap(e);
                var hosts = sightings
                    .Where(s => untamedHosts.Contains(s.Key))
                    .Select(s => new HostPresence(s.Key, null, null, s.Value.LastSeenUtc))
                    .OrderBy(h => h.Hostname, StringComparer.OrdinalIgnoreCase)
                    .ToList();
                if (hosts.Count == 0)
                    continue;
                untamed.Add(new UntamedAppRow(e.Id, e.ProcessName, e.ExecutablePath, e.DisplayName, hosts));
            }

            untamed = untamed
                .OrderBy(u => u.ProcessName, StringComparer.OrdinalIgnoreCase)
                .ThenBy(u => u.ExecutablePath, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }

        var stale = (await db.SpecStaleAlerts.AsNoTracking()
                .Where(a => a.ResolvedUtc == null)
                .ToListAsync(ct))
            .OrderBy(a => a.FirstFlaggedUtc)
            .Select(a => new StaleAlertRow(
                a.Id,
                a.CatalogEntryId,
                a.ProcessName,
                a.ExecutablePath,
                a.DisplayName,
                a.LastSeenUtc,
                a.FirstFlaggedUtc))
            .ToList();

        return (pendingRows, untamed, stale);
    }

    public async Task ResolveStaleAlertAsync(int alertId, bool keepSticky, CancellationToken ct = default)
    {
        var alert = await db.SpecStaleAlerts.FirstOrDefaultAsync(a => a.Id == alertId, ct)
            ?? throw new InvalidOperationException("Stale alert not found.");
        if (alert.ResolvedUtc is not null)
            return;

        alert.ResolvedUtc = DateTimeOffset.UtcNow;
        alert.KeepSticky = keepSticky;
        await db.SaveChangesAsync(ct);

        if (!keepSticky)
            await ArchiveGoneEverywhereAsync(alert.ProcessName, alert.ExecutablePath, alert.CatalogEntryId, force: true, ct);
    }

    /// <summary>
    /// Compare latest machine inventories to catalog presence; drop gone hosts (unless network sticky);
    /// archive Spec when unseen on all machines; flag sticky network paths unseen 12 months.
    /// </summary>
    public async Task<string> ReconcilePresenceAsync(CancellationToken ct = default)
    {
        var machines = await db.Machines.AsNoTracking()
            .Where(m => m.DiscoveredInventoryJson != null && m.DiscoveredInventoryJson != "")
            .Select(m => new { m.Hostname, m.DiscoveredInventoryJson, m.InventoryCollectedUtc })
            .ToListAsync(ct);

        var inventoryByHost = new Dictionary<string, HashSet<(string Name, string Path)>>(StringComparer.OrdinalIgnoreCase);
        foreach (var m in machines)
        {
            var set = new HashSet<(string, string)>(NamePathComparer.Instance);
            foreach (var snap in DeserializeInventory(m.DiscoveredInventoryJson))
            {
                var name = ConfigService.NormalizeProcessName(snap.ProcessName);
                var path = string.IsNullOrWhiteSpace(snap.ExecutablePath) ? "" : snap.ExecutablePath.Trim();
                if (name.Length == 0) continue;
                set.Add((name, path));
            }
            inventoryByHost[m.Hostname] = set;
        }

        var ctx = await processGroups.BuildContextAsync(ct);
        var entries = await db.ProcessCatalogEntries.ToListAsync(ct);
        var now = DateTimeOffset.UtcNow;
        var hostsRemoved = 0;
        var archived = 0;
        var staleFlagged = 0;

        foreach (var entry in entries)
        {
            if (entry.Ignored || !IsSpecializationCandidate(entry.ProcessName, ctx))
                continue;

            var sticky = SpecNetworkPath.IsStickyNetworkPath(entry.ExecutablePath);
            var sightings = ProcessCatalogService.GetHostSightingMap(entry).ToDictionary(
                kv => kv.Key,
                kv => kv.Value,
                StringComparer.OrdinalIgnoreCase);

            var changed = false;
            foreach (var host in sightings.Keys.ToList())
            {
                if (!inventoryByHost.TryGetValue(host, out var inv))
                    continue; // no fresh inventory for host — leave presence alone

                var key = (entry.ProcessName, entry.ExecutablePath ?? "");
                if (inv.Contains(key))
                    continue;

                if (sticky)
                    continue; // keep linked

                await catalog.RemoveHostnameAsync(entry.Id, host, ct);
                hostsRemoved++;
                changed = true;
                sightings.Remove(host);
            }

            if (changed)
            {
                // Reload entry state after RemoveHostnameAsync
                await db.Entry(entry).ReloadAsync(ct);
                sightings = ProcessCatalogService.GetHostSightingMap(entry).ToDictionary(
                    kv => kv.Key, kv => kv.Value, StringComparer.OrdinalIgnoreCase);
            }

            if (sightings.Count == 0 && !sticky)
            {
                if (await ArchiveGoneEverywhereAsync(entry.ProcessName, entry.ExecutablePath, entry.Id, force: false, ct))
                    archived++;
                continue;
            }

            if (sticky && sightings.Count > 0)
            {
                var lastSeen = sightings.Values.Max(v => v.LastSeenUtc);
                if (now - lastSeen >= StaleNetworkAge)
                {
                    if (await EnsureStaleAlertAsync(entry, lastSeen, now, ct))
                        staleFlagged++;
                }
            }
        }

        return $"Presence reconcile: {hostsRemoved} host link(s) removed, {archived} archived, {staleFlagged} stale network alert(s).";
    }

    public async Task EnsureArchiveListAsync(CancellationToken ct = default)
    {
        var list = await db.AppLists.FirstOrDefaultAsync(a => a.SystemKey == ArchiveSystemKey, ct);
        if (list is not null)
            return;
        var now = DateTimeOffset.UtcNow;
        db.AppLists.Add(new AppList
        {
            Name = ArchiveListName,
            IsSystem = true,
            SystemKey = ArchiveSystemKey,
            Notes = "Specialization apps removed from active Spec when unseen on all machines (or after stale network review).",
            CreatedUtc = now,
            UpdatedUtc = now
        });
        await db.SaveChangesAsync(ct);
    }

    public async Task SetPrimaryAppListAsync(int teamId, int appListId, CancellationToken ct = default)
    {
        var links = await db.TeamAppListLinks
            .Where(l => l.TeamId == teamId)
            .ToListAsync(ct);
        var target = links.FirstOrDefault(l => l.AppListId == appListId)
            ?? throw new InvalidOperationException("App list is not linked to this team.");
        if (target.IsExcluded)
            throw new InvalidOperationException("Cannot set a do-not-track list as primary.");

        foreach (var link in links)
            link.IsPrimary = link.AppListId == appListId;
        await db.SaveChangesAsync(ct);
    }

    private async Task EnsureReviewForTeamAsync(
        ProcessCatalogEntry entry,
        int teamId,
        string? displayName,
        CancellationToken ct)
    {
        var path = entry.ExecutablePath ?? "";
        var existing = await db.SpecReviewItems
            .FirstOrDefaultAsync(r =>
                r.TeamId == teamId
                && r.ProcessName == entry.ProcessName
                && r.ExecutablePath == path, ct);

        if (existing is not null)
        {
            // Already decided or pending — do not re-offer.
            if (existing.CatalogEntryId is null && entry.Id > 0)
            {
                existing.CatalogEntryId = entry.Id;
                await db.SaveChangesAsync(ct);
            }
            return;
        }

        var autoAdded = false;
        var primaryId = await GetPrimaryAppListIdAsync(teamId, ct);
        if (primaryId is int listId)
        {
            var added = await appLists.AddEntriesToListAsync(
                listId,
                [(entry.ProcessName, displayName ?? entry.DisplayName)],
                assignToHostname: null,
                ct);
            autoAdded = added > 0 || await ListContainsProcessAsync(listId, entry.ProcessName, ct);
        }

        db.SpecReviewItems.Add(new SpecReviewItem
        {
            CatalogEntryId = entry.Id,
            ProcessName = entry.ProcessName,
            ExecutablePath = path,
            DisplayName = displayName ?? entry.DisplayName,
            TeamId = teamId,
            Status = SpecReviewStatuses.Pending,
            CreatedUtc = DateTimeOffset.UtcNow,
            AutoAddedToPrimaryList = autoAdded
        });
        await db.SaveChangesAsync(ct);
    }

    /// <summary>
    /// Team primary for Spec auto-add / Continue. System classification lists (Core Windows / SOE /
    /// Specialization) are never returned — <see cref="AppListService.AddEntriesToListAsync"/> rejects
    /// them, and treating Specialization as the sole linked list used to 500 every inventory ingest
    /// for teams that only have system lists linked (e.g. Energy), poisoning LastSeenUtc via the queue.
    /// </summary>
    private async Task<int?> GetPrimaryAppListIdAsync(int teamId, CancellationToken ct)
    {
        var primary = await db.TeamAppListLinks.AsNoTracking()
            .Where(l => l.TeamId == teamId && !l.IsExcluded && l.IsPrimary && !l.AppList.IsSystem)
            .Select(l => (int?)l.AppListId)
            .FirstOrDefaultAsync(ct);
        if (primary is not null)
            return primary;

        // Fallback: single non-system tracking list → treat as primary.
        var tracking = await db.TeamAppListLinks.AsNoTracking()
            .Where(l => l.TeamId == teamId && !l.IsExcluded && !l.AppList.IsSystem)
            .Select(l => l.AppListId)
            .ToListAsync(ct);
        return tracking.Count == 1 ? tracking[0] : null;
    }

    private async Task<bool> ListContainsProcessAsync(int listId, string processName, CancellationToken ct)
    {
        var proc = ConfigService.NormalizeProcessName(processName);
        return await db.AppListEntries.AsNoTracking()
            .AnyAsync(e => e.AppListId == listId && e.ProcessName == proc, ct);
    }

    private async Task RemoveProcessFromListAsync(int listId, string processName, CancellationToken ct)
    {
        var proc = ConfigService.NormalizeProcessName(processName);
        var list = await db.AppLists.Include(a => a.Entries).FirstOrDefaultAsync(a => a.Id == listId, ct);
        if (list is null || list.IsSystem)
            return;
        var stale = list.Entries.Where(e => string.Equals(e.ProcessName, proc, StringComparison.OrdinalIgnoreCase)).ToList();
        if (stale.Count == 0)
            return;
        db.AppListEntries.RemoveRange(stale);
        list.UpdatedUtc = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(ct);
        await appLists.AuditAsync("updated", $"Removed {proc} from “{list.Name}” (Spec review ignore).", list.Id, list.Name, ct: ct);
    }

    private async Task<bool> ArchiveGoneEverywhereAsync(
        string processName,
        string executablePath,
        int? catalogEntryId,
        bool force,
        CancellationToken ct)
    {
        var proc = ConfigService.NormalizeProcessName(processName);
        var path = executablePath ?? "";

        if (!force && SpecNetworkPath.IsStickyNetworkPath(path))
            return false;

        // Other path variants of the same exe still present → only mark this review archived.
        var siblings = await db.ProcessCatalogEntries.AsNoTracking()
            .Where(e => e.ProcessName == proc && !e.Ignored)
            .ToListAsync(ct);
        var otherStillPresent = siblings.Any(e =>
            !string.Equals(e.ExecutablePath ?? "", path, StringComparison.OrdinalIgnoreCase)
            && (ProcessCatalogService.GetSeenHostnames(e).Count > 0
                || SpecNetworkPath.IsStickyNetworkPath(e.ExecutablePath)));

        var reviews = await db.SpecReviewItems
            .Where(r => r.ProcessName == proc && r.ExecutablePath == path && r.Status != SpecReviewStatuses.Archived)
            .ToListAsync(ct);
        foreach (var r in reviews)
        {
            r.Status = SpecReviewStatuses.Archived;
            r.DecidedUtc = DateTimeOffset.UtcNow;
        }

        if (catalogEntryId is int cid)
        {
            var entry = await db.ProcessCatalogEntries.FirstOrDefaultAsync(e => e.Id == cid, ct);
            // Soft-hide this path instance from discovery noise when fully gone (non-sticky).
            if (entry is not null && !SpecNetworkPath.IsStickyNetworkPath(path))
                entry.Ignored = true;
        }

        if (otherStillPresent)
        {
            await db.SaveChangesAsync(ct);
            return true;
        }

        // No remaining path variants with presence — remove from team lists + Spec → Archive.
        var teamEntries = await db.AppListEntries
            .Include(e => e.AppList)
            .Where(e => e.ProcessName == proc && !e.AppList.IsSystem)
            .ToListAsync(ct);
        if (teamEntries.Count > 0)
        {
            foreach (var group in teamEntries.GroupBy(e => e.AppList))
                group.Key.UpdatedUtc = DateTimeOffset.UtcNow;
            db.AppListEntries.RemoveRange(teamEntries);
        }

        await appLists.RemoveFromSpecializationAsync([proc], ct);

        await EnsureArchiveListAsync(ct);
        var archive = await db.AppLists.Include(a => a.Entries)
            .FirstAsync(a => a.SystemKey == ArchiveSystemKey, ct);
        if (!archive.Entries.Any(e => string.Equals(e.ProcessName, proc, StringComparison.OrdinalIgnoreCase)))
        {
            archive.Entries.Add(new AppListEntry { ProcessName = proc, DisplayName = proc });
            archive.UpdatedUtc = DateTimeOffset.UtcNow;
        }

        await db.SaveChangesAsync(ct);
        await appLists.AuditAsync("spec-archived",
            $"Archived Spec app {proc} @ {path} (unseen on all machines or stale network remove).",
            archive.Id, archive.Name, ct: ct);
        return true;
    }

    private async Task<bool> EnsureStaleAlertAsync(
        ProcessCatalogEntry entry,
        DateTimeOffset lastSeen,
        DateTimeOffset now,
        CancellationToken ct)
    {
        var path = entry.ExecutablePath ?? "";
        var open = await db.SpecStaleAlerts
            .FirstOrDefaultAsync(a =>
                a.ProcessName == entry.ProcessName
                && a.ExecutablePath == path
                && a.ResolvedUtc == null, ct);
        if (open is not null)
        {
            open.LastSeenUtc = lastSeen;
            await db.SaveChangesAsync(ct);
            return false;
        }

        db.SpecStaleAlerts.Add(new SpecStaleAlert
        {
            CatalogEntryId = entry.Id,
            ProcessName = entry.ProcessName,
            ExecutablePath = path,
            DisplayName = entry.DisplayName,
            FirstFlaggedUtc = now,
            LastSeenUtc = lastSeen
        });
        await db.SaveChangesAsync(ct);
        return true;
    }

    private static bool IsSpecializationCandidate(string processName, ProcessClassificationContext ctx)
    {
        var classified = ProcessClassification.Classify(processName, ctx);
        if (classified.Group != AppGroup.Specialization)
            return false;
        // Explicit Spec assignment, or still in the default Spec bucket (needs classification / suggested Spec).
        return true;
    }

    private static ProcessCatalogEntry? FindCatalogEntry(
        IReadOnlyList<ProcessCatalogEntry> rows,
        string processName,
        string path)
    {
        var exact = rows.FirstOrDefault(e =>
            string.Equals(e.ProcessName, processName, StringComparison.OrdinalIgnoreCase)
            && string.Equals(e.ExecutablePath ?? "", path, StringComparison.OrdinalIgnoreCase));
        if (exact is not null)
            return exact;
        return rows.FirstOrDefault(e =>
            string.Equals(e.ProcessName, processName, StringComparison.OrdinalIgnoreCase)
            && string.IsNullOrEmpty(e.ExecutablePath));
    }

    private static IReadOnlyList<HostPresence> BuildHostPresences(
        ProcessCatalogEntry? entry,
        IReadOnlyDictionary<string, (int? TeamId, string? TeamName)> hostMeta)
    {
        if (entry is null)
            return [];
        var sightings = ProcessCatalogService.GetHostSightingMap(entry);
        return sightings
            .Select(s =>
            {
                hostMeta.TryGetValue(s.Key, out var meta);
                return new HostPresence(s.Key, meta.TeamName, meta.TeamId, s.Value.LastSeenUtc);
            })
            .OrderBy(h => h.TeamName ?? "\uFFFF", StringComparer.OrdinalIgnoreCase)
            .ThenBy(h => h.Hostname, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static IEnumerable<InventorySnap> DeserializeInventory(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
            yield break;
        List<InventorySnap>? list = null;
        try
        {
            list = JsonSerializer.Deserialize<List<InventorySnap>>(json, InventoryJsonOptions);
        }
        catch
        {
            yield break;
        }

        if (list is null)
            yield break;
        foreach (var s in list)
            yield return s;
    }

    private sealed class InventorySnap
    {
        public string ProcessName { get; set; } = "";
        public string? ExecutablePath { get; set; }
    }

    private sealed class NamePathComparer : IEqualityComparer<(string Name, string Path)>
    {
        public static readonly NamePathComparer Instance = new();
        public bool Equals((string Name, string Path) x, (string Name, string Path) y) =>
            string.Equals(x.Name, y.Name, StringComparison.OrdinalIgnoreCase)
            && string.Equals(x.Path, y.Path, StringComparison.OrdinalIgnoreCase);
        public int GetHashCode((string Name, string Path) obj) =>
            HashCode.Combine(
                StringComparer.OrdinalIgnoreCase.GetHashCode(obj.Name),
                StringComparer.OrdinalIgnoreCase.GetHashCode(obj.Path ?? ""));
    }
}
