using System.Text;
using Heimdall.Api.Data;
using Heimdall.Shared.Contracts;
using Microsoft.EntityFrameworkCore;

namespace Heimdall.Api.Services;

/// <summary>Persists user overrides for process group membership and bulk list cleanup.</summary>
public sealed class ProcessGroupService(HeimdallDbContext db)
{
    public const string CsvHeader = "ProcessName,ExecutablePath,Group,Description,DisplayName";
    private const int MaxImportRows = 50_000;

    public record CsvExportRow(
        string ProcessName,
        string? ExecutablePath,
        AppGroup Group,
        string? Description,
        string DisplayName);

    public record CsvImportResult(int Updated, int Skipped, IReadOnlyList<string> Errors);
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

    /// <summary>Export classified processes for a machine's discovered inventory.</summary>
    public async Task<IReadOnlyList<CsvExportRow>> BuildMachineExportRowsAsync(string hostname, CancellationToken ct = default)
    {
        var machine = await db.Machines.AsNoTracking().FirstOrDefaultAsync(m => m.Hostname == hostname, ct);
        if (machine is null)
            return [];

        var ctx = await BuildContextAsync(ct);
        var assignments = await db.ProcessGroupAssignments.AsNoTracking().ToDictionaryAsync(a => a.ProcessName, StringComparer.OrdinalIgnoreCase, ct);
        var knownApps = await db.KnownApps.AsNoTracking().ToDictionaryAsync(k => k.ProcessName, StringComparer.OrdinalIgnoreCase, ct);
        var soeApps = await db.SoeApps.AsNoTracking().ToDictionaryAsync(s => s.ProcessName, StringComparer.OrdinalIgnoreCase, ct);

        var map = new Dictionary<string, (string? Path, string DisplayName)>(StringComparer.OrdinalIgnoreCase);

        void AddPath(string processName, string? path, string displayName)
        {
            var name = ConfigService.NormalizeProcessName(processName);
            if (name.Length == 0) return;
            if (map.TryGetValue(name, out var existing))
            {
                if (string.IsNullOrWhiteSpace(existing.Path) && !string.IsNullOrWhiteSpace(path))
                    map[name] = (path.Trim(), existing.DisplayName);
            }
            else
            {
                map[name] = (string.IsNullOrWhiteSpace(path) ? null : path.Trim(), displayName);
            }
        }

        var fromRuns = await db.ProcessRuns.AsNoTracking()
            .Where(r => r.MachineId == machine.Id)
            .Select(r => new { r.ProcessName, r.ExecutablePath })
            .ToListAsync(ct);
        foreach (var r in fromRuns)
            AddPath(r.ProcessName, r.ExecutablePath, ConfigService.NormalizeProcessName(r.ProcessName));

        if (!string.IsNullOrWhiteSpace(machine.DiscoveredInventoryJson))
        {
            try
            {
                var snapshot = System.Text.Json.JsonSerializer.Deserialize<List<MachineInventorySnapshotRow>>(
                    machine.DiscoveredInventoryJson,
                    new System.Text.Json.JsonSerializerOptions { PropertyNameCaseInsensitive = true }) ?? [];
                foreach (var row in snapshot)
                    AddPath(row.ProcessName, row.ExecutablePath, row.DisplayName ?? row.ProcessName);
            }
            catch
            {
                // ignore corrupt snapshot
            }
        }

        return map.Keys
            .OrderBy(n => ProcessClassification.GroupSortOrder(ProcessClassification.Classify(n, ctx).Group))
            .ThenBy(n => n, StringComparer.OrdinalIgnoreCase)
            .Select(name => ToExportRow(name, map[name].Path, ctx, assignments, knownApps, soeApps))
            .ToList();
    }

