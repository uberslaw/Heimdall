using System.Security.Principal;
using System.Text;
using System.Text.Json;
using Heimdall.Api.Data;
using Heimdall.Shared.Contracts;
using Microsoft.EntityFrameworkCore;
namespace Heimdall.Api.Services;

public sealed class AppListService(HeimdallDbContext db, ProcessGroupService processGroups, ProcessCatalogService catalog)
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    public async Task AuditAsync(
        string action,
        string detail,
        int? appListId = null,
        string? appListName = null,
        ConfigScope? scope = null,
        string? scopeValue = null,
        string? hostname = null,
        string? actor = null,
        CancellationToken ct = default)
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
            Actor = actor ?? ResolveActor()
        });
        await db.SaveChangesAsync(ct);
    }

    public static string ResolveActor()
    {
        try
        {
            var id = WindowsIdentity.GetCurrent()?.Name;
            if (!string.IsNullOrWhiteSpace(id))
                return id;
        }
        catch { /* non-Windows / no identity */ }
        return "system";
    }

    public async Task<AppList> CreateOrUpdateListAsync(
        int? id,
        string name,
        int? teamId,
        string? notes,
        IEnumerable<(string ProcessName, string? DisplayName)> entries,
        CancellationToken ct)
    {
        var now = DateTimeOffset.UtcNow;
        AppList list;
        string? oldDetail = null;

        if (id is int existingId)
        {
            list = await db.AppLists.Include(a => a.Entries).FirstAsync(a => a.Id == existingId, ct);
            oldDetail = SummarizeList(list);
            list.Name = name.Trim();
            list.TeamId = teamId;
            list.Notes = string.IsNullOrWhiteSpace(notes) ? null : notes.Trim();
            list.UpdatedUtc = now;
            db.AppListEntries.RemoveRange(list.Entries);
            list.Entries.Clear();
        }
        else
        {
            list = new AppList
            {
                Name = name.Trim(),
                TeamId = teamId,
                Notes = string.IsNullOrWhiteSpace(notes) ? null : notes.Trim(),
                CreatedUtc = now,
                UpdatedUtc = now
            };
            db.AppLists.Add(list);
        }

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (processName, displayName) in entries)
        {
            var proc = ConfigService.NormalizeProcessName(processName);
            if (proc.Length == 0 || !seen.Add(proc))
                continue;
            list.Entries.Add(new AppListEntry
            {
                ProcessName = proc,
                DisplayName = string.IsNullOrWhiteSpace(displayName) ? null : displayName.Trim()
            });
        }

        await db.SaveChangesAsync(ct);

        await catalog.UpsertAsync(
            list.Entries.Select(e => new ProcessCatalogService.CatalogItem(e.ProcessName, null, e.DisplayName)),
            null, id is null ? $"list “{list.Name}” created" : $"list “{list.Name}” updated", ct);

        var newDetail = SummarizeList(list);
        await AuditAsync(
            id is null ? "created" : "updated",
            id is null ? $"Created list “{list.Name}”: {newDetail}" : $"Updated list “{list.Name}”: {oldDetail} → {newDetail}",
            list.Id, list.Name, actor: ResolveActor(), ct: ct);

        return list;
    }

    /// <summary>Render a process selection in the same CSV shape the Upload box accepts, for round-tripping back in.</summary>
    public static byte[] RenderUploadCsv(IEnumerable<(string ProcessName, string? DisplayName)> processes, string listName)
    {
        var sb = new StringBuilder();
        sb.AppendLine("ListName,ProcessName,DisplayName");
        foreach (var (proc, display) in processes)
            sb.AppendLine($"{CsvEscape(listName)},{CsvEscape(proc)},{CsvEscape(display ?? "")}");
        return Encoding.UTF8.GetBytes(sb.ToString());
    }

    /// <summary>Render a process selection in the same JSON shape the Upload box accepts, for round-tripping back in.</summary>
    public static byte[] RenderUploadJson(IEnumerable<(string ProcessName, string? DisplayName)> processes, string listName)
    {
        var payload = processes.Select(p => new { processName = p.ProcessName, displayName = p.DisplayName, listName });
        return Encoding.UTF8.GetBytes(JsonSerializer.Serialize(payload, new JsonSerializerOptions { WriteIndented = true }));
    }

    /// <summary>Multi-list variant (e.g. exporting several selected app lists' entries at once).</summary>
    public static byte[] RenderUploadCsv(IEnumerable<(string ListName, string ProcessName, string? DisplayName)> rows)
    {
        var sb = new StringBuilder();
        sb.AppendLine("ListName,ProcessName,DisplayName");
        foreach (var (listName, proc, display) in rows)
            sb.AppendLine($"{CsvEscape(listName)},{CsvEscape(proc)},{CsvEscape(display ?? "")}");
        return Encoding.UTF8.GetBytes(sb.ToString());
    }

    public static byte[] RenderUploadJson(IEnumerable<(string ListName, string ProcessName, string? DisplayName)> rows)
    {
        var payload = rows.Select(r => new { processName = r.ProcessName, displayName = r.DisplayName, listName = r.ListName });
        return Encoding.UTF8.GetBytes(JsonSerializer.Serialize(payload, new JsonSerializerOptions { WriteIndented = true }));
    }

    private static string CsvEscape(string value)
    {
        if (string.IsNullOrEmpty(value)) return "";
        return value.Contains(',') || value.Contains('"') || value.Contains('\n')
            ? $"\"{value.Replace("\"", "\"\"")}\""
            : value;
    }

    public async Task<(int ListsCreated, int EntriesAdded)> UploadCsvAsync(
        Stream stream,
        int? defaultTeamId,
        CancellationToken ct)
    {
        using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
        var headerLine = await reader.ReadLineAsync(ct);
        if (headerLine is null)
            return (0, 0);

        var headers = SplitCsv(headerLine).Select(h => h.Trim()).ToList();
        var procIdx = IndexOfAny(headers, "ProcessName", "Process", "Exe", "Executable");
        var dispIdx = IndexOfAny(headers, "DisplayName", "Name", "App", "Application");
        var teamIdx = IndexOfAny(headers, "Team", "TeamName", "TeamCode");
        var listIdx = IndexOfAny(headers, "ListName", "AppList", "Schema", "List");

        if (procIdx < 0)
        {
            // Headerless single-column process list
            procIdx = 0;
            headers = ["ProcessName"];
            // Re-process first line as data
            return await UploadRowsAsync(
                await ParseDataRows(headerLine, null, reader, procIdx, dispIdx, teamIdx, listIdx, ct),
                defaultTeamId, ct);
        }

        var rows = new List<UploadRow>();
        string? line;
        while ((line = await reader.ReadLineAsync(ct)) is not null)
        {
            if (string.IsNullOrWhiteSpace(line)) continue;
            var cols = SplitCsv(line);
            var proc = GetCol(cols, procIdx);
            if (string.IsNullOrWhiteSpace(proc)) continue;
            rows.Add(new UploadRow(
                GetCol(cols, listIdx) ?? "Uploaded list",
                ConfigService.NormalizeProcessName(proc),
                GetCol(cols, dispIdx),
                GetCol(cols, teamIdx)));
        }

        return await UploadRowsAsync(rows, defaultTeamId, ct);
    }

    public async Task<(int ListsCreated, int EntriesAdded)> UploadJsonAsync(
        Stream stream,
        int? defaultTeamId,
        CancellationToken ct)
    {
        using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: ct);
        var rows = new List<UploadRow>();

        if (doc.RootElement.ValueKind == JsonValueKind.Array)
        {
            foreach (var el in doc.RootElement.EnumerateArray())
            {
                var proc = el.TryGetProperty("processName", out var p) ? p.GetString()
                    : el.TryGetProperty("ProcessName", out var p2) ? p2.GetString() : null;
                if (string.IsNullOrWhiteSpace(proc)) continue;
                var disp = el.TryGetProperty("displayName", out var d) ? d.GetString()
                    : el.TryGetProperty("DisplayName", out var d2) ? d2.GetString() : null;
                var listName = el.TryGetProperty("listName", out var l) ? l.GetString()
                    : el.TryGetProperty("ListName", out var l2) ? l2.GetString() : "Uploaded list";
                var team = el.TryGetProperty("team", out var t) ? t.GetString()
                    : el.TryGetProperty("Team", out var t2) ? t2.GetString() : null;
                rows.Add(new UploadRow(listName ?? "Uploaded list", ConfigService.NormalizeProcessName(proc), disp, team));
            }
        }

        return await UploadRowsAsync(rows, defaultTeamId, ct);
    }

    private async Task<(int ListsCreated, int EntriesAdded)> UploadRowsAsync(
        List<UploadRow> rows,
        int? defaultTeamId,
        CancellationToken ct)
    {
        if (rows.Count == 0)
            return (0, 0);

        var teams = await db.Teams.AsNoTracking().ToListAsync(ct);
        var listsCreated = 0;
        var entriesAdded = 0;

        foreach (var group in rows.GroupBy(r => r.ListName, StringComparer.OrdinalIgnoreCase))
        {
            var teamHint = group.Select(g => g.TeamHint).FirstOrDefault(t => !string.IsNullOrWhiteSpace(t));
            int? teamId = defaultTeamId;
            if (!string.IsNullOrWhiteSpace(teamHint))
            {
                var match = teams.FirstOrDefault(t =>
                    string.Equals(t.Name, teamHint, StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(t.Code, teamHint, StringComparison.OrdinalIgnoreCase));
                if (match is not null)
                    teamId = match.Id;
            }

            var existing = await db.AppLists.Include(a => a.Entries)
                .FirstOrDefaultAsync(a => a.Name == group.Key, ct);

            if (existing is null)
            {
                existing = new AppList
                {
                    Name = group.Key,
                    TeamId = teamId,
                    CreatedUtc = DateTimeOffset.UtcNow,
                    UpdatedUtc = DateTimeOffset.UtcNow
                };
                db.AppLists.Add(existing);
                listsCreated++;
            }
            else
            {
                existing.UpdatedUtc = DateTimeOffset.UtcNow;
                if (teamId is not null)
                    existing.TeamId = teamId;
            }

            var have = existing.Entries.Select(e => e.ProcessName).ToHashSet(StringComparer.OrdinalIgnoreCase);
            foreach (var row in group)
            {
                if (row.ProcessName.Length == 0 || !have.Add(row.ProcessName))
                    continue;
                existing.Entries.Add(new AppListEntry
                {
                    ProcessName = row.ProcessName,
                    DisplayName = row.DisplayName
                });
                entriesAdded++;
            }
        }

        await db.SaveChangesAsync(ct);

        await catalog.UpsertAsync(
            rows.Select(r => new ProcessCatalogService.CatalogItem(r.ProcessName, null, r.DisplayName)),
            null, "CSV/JSON upload", ct);

        await AuditAsync("uploaded", $"Upload: {listsCreated} list(s), {entriesAdded} entr(y/ies)", ct: ct);
        return (listsCreated, entriesAdded);
    }

    private static async Task<List<UploadRow>> ParseDataRows(
        string firstLine,
        string? _,
        StreamReader reader,
        int procIdx,
        int dispIdx,
        int teamIdx,
        int listIdx,
        CancellationToken ct)
    {
        var rows = new List<UploadRow>();
        void AddLine(string line)
        {
            var cols = SplitCsv(line);
            var proc = GetCol(cols, procIdx);
            if (string.IsNullOrWhiteSpace(proc)) return;
            rows.Add(new UploadRow(
                GetCol(cols, listIdx) ?? "Uploaded list",
                ConfigService.NormalizeProcessName(proc),
                GetCol(cols, dispIdx),
                GetCol(cols, teamIdx)));
        }
        AddLine(firstLine);
        string? next;
        while ((next = await reader.ReadLineAsync(ct)) is not null)
        {
            if (!string.IsNullOrWhiteSpace(next))
                AddLine(next);
        }
        return rows;
    }

    public async Task AssignAsync(
        int appListId,
        IEnumerable<(ConfigScope Scope, string? ScopeValue)> scopes,
        CancellationToken ct)
    {
        var list = await db.AppLists.FirstAsync(a => a.Id == appListId, ct);
        foreach (var (scope, scopeValue) in scopes.Distinct())
        {
            var value = string.IsNullOrWhiteSpace(scopeValue) ? null : scopeValue.Trim();
            var existing = await db.AppListAssignments.FirstOrDefaultAsync(a =>
                a.AppListId == appListId &&
                a.Scope == scope &&
                a.ScopeValue == value, ct);

            if (existing is null)
            {
                db.AppListAssignments.Add(new AppListAssignment
                {
                    AppListId = appListId,
                    Scope = scope,
                    ScopeValue = value,
                    Priority = ConfigService.ScopeRank(scope) * 10,
                    IsEnabled = true
                });
                await AuditAsync("assigned", $"Assigned “{list.Name}” to {scope}:{value ?? "*"}",
                    list.Id, list.Name, scope, value, ct: ct);
            }
            else if (!existing.IsEnabled)
            {
                existing.IsEnabled = true;
                await AuditAsync("assigned", $"Re-enabled “{list.Name}” on {scope}:{value ?? "*"}",
                    list.Id, list.Name, scope, value, ct: ct);
            }
        }
        await db.SaveChangesAsync(ct);
    }

    public async Task UnassignAsync(int assignmentId, CancellationToken ct)
    {
        var a = await db.AppListAssignments.Include(x => x.AppList)
            .FirstOrDefaultAsync(x => x.Id == assignmentId, ct);
        if (a is null) return;
        var name = a.AppList.Name;
        var scope = a.Scope;
        var value = a.ScopeValue;
        var listId = a.AppListId;
        db.AppListAssignments.Remove(a);
        await db.SaveChangesAsync(ct);
        await AuditAsync("removed", $"Removed “{name}” from {scope}:{value ?? "*"}",
            listId, name, scope, value, ct: ct);
    }

    public async Task<MachineAppListsView> GetEffectiveForHostAsync(string hostname, CancellationToken ct)
    {
        var machine = await db.Machines.AsNoTracking().FirstOrDefaultAsync(m => m.Hostname == hostname, ct);
        if (machine is not null)
            MachineHierarchy.EnsureDefaults(machine);

        var assignments = await db.AppListAssignments.AsNoTracking()
            .Include(a => a.AppList).ThenInclude(l => l.Entries)
            .Include(a => a.AppList).ThenInclude(l => l.Team)
            .Where(a => a.IsEnabled)
            .ToListAsync(ct);

        var matching = assignments
            .Where(a => ConfigService.MatchesScope(a.Scope, a.ScopeValue, machine, hostname))
            .OrderByDescending(a => ConfigService.ScopeRank(a.Scope))
            .ThenByDescending(a => a.Priority)
            .ToList();

        var processes = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var a in matching)
            foreach (var e in a.AppList.Entries)
                processes.Add(e.ProcessName);

        return new MachineAppListsView(
            hostname,
            matching.Select(a => new ActiveAppListInfo(
                a.AppListId,
                a.AppList.Name,
                a.AppList.Team?.Name,
                a.Scope,
                a.ScopeValue,
                a.AppList.IsAutoDiscovered,
                a.AppList.Entries.Count)).ToList(),
            processes.ToList(),
            machine?.AppAnalysisStatus ?? AppAnalysisStatus.None,
            DeserializeProposals(machine?.AppAnalysisProposalJson));
    }

    public async Task<IReadOnlyList<string>> ResolveProcessNamesForHostAsync(string hostname, CancellationToken ct)
    {
        var view = await GetEffectiveForHostAsync(hostname, ct);
        return view.MergedProcesses;
    }

    /// <summary>
    /// Discover non-SOE apps and store as PendingApproval proposals.
    /// Does NOT start tracking until Approve / Apply team.
    /// </summary>
    public async Task<AnalysisResult> AnalyzeMachineAsync(
        string hostname,
        IEnumerable<DiscoveredProcessDto>? inventory,
        bool requestAgentInventoryIfEmpty,
        CancellationToken ct)
    {
        var machine = await db.Machines.FirstOrDefaultAsync(m => m.Hostname == hostname, ct);
        if (machine is null)
            throw new InvalidOperationException($"Machine “{hostname}” not found.");

        MachineHierarchy.EnsureDefaults(machine);

        var ctx = await processGroups.BuildContextAsync(ct);

        var fromRuns = await db.ProcessRuns.AsNoTracking()
            .Where(r => r.MachineId == machine.Id)
            .Select(r => new { r.ProcessName, r.ExecutablePath })
            .ToListAsync(ct);

        var candidates = new Dictionary<string, MutableCandidate>(StringComparer.OrdinalIgnoreCase);
        foreach (var r in fromRuns)
        {
            var name = ConfigService.NormalizeProcessName(r.ProcessName);
            if (name.Length == 0) continue;
            MergeCandidate(candidates, name, name, r.ExecutablePath, "ProcessRuns");
        }

        if (inventory is not null)
        {
            foreach (var d in inventory)
            {
                var name = ConfigService.NormalizeProcessName(d.ProcessName);
                if (name.Length == 0) continue;
                var display = string.IsNullOrWhiteSpace(d.DisplayName) ? name : d.DisplayName.Trim();
                MergeCandidate(candidates, name, display, d.ExecutablePath, "AgentInventory",
                    d.FileVersion, d.ProductVersion, d.CompanyName, d.FileDescription);
            }
        }

        machine.DiscoveredInventoryJson = JsonSerializer.Serialize(
            candidates.Values.Select(c => new InventorySnapshotRow(c.ProcessName, c.DisplayName, c.ExecutablePath, c.Source)).ToList(),
            JsonOptions);

        var catalogResult = await catalog.UpsertAsync(
            candidates.Values.Select(c => new ProcessCatalogService.CatalogItem(
                c.ProcessName, c.ExecutablePath, c.DisplayName, c.FileVersion, c.ProductVersion, c.CompanyName, c.FileDescription)),
            hostname, "machine analysis", ct);

        var proposals = candidates.Values
            .Select(c => ToProposedApp(c, ctx))
            .Where(p => ProcessClassification.IsProposableForTracking(p.ProcessName, ctx))
            .OrderBy(p => p.ProcessName, StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (proposals.Count == 0 && requestAgentInventoryIfEmpty)
        {
            machine.PendingAppAnalysis = true;
            await db.SaveChangesAsync(ct);
            await AuditAsync("analyze-queued",
                $"Analysis queued for {hostname}; waiting for agent process inventory.",
                hostname: hostname, ct: ct);
            return new AnalysisResult(hostname, [], AppAnalysisStatus.None, queuedForAgent: true, catalogResult.NewCount);
        }

        machine.AppsAnalyzedAt = DateTimeOffset.UtcNow;
        machine.AppAnalysisStatus = AppAnalysisStatus.PendingApproval;
        machine.AppAnalysisProposalJson = JsonSerializer.Serialize(proposals, JsonOptions);
        machine.PendingAppAnalysis = false;
        await db.SaveChangesAsync(ct);

        await AuditAsync("analyzed",
            $"Analysis for {hostname}: {proposals.Count} specialization app(s) pending approval (Core Windows and SOE excluded by default). Pre-approval tracking = existing config includes + known defaults + already-assigned app lists only.",
            hostname: hostname, ct: ct);
        return new AnalysisResult(hostname, proposals, AppAnalysisStatus.PendingApproval, queuedForAgent: false, catalogResult.NewCount);
    }

    public async Task ApproveAsync(
        string hostname,
        IReadOnlyList<string>? selectedProcessNames,
        CancellationToken ct)
    {
        var machine = await db.Machines.FirstAsync(m => m.Hostname == hostname, ct);
        var proposals = DeserializeProposals(machine.AppAnalysisProposalJson);
        if (proposals.Count == 0)
            throw new InvalidOperationException("No proposals to approve.");

        var approved = selectedProcessNames is null || selectedProcessNames.Count == 0
            ? proposals
            : proposals.Where(p => selectedProcessNames.Contains(p.ProcessName, StringComparer.OrdinalIgnoreCase)).ToList();

        if (approved.Count == 0)
            throw new InvalidOperationException("Select at least one app to approve.");

        var listName = $"Discovered on {hostname}";
        var list = await db.AppLists.Include(a => a.Entries)
            .FirstOrDefaultAsync(a => a.Name == listName && a.IsAutoDiscovered, ct);

        if (list is null)
        {
            list = new AppList
            {
                Name = listName,
                IsAutoDiscovered = true,
                Notes = "Created from analysis approval",
                CreatedUtc = DateTimeOffset.UtcNow,
                UpdatedUtc = DateTimeOffset.UtcNow
            };
            db.AppLists.Add(list);
        }
        else
        {
            list.UpdatedUtc = DateTimeOffset.UtcNow;
            db.AppListEntries.RemoveRange(list.Entries);
            list.Entries.Clear();
        }

        foreach (var p in approved)
        {
            list.Entries.Add(new AppListEntry
            {
                ProcessName = p.ProcessName,
                DisplayName = p.DisplayName
            });
        }

        await db.SaveChangesAsync(ct);

        var assignment = await db.AppListAssignments.FirstOrDefaultAsync(a =>
            a.AppListId == list.Id &&
            a.Scope == ConfigScope.Machine &&
            a.ScopeValue == hostname, ct);
        if (assignment is null)
        {
            db.AppListAssignments.Add(new AppListAssignment
            {
                AppListId = list.Id,
                Scope = ConfigScope.Machine,
                ScopeValue = hostname,
                Priority = ConfigService.ScopeRank(ConfigScope.Machine) * 10,
                IsEnabled = true
            });
        }
        else
        {
            assignment.IsEnabled = true;
        }

        machine.AppAnalysisStatus = AppAnalysisStatus.Approved;
        machine.AppAnalysisProposalJson = "[]";
        await db.SaveChangesAsync(ct);

        var mode = selectedProcessNames is null || selectedProcessNames.Count == 0 ? "Approve all" : "Approve selected";
        await AuditAsync("approved",
            $"{mode} for {hostname}: tracking {approved.Count} app(s) via “{listName}” [{string.Join(", ", approved.Select(a => a.ProcessName))}]",
            list.Id, listName, ConfigScope.Machine, hostname, hostname, ct: ct);
    }

    public async Task ApplyTeamListAsync(string hostname, int appListId, CancellationToken ct)
    {
        var machine = await db.Machines.FirstAsync(m => m.Hostname == hostname, ct);
        var list = await db.AppLists.Include(a => a.Entries).Include(a => a.Team)
            .FirstAsync(a => a.Id == appListId, ct);

        // Team path: only this list applies for the machine (disable other machine-scoped auto lists).
        var machineAssignments = await db.AppListAssignments
            .Include(a => a.AppList)
            .Where(a => a.Scope == ConfigScope.Machine && a.ScopeValue == hostname)
            .ToListAsync(ct);

        foreach (var a in machineAssignments)
        {
            if (a.AppListId == appListId)
            {
                a.IsEnabled = true;
                continue;
            }
            if (a.AppList.IsAutoDiscovered)
            {
                a.IsEnabled = false;
                await AuditAsync("removed",
                    $"Disabled auto-discovered “{a.AppList.Name}” on {hostname} in favour of team list “{list.Name}”",
                    a.AppListId, a.AppList.Name, ConfigScope.Machine, hostname, hostname, ct: ct);
            }
        }

        if (!machineAssignments.Any(a => a.AppListId == appListId))
        {
            db.AppListAssignments.Add(new AppListAssignment
            {
                AppListId = appListId,
                Scope = ConfigScope.Machine,
                ScopeValue = hostname,
                Priority = ConfigService.ScopeRank(ConfigScope.Machine) * 10,
                IsEnabled = true
            });
        }

        machine.AppAnalysisStatus = AppAnalysisStatus.Approved;
        machine.AppAnalysisProposalJson = "[]";
        await db.SaveChangesAsync(ct);

        var procs = string.Join(", ", list.Entries.Select(e => e.ProcessName));
        await AuditAsync("team-applied",
            $"Applied team app list “{list.Name}” ({list.Team?.Name ?? "no team"}) to {hostname}. Tracking only: [{procs}]",
            list.Id, list.Name, ConfigScope.Machine, hostname, hostname, ct: ct);
    }

    public async Task DismissAnalysisAsync(string hostname, CancellationToken ct)
    {
        var machine = await db.Machines.FirstAsync(m => m.Hostname == hostname, ct);
        var count = DeserializeProposals(machine.AppAnalysisProposalJson).Count;
        machine.AppAnalysisStatus = AppAnalysisStatus.Dismissed;
        machine.AppAnalysisProposalJson = "[]";
        await db.SaveChangesAsync(ct);
        await AuditAsync("dismissed",
            $"Dismissed analysis for {hostname} ({count} proposed app(s) ignored; not tracked).",
            hostname: hostname, ct: ct);
    }

    /// <summary>Ask the agent to upload a one-shot full process inventory on its next config/upload cycle.</summary>
    public async Task RequestAgentInventoryAsync(string hostname, CancellationToken ct)
    {
        var machine = await db.Machines.FirstOrDefaultAsync(m => m.Hostname == hostname, ct)
            ?? throw new InvalidOperationException($"Machine “{hostname}” not found.");

        machine.PendingAppAnalysis = true;
        await db.SaveChangesAsync(ct);
        await AuditAsync("inventory-requested",
            $"Requested full process inventory for {hostname}. Agent picks this up on next config refresh (~5 min), then uploads on next heartbeat (~1 min).",
            hostname: hostname, ct: ct);
    }

    public async Task<IReadOnlyList<ClassifiedProcessRow>> GetMachineInventoryAsync(string hostname, CancellationToken ct)
    {
        var machine = await db.Machines.AsNoTracking().FirstOrDefaultAsync(m => m.Hostname == hostname, ct);
        if (machine is null)
            return [];

        var ctx = await processGroups.BuildContextAsync(ct);

        var tracked = (await ResolveProcessNamesForHostAsync(hostname, ct))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var proposals = DeserializeProposals(machine.AppAnalysisProposalJson)
            .ToDictionary(p => p.ProcessName, StringComparer.OrdinalIgnoreCase);

        var fromRuns = await db.ProcessRuns.AsNoTracking()
            .Where(r => r.MachineId == machine.Id)
            .Select(r => new { r.ProcessName, r.ExecutablePath })
            .ToListAsync(ct);

        var map = new Dictionary<string, MutableCandidate>(StringComparer.OrdinalIgnoreCase);
        foreach (var r in fromRuns)
        {
            var name = ConfigService.NormalizeProcessName(r.ProcessName);
            if (name.Length == 0) continue;
            MergeCandidate(map, name, name, r.ExecutablePath, "ProcessRuns");
        }

        foreach (var snap in DeserializeInventorySnapshot(machine.DiscoveredInventoryJson))
            MergeCandidate(map, snap.ProcessName, snap.DisplayName ?? snap.ProcessName, snap.ExecutablePath, snap.Source ?? "AgentInventory");

        foreach (var p in proposals.Values)
            MergeCandidate(map, p.ProcessName, p.DisplayName, p.ExecutablePath, p.Source);

        var catalogEntries = await catalog.GetForProcessNamesAsync(map.Keys, ct);
        var suggestions = catalogEntries
            .Where(e => e.SuggestedGroup is not null)
            .GroupBy(e => e.ProcessName, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);

        return map.Values
            .Select(c =>
            {
                var row = ToProposedApp(c, ctx);
                var status = ResolveInventoryStatus(row, tracked, proposals);
                suggestions.TryGetValue(row.ProcessName, out var suggestion);
                return new ClassifiedProcessRow(
                    row.ProcessName,
                    row.DisplayName,
                    row.ExecutablePath,
                    row.Source,
                    row.Group,
                    row.AllowForPresence,
                    row.ExcludedFromDefaultTracking,
                    status,
                    suggestion?.SuggestedGroup,
                    suggestion?.SuggestionReason);
            })
            .OrderBy(r => ProcessClassification.GroupSortOrder(r.Group))
            .ThenBy(r => r.ProcessName, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static void MergeCandidate(
        Dictionary<string, MutableCandidate> map,
        string processName,
        string displayName,
        string? executablePath,
        string source,
        string? fileVersion = null,
        string? productVersion = null,
        string? companyName = null,
        string? fileDescription = null)
    {
        if (map.TryGetValue(processName, out var existing))
        {
            if (string.IsNullOrWhiteSpace(existing.ExecutablePath) && !string.IsNullOrWhiteSpace(executablePath))
                existing.ExecutablePath = executablePath.Trim();
            if (!string.Equals(existing.DisplayName, displayName, StringComparison.OrdinalIgnoreCase)
                && !string.IsNullOrWhiteSpace(displayName)
                && displayName != processName)
            {
                existing.DisplayName = displayName;
            }
            if (string.Equals(source, "AgentInventory", StringComparison.OrdinalIgnoreCase))
                existing.Source = source;
            existing.FileVersion ??= fileVersion;
            existing.ProductVersion ??= productVersion;
            existing.CompanyName ??= companyName;
            existing.FileDescription ??= fileDescription;
            return;
        }

        map[processName] = new MutableCandidate
        {
            ProcessName = processName,
            DisplayName = displayName,
            ExecutablePath = string.IsNullOrWhiteSpace(executablePath) ? null : executablePath.Trim(),
            Source = source,
            FileVersion = fileVersion,
            ProductVersion = productVersion,
            CompanyName = companyName,
            FileDescription = fileDescription
        };
    }

    private static ProposedApp ToProposedApp(MutableCandidate candidate, ProcessClassificationContext ctx)
    {
        var classification = ProcessClassification.Classify(candidate.ProcessName, ctx);
        return new ProposedApp(
            candidate.ProcessName,
            candidate.DisplayName,
            candidate.Source,
            candidate.ExecutablePath,
            classification.Group,
            classification.AllowForPresence,
            classification.ExcludedFromDefaultTracking);
    }

    private static InventoryStatus ResolveInventoryStatus(
        ProposedApp row,
        IReadOnlySet<string> tracked,
        IReadOnlyDictionary<string, ProposedApp> proposals)
    {
        if (tracked.Contains(row.ProcessName))
            return InventoryStatus.Tracked;
        if (proposals.ContainsKey(row.ProcessName))
            return InventoryStatus.Proposed;
        if (row.ExcludedFromDefaultTracking)
            return InventoryStatus.Excluded;
        return InventoryStatus.Available;
    }

    private sealed class MutableCandidate
    {
        public required string ProcessName { get; init; }
        public string DisplayName { get; set; } = "";
        public string? ExecutablePath { get; set; }
        public string Source { get; set; } = "ProcessRuns";
        public string? FileVersion { get; set; }
        public string? ProductVersion { get; set; }
        public string? CompanyName { get; set; }
        public string? FileDescription { get; set; }
    }

    public async Task QueueFirstSeenAnalysisAsync(Machine machine, CancellationToken ct)
    {
        if (machine.AppsAnalyzedAt is not null || machine.AppAnalysisStatus != AppAnalysisStatus.None)
            return;
        if (machine.PendingAppAnalysis)
            return;

        machine.PendingAppAnalysis = true;
        await AuditAsync("analyze-queued",
            $"First-seen: queued app analysis for {machine.Hostname}.",
            hostname: machine.Hostname, actor: "system", ct: ct);
    }

    public async Task<IReadOnlyList<TeamAppListOption>> GetTeamListsForHostAsync(string hostname, CancellationToken ct)
    {
        var machine = await db.Machines.AsNoTracking().FirstOrDefaultAsync(m => m.Hostname == hostname, ct);
        if (machine is null) return [];

        var usernames = await db.Sessions.AsNoTracking()
            .Where(s => s.MachineId == machine.Id)
            .Select(s => s.Username)
            .Distinct()
            .ToListAsync(ct);

        var shortNames = usernames
            .Select(u => u.Contains('\\') ? u.Split('\\').Last() : u)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var personTeams = await db.PersonTeams.AsNoTracking().Include(p => p.Team).ToListAsync(ct);
        var teamIds = personTeams
            .Where(p => shortNames.Contains(p.Username) || usernames.Contains(p.Username, StringComparer.OrdinalIgnoreCase))
            .Select(p => p.TeamId)
            .ToHashSet();

        var lists = await db.AppLists.AsNoTracking()
            .Include(a => a.Entries)
            .Include(a => a.Team)
            .Where(a => a.TeamId != null)
            .OrderBy(a => a.Name)
            .ToListAsync(ct);

        // Show all team-linked lists; flag those matching users seen on this machine.
        return lists
            .Select(a => new TeamAppListOption(
                a.Id,
                a.Name,
                a.Team?.Name,
                a.Entries.Count,
                a.TeamId is int tid && teamIds.Contains(tid),
                a.Entries.Select(e => e.ProcessName).OrderBy(x => x, StringComparer.OrdinalIgnoreCase).ToList()))
            .OrderByDescending(o => o.MatchesMachineUsers)
            .ThenBy(o => o.ListName, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    public static List<ProposedApp> DeserializeProposals(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return [];
        try
        {
            return JsonSerializer.Deserialize<List<ProposedApp>>(json, JsonOptions) ?? [];
        }
        catch
        {
            return [];
        }
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

    private static string SummarizeList(AppList list) =>
        $"{list.Entries.Count} process(es) [{string.Join(", ", list.Entries.Select(e => e.ProcessName).Take(12))}{(list.Entries.Count > 12 ? ",…" : "")}]";

    private static int IndexOfAny(List<string> headers, params string[] names)
    {
        for (var i = 0; i < headers.Count; i++)
            if (names.Any(n => string.Equals(headers[i], n, StringComparison.OrdinalIgnoreCase)))
                return i;
        return -1;
    }

    private static string? GetCol(List<string> cols, int idx) =>
        idx >= 0 && idx < cols.Count ? cols[idx].Trim().Trim('"') : null;

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

    private record UploadRow(string ListName, string ProcessName, string? DisplayName, string? TeamHint);

    public record ProposedApp(
        string ProcessName,
        string DisplayName,
        string Source,
        string? ExecutablePath = null,
        AppGroup Group = AppGroup.Specialization,
        bool AllowForPresence = false,
        bool ExcludedFromDefaultTracking = false);

    public enum InventoryStatus
    {
        Excluded,
        Available,
        Proposed,
        Tracked
    }

    public record ClassifiedProcessRow(
        string ProcessName,
        string DisplayName,
        string? ExecutablePath,
        string Source,
        AppGroup Group,
        bool AllowForPresence,
        bool ExcludedFromDefaultTracking,
        InventoryStatus Status,
        AppGroup? SuggestedGroup = null,
        string? SuggestionReason = null);

    public record AnalysisResult(string Hostname, IReadOnlyList<ProposedApp> Proposals, AppAnalysisStatus Status, bool queuedForAgent, int NewCatalogCount = 0);
    public record ActiveAppListInfo(int AppListId, string Name, string? TeamName, ConfigScope Scope, string? ScopeValue, bool IsAutoDiscovered, int EntryCount);
    public record MachineAppListsView(string Hostname, IReadOnlyList<ActiveAppListInfo> ActiveLists, IReadOnlyList<string> MergedProcesses, AppAnalysisStatus AnalysisStatus, IReadOnlyList<ProposedApp> PendingProposals);
    public record TeamAppListOption(int AppListId, string ListName, string? TeamName, int EntryCount, bool MatchesMachineUsers, IReadOnlyList<string> ProcessNames);
}
