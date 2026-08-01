using Heimdall.Api.Data;
using Heimdall.Shared.Contracts;
using Microsoft.EntityFrameworkCore;

namespace Heimdall.Api.Services;

/// <summary>Persists user overrides for process group membership and bulk list cleanup.</summary>
public sealed class ProcessGroupService(HeimdallDbContext db)
{
    public async Task<ProcessClassificationContext> BuildContextAsync(CancellationToken ct = default)
    {
        var assignments = await db.ProcessGroupAssignments.AsNoTracking().ToListAsync(ct);
        var userMap = assignments.ToDictionary(
            a => a.ProcessName,
            a => a.Group,
            StringComparer.OrdinalIgnoreCase);

        var soe = await db.SoeApps.AsNoTracking().Select(s => s.ProcessName).ToListAsync(ct);
        return new ProcessClassificationContext
        {
            UserAssignments = userMap,
            SoeProcessNames = soe.ToHashSet(StringComparer.OrdinalIgnoreCase)
        };
    }

    /// <summary>Assign one or many processes to a group. User rows win over static catalogs.</summary>
    public async Task<int> AssignGroupsAsync(
        IEnumerable<string> processNames,
        AppGroup targetGroup,
        CancellationToken ct = default)
    {
        var names = processNames
            .Select(ConfigService.NormalizeProcessName)
            .Where(n => n.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (names.Count == 0)
            return 0;

        var now = DateTimeOffset.UtcNow;
        var existingAssignments = await db.ProcessGroupAssignments.ToListAsync(ct);
        var assignmentMap = existingAssignments.ToDictionary(a => a.ProcessName, StringComparer.OrdinalIgnoreCase);
        var soeApps = await db.SoeApps.ToListAsync(ct);
        var soeMap = soeApps.ToDictionary(s => s.ProcessName, StringComparer.OrdinalIgnoreCase);
        var dirty = false;

        foreach (var name in names)
        {
            if (assignmentMap.TryGetValue(name, out var existing))
            {
                if (existing.Group != targetGroup)
                {
                    existing.Group = targetGroup;
                    existing.UpdatedUtc = now;
                    dirty = true;
                }
            }
            else
            {
                db.ProcessGroupAssignments.Add(new ProcessGroupAssignment
                {
                    ProcessName = name,
                    Group = targetGroup,
                    DisplayName = name,
                    UpdatedUtc = now
                });
                assignmentMap[name] = new ProcessGroupAssignment { ProcessName = name, Group = targetGroup };
                dirty = true;
            }

            if (targetGroup == AppGroup.Soe)
            {
                if (!soeMap.ContainsKey(name))
                {
                    var catalogEntry = SoeCatalog.Entries.FirstOrDefault(e =>
                        string.Equals(e.ProcessName, name, StringComparison.OrdinalIgnoreCase));
                    db.SoeApps.Add(new SoeApp
                    {
                        DisplayName = string.IsNullOrEmpty(catalogEntry.ProcessName) ? name : catalogEntry.DisplayName,
                        ProcessName = name,
                        Category = "SOE",
                        Vendor = catalogEntry.Vendor ?? "User"
                    });
                    soeMap[name] = new SoeApp { ProcessName = name, DisplayName = name };
                    dirty = true;
                }
            }
            else if (soeMap.Remove(name, out var soeRow))
            {
                db.SoeApps.Remove(soeRow);
                dirty = true;
            }
        }

        if (dirty)
            await db.SaveChangesAsync(ct);

        await AuditAsync(
            "group-assign",
            $"Assigned {names.Count} process(es) to {ProcessClassification.GroupLabel(targetGroup)}: [{string.Join(", ", names)}]");

        return names.Count;
    }

    private async Task AuditAsync(string action, string detail, int? appListId = null, string? appListName = null,
        ConfigScope? scope = null, string? scopeValue = null, string? hostname = null)
    {
        db.AppListAuditLogs.Add(new AppListAuditLog
        {
            Utc = DateTimeOffset.UtcNow,
            Action = action,
            AppListId = appListId,
            AppListName = appListName,
            Scope = scope,
            ScopeValue = scopeValue,
            MachineHostname = hostname,
            Detail = detail,
            Actor = AppListService.ResolveActor()
        });
        await db.SaveChangesAsync();
    }

    /// <summary>Remove Core Windows and SOE entries from a machine-scoped auto-discovered app list.</summary>
    public async Task<(int Removed, int Remaining)> CleanupDiscoveredListAsync(
        string hostname,
        CancellationToken ct = default)
    {
        var listName = $"Discovered on {hostname}";
        var list = await db.AppLists
            .Include(a => a.Entries)
            .FirstOrDefaultAsync(a => a.Name == listName && a.IsAutoDiscovered, ct);
        if (list is null || list.Entries.Count == 0)
            return (0, 0);

        var ctx = await BuildContextAsync(ct);
        var toRemove = list.Entries
            .Where(e => ProcessClassification.Classify(e.ProcessName, ctx).Group != AppGroup.Specialization)
            .ToList();

        if (toRemove.Count == 0)
            return (0, list.Entries.Count);

        db.AppListEntries.RemoveRange(toRemove);
        list.UpdatedUtc = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(ct);

        await AuditAsync(
            "discovered-cleanup",
            $"Removed {toRemove.Count} Core Windows / SOE entr(y/ies) from “{listName}”; {list.Entries.Count - toRemove.Count} specialization app(s) remain.",
            list.Id,
            listName,
            ConfigScope.Machine,
            hostname,
            hostname);

        return (toRemove.Count, list.Entries.Count - toRemove.Count);
    }
}