    /// <summary>Export the global classification universe (catalogs + DB assignments + known apps + process runs).</summary>
    public async Task<IReadOnlyList<CsvExportRow>> BuildGlobalExportRowsAsync(CancellationToken ct = default)
    {
        var ctx = await BuildContextAsync(ct);
        var assignments = await db.ProcessGroupAssignments.AsNoTracking().ToDictionaryAsync(a => a.ProcessName, StringComparer.OrdinalIgnoreCase, ct);
        var knownApps = await db.KnownApps.AsNoTracking().ToDictionaryAsync(k => k.ProcessName, StringComparer.OrdinalIgnoreCase, ct);
        var soeApps = await db.SoeApps.AsNoTracking().ToDictionaryAsync(s => s.ProcessName, StringComparer.OrdinalIgnoreCase, ct);

        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var n in WindowsCoreCatalog.Names)
            names.Add(n);
        foreach (var e in SoeCatalog.Entries)
            names.Add(ConfigService.NormalizeProcessName(e.ProcessName));
        foreach (var s in soeApps.Keys)
            names.Add(s);
        foreach (var a in assignments.Keys)
            names.Add(a);
        foreach (var k in knownApps.Keys)
            names.Add(k);

        var pathRows = await db.ProcessRuns.AsNoTracking()
            .Where(r => r.ExecutablePath != null && r.ExecutablePath != "")
            .Select(r => new { r.ProcessName, r.ExecutablePath, r.LastSeenAtUtc })
            .ToListAsync(ct);

        var pathMap = pathRows
            .Select(r => new { Name = ConfigService.NormalizeProcessName(r.ProcessName), r.ExecutablePath, r.LastSeenAtUtc })
            .Where(r => r.Name.Length > 0)
            .GroupBy(r => r.Name, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(
                g => g.Key,
                g => g.OrderByDescending(x => x.LastSeenAtUtc).First().ExecutablePath!.Trim(),
                StringComparer.OrdinalIgnoreCase);

        foreach (var n in pathMap.Keys)
            names.Add(n);

        var runOnly = await db.ProcessRuns.AsNoTracking()
            .Select(r => r.ProcessName)
            .Distinct()
            .ToListAsync(ct);
        foreach (var p in runOnly)
        {
            var n = ConfigService.NormalizeProcessName(p);
            if (n.Length > 0)
                names.Add(n);
        }

        return names
            .OrderBy(n => ProcessClassification.GroupSortOrder(ProcessClassification.Classify(n, ctx).Group))
            .ThenBy(n => n, StringComparer.OrdinalIgnoreCase)
            .Select(name =>
            {
                pathMap.TryGetValue(name, out var path);
                return ToExportRow(name, path, ctx, assignments, knownApps, soeApps);
            })
            .ToList();
    }

    public static byte[] RenderCsv(IReadOnlyList<CsvExportRow> rows)
    {
        var sb = new StringBuilder();
        sb.AppendLine(CsvHeader);
        foreach (var row in rows)
        {
            sb.Append(EscapeCsv(row.ProcessName)).Append(',');
            sb.Append(EscapeCsv(row.ExecutablePath ?? "")).Append(',');
            sb.Append(EscapeCsv(row.Group.ToString())).Append(',');
            sb.Append(EscapeCsv(row.Description ?? "")).Append(',');
            sb.AppendLine(EscapeCsv(row.DisplayName));
        }
        return Encoding.UTF8.GetBytes(sb.ToString());
    }

    public async Task<CsvImportResult> ImportCsvAsync(Stream stream, CancellationToken ct = default)
    {
        using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true, leaveOpen: true);
        var headerLine = await reader.ReadLineAsync(ct);
        if (headerLine is null)
            return new CsvImportResult(0, 0, ["CSV file is empty."]);

        var headers = SplitCsv(headerLine).Select(h => h.Trim()).ToList();
        var procIdx = IndexOfAny(headers, "ProcessName", "Process", "Exe");
        var pathIdx = IndexOfAny(headers, "ExecutablePath", "Path", "Executable");
        var groupIdx = IndexOfAny(headers, "Group", "Classification");
        var descIdx = IndexOfAny(headers, "Description", "Notes", "Note");
        var dispIdx = IndexOfAny(headers, "DisplayName", "Name", "App");

        if (procIdx < 0)
            return new CsvImportResult(0, 0, ["Missing required column: ProcessName."]);

        var existingAssignments = await db.ProcessGroupAssignments.ToListAsync(ct);
        var assignmentMap = existingAssignments.ToDictionary(a => a.ProcessName, StringComparer.OrdinalIgnoreCase);
        var soeApps = await db.SoeApps.ToListAsync(ct);
        var soeMap = soeApps.ToDictionary(s => s.ProcessName, StringComparer.OrdinalIgnoreCase);
        var now = DateTimeOffset.UtcNow;
        var updated = 0;
        var skipped = 0;
        var errors = new List<string>();
        var rowNum = 1;

        string? line;
        while ((line = await reader.ReadLineAsync(ct)) is not null)
        {
            rowNum++;
            if (string.IsNullOrWhiteSpace(line)) continue;
            if (updated + skipped >= MaxImportRows)
            {
                errors.Add($"Stopped at row {rowNum}: maximum {MaxImportRows:N0} data rows per import.");
                break;
            }

            var cols = SplitCsv(line);
            var rawProc = GetCol(cols, procIdx);
            if (string.IsNullOrWhiteSpace(rawProc))
            {
                skipped++;
                continue;
            }

            var processName = ConfigService.NormalizeProcessName(rawProc);
            if (processName.Length == 0)
            {
                errors.Add($"Row {rowNum}: invalid process name.");
                skipped++;
                continue;
            }

            var groupRaw = GetCol(cols, groupIdx);
            if (!TryParseGroup(groupRaw, out var targetGroup))
            {
                errors.Add($"Row {rowNum} ({processName}): invalid Group '{groupRaw ?? ""}' — use CoreWindows, Soe, or Specialization.");
                skipped++;
                continue;
            }

            var description = NullIfEmpty(GetCol(cols, descIdx));
            var displayName = NullIfEmpty(GetCol(cols, dispIdx)) ?? processName;
            _ = GetCol(cols, pathIdx); // path is informational for export round-trip; not persisted on assignment

            var changed = false;
            if (assignmentMap.TryGetValue(processName, out var existing))
            {
                if (existing.Group != targetGroup)
                {
                    existing.Group = targetGroup;
                    changed = true;
                }
                if (!string.Equals(existing.Description, description, StringComparison.Ordinal))
                {
                    existing.Description = description;
                    changed = true;
                }
                if (!string.Equals(existing.DisplayName, displayName, StringComparison.Ordinal))
                {
                    existing.DisplayName = displayName;
                    changed = true;
                }
                if (changed)
                {
                    existing.UpdatedUtc = now;
                    updated++;
                }
                else
                {
                    skipped++;
                }
            }
            else
            {
                var row = new ProcessGroupAssignment
                {
                    ProcessName = processName,
                    Group = targetGroup,
                    DisplayName = displayName,
                    Description = description,
                    UpdatedUtc = now
                };
                db.ProcessGroupAssignments.Add(row);
                assignmentMap[processName] = row;
                changed = true;
                updated++;
            }

            if (changed)
                ApplySoeMembership(processName, displayName, targetGroup, soeMap);
        }

        if (updated > 0)
            await db.SaveChangesAsync(ct);

        if (updated > 0)
        {
            await AuditAsync(
                "csv-import",
                $"CSV import: {updated} updated, {skipped} skipped, {errors.Count} error(s).");
        }

        return new CsvImportResult(updated, skipped, errors);
    }

    public static bool TryParseGroup(string? value, out AppGroup group)
    {
        group = AppGroup.Specialization;
        if (string.IsNullOrWhiteSpace(value))
            return false;

        var v = value.Trim();
        var compact = v.Replace(" ", "", StringComparison.Ordinal)
            .Replace("-", "", StringComparison.Ordinal)
            .Replace("_", "", StringComparison.Ordinal);

        if (compact.Equals("CoreWindows", StringComparison.OrdinalIgnoreCase)
            || compact.Equals("Core", StringComparison.OrdinalIgnoreCase)
            || v.Equals("Core Windows", StringComparison.OrdinalIgnoreCase))
        {
            group = AppGroup.CoreWindows;
            return true;
        }

        if (compact.Equals("SOE", StringComparison.OrdinalIgnoreCase)
            || compact.Equals("Soe", StringComparison.OrdinalIgnoreCase))
        {
            group = AppGroup.Soe;
            return true;
        }

        if (compact.Equals("Specialization", StringComparison.OrdinalIgnoreCase)
            || compact.Equals("Spec", StringComparison.OrdinalIgnoreCase)
            || compact.Equals("Specialisation", StringComparison.OrdinalIgnoreCase))
        {
            group = AppGroup.Specialization;
            return true;
        }

        return Enum.TryParse(v, ignoreCase: true, out group);
    }

    private static CsvExportRow ToExportRow(
        string processName,
        string? executablePath,
        ProcessClassificationContext ctx,
        IReadOnlyDictionary<string, ProcessGroupAssignment> assignments,
        IReadOnlyDictionary<string, KnownApp> knownApps,
        IReadOnlyDictionary<string, SoeApp> soeApps)
    {
        var classification = ProcessClassification.Classify(processName, ctx);
        assignments.TryGetValue(processName, out var assignment);

        var displayName = assignment?.DisplayName;
        if (string.IsNullOrWhiteSpace(displayName) && knownApps.TryGetValue(processName, out var known))
            displayName = known.DisplayName;
        if (string.IsNullOrWhiteSpace(displayName) && soeApps.TryGetValue(processName, out var soe))
            displayName = soe.DisplayName;
        if (string.IsNullOrWhiteSpace(displayName))
        {
            var catalogSoe = SoeCatalog.Entries.FirstOrDefault(e =>
                string.Equals(e.ProcessName, processName, StringComparison.OrdinalIgnoreCase));
            if (!string.IsNullOrEmpty(catalogSoe.ProcessName))
                displayName = catalogSoe.DisplayName;
        }
        displayName ??= processName;

        var description = assignment?.Description;
        var group = assignment?.Group ?? classification.Group;

        return new CsvExportRow(processName, executablePath, group, description, displayName);
    }

    private void ApplySoeMembership(
        string processName,
        string displayName,
        AppGroup targetGroup,
        Dictionary<string, SoeApp> soeMap)
    {
        if (targetGroup == AppGroup.Soe)
        {
            if (soeMap.ContainsKey(processName))
                return;

            var catalogEntry = SoeCatalog.Entries.FirstOrDefault(e =>
                string.Equals(e.ProcessName, processName, StringComparison.OrdinalIgnoreCase));
            db.SoeApps.Add(new SoeApp
            {
                DisplayName = string.IsNullOrEmpty(catalogEntry.ProcessName) ? displayName : catalogEntry.DisplayName,
                ProcessName = processName,
                Category = "SOE",
                Vendor = catalogEntry.Vendor ?? "User"
            });
            soeMap[processName] = new SoeApp { ProcessName = processName, DisplayName = displayName };
            return;
        }

        if (soeMap.Remove(processName, out var soeRow))
            db.SoeApps.Remove(soeRow);
    }

    private static string EscapeCsv(string value)
    {
        if (string.IsNullOrEmpty(value))
            return "";
        if (value.Contains(',') || value.Contains('"') || value.Contains('\n') || value.Contains('\r'))
            return $"\"{value.Replace("\"", "\"\"")}\"";
        return value;
    }

    private static int IndexOfAny(List<string> headers, params string[] names)
    {
        for (var i = 0; i < headers.Count; i++)
            if (names.Any(n => string.Equals(headers[i], n, StringComparison.OrdinalIgnoreCase)))
                return i;
        return -1;
    }

    private static string? GetCol(List<string> cols, int idx) =>
        idx >= 0 && idx < cols.Count ? cols[idx].Trim().Trim('"') : null;

    private static string? NullIfEmpty(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static List<string> SplitCsv(string line)
    {
        var result = new List<string>();
        var sb = new StringBuilder();
        var inQuotes = false;
        foreach (var ch in line)
        {
            if (ch == '"') { inQuotes = !inQuotes; continue; }
            if (ch == ',' && !inQuotes) { result.Add(sb.ToString()); sb.Clear(); continue; }
            sb.Append(ch);
        }
        result.Add(sb.ToString());
        return result;
    }

    private sealed record MachineInventorySnapshotRow(
        string ProcessName,
        string? DisplayName,
        string? ExecutablePath,
        string? Source);
}
