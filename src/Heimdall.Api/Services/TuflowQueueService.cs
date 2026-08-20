using System.Text.Json;
using Heimdall.Api.Data;
using Heimdall.Shared;
using Heimdall.Shared.Contracts;
using Microsoft.EntityFrameworkCore;

namespace Heimdall.Api.Services;

/// <summary>
/// TUFLOW Runner-like queue: multi-sim items per host or unassigned (fleet), scenario/event matrix,
/// priority, cancel/rerun, import/export. Dispatch into PendingTuflowStartJson happens in
/// <see cref="TuflowRunService.GetPendingAsync"/>.
/// </summary>
public class TuflowQueueService(HeimdallDbContext db, FleetDashboardService fleetDashboard)
{
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true };

    public async Task<TuflowQueuePage> GetPageAsync(int? machineId, CancellationToken ct)
    {
        var live = await fleetDashboard.GetLiveFleetAsync(ct);
        var hosts = live
            .OrderBy(l => l.Hostname, StringComparer.OrdinalIgnoreCase)
            .Select(l => new TuflowQueueHostOption(l.MachineId, l.Hostname, l.FriendlyName, l.TuflowRunning, l.Status.ToString()))
            .ToList();

        var templates = await db.TuflowQueues.AsNoTracking()
            .Where(q => q.IsTemplate)
            .OrderBy(q => q.Name)
            .Select(q => new TuflowQueueTemplateRow(q.Id, q.Name, q.Items.Count, q.UpdatedUtc))
            .ToListAsync(ct);

        Machine? machine = null;
        if (machineId is int mid)
            machine = await db.Machines.AsNoTracking().FirstOrDefaultAsync(m => m.Id == mid, ct);

        var queue = await EnsureMachineQueueAsync(machineId, create: false, ct);
        var items = new List<TuflowQueueItemView>();
        if (queue is not null)
        {
            items = await db.TuflowQueueItems.AsNoTracking()
                .Where(i => i.QueueId == queue.Id)
                .OrderBy(i => i.Priority)
                .ThenBy(i => i.Id)
                .Select(i => new TuflowQueueItemView(
                    i.Id,
                    i.Priority,
                    i.State,
                    i.AssignedMachineId,
                    i.AssignedMachine != null ? i.AssignedMachine.Hostname : null,
                    i.LaunchMode,
                    i.ExePath,
                    i.TcfPath,
                    i.CmdPath,
                    i.WorkingDirectory,
                    i.ScenariosJson,
                    i.EventsJson,
                    i.ResultsFolder,
                    i.RunName,
                    i.RequestedBy,
                    i.RunId,
                    i.CreatedUtc,
                    i.StartedUtc,
                    i.EndedUtc,
                    i.ErrorSummary))
                .ToListAsync(ct);
        }

        var fleetQueued = await db.TuflowQueueItems.AsNoTracking()
            .Where(i => i.State == TuflowQueueItemStates.Queued && i.AssignedMachineId == null)
            .OrderBy(i => i.Priority)
            .ThenBy(i => i.Id)
            .Take(80)
            .Select(i => new TuflowQueueItemView(
                i.Id, i.Priority, i.State, i.AssignedMachineId,
                null, i.LaunchMode, i.ExePath, i.TcfPath, i.CmdPath, i.WorkingDirectory,
                i.ScenariosJson, i.EventsJson, i.ResultsFolder, i.RunName, i.RequestedBy,
                i.RunId, i.CreatedUtc, i.StartedUtc, i.EndedUtc, i.ErrorSummary))
            .ToListAsync(ct);

        var activeItems = await db.TuflowQueueItems.AsNoTracking()
            .Where(i => i.State == TuflowQueueItemStates.Running || i.State == TuflowQueueItemStates.Dispatching)
            .OrderBy(i => i.AssignedMachineId)
            .ThenBy(i => i.Priority)
            .Select(i => new TuflowQueueItemView(
                i.Id, i.Priority, i.State, i.AssignedMachineId,
                i.AssignedMachine != null ? i.AssignedMachine.Hostname : null,
                i.LaunchMode, i.ExePath, i.TcfPath, i.CmdPath, i.WorkingDirectory,
                i.ScenariosJson, i.EventsJson, i.ResultsFolder, i.RunName, i.RequestedBy,
                i.RunId, i.CreatedUtc, i.StartedUtc, i.EndedUtc, i.ErrorSummary))
            .ToListAsync(ct);

        return new TuflowQueuePage(
            hosts,
            machineId,
            machine?.Hostname,
            machine?.FriendlyName,
            Math.Max(1, machine?.TuflowMaxConcurrentRuns ?? 1),
            machine?.TuflowMaxGpuCards,
            machine?.TuflowMaxCpuThreads,
            queue?.Id,
            queue?.Name ?? "Queue",
            items,
            fleetQueued,
            activeItems,
            templates);
    }

    public async Task<(bool Ok, string? Error)> SaveHostSettingsAsync(
        int machineId, int maxConcurrent, int? maxGpu, int? maxCpu, CancellationToken ct)
    {
        var machine = await db.Machines.FirstOrDefaultAsync(m => m.Id == machineId, ct);
        if (machine is null)
            return (false, "Machine not found.");
        if (!await IsFloodEnrolledAsync(machineId, ct))
            return (false, "Machine is not Flood-enrolled.");

        machine.TuflowMaxConcurrentRuns = Math.Clamp(maxConcurrent, 1, 8);
        machine.TuflowMaxGpuCards = maxGpu is int g && g > 0 ? g : null;
        machine.TuflowMaxCpuThreads = maxCpu is int t && t > 0 ? t : null;
        await db.SaveChangesAsync(ct);
        return (true, null);
    }

    public async Task<(bool Ok, string? Error, int Added)> AddMatrixAsync(
        int? machineId,
        bool fleetUnassigned,
        string? runName,
        string? requestedBy,
        string launchMode,
        string? exePath,
        string? tcfPath,
        string? cmdPath,
        string? workingDirectory,
        string? resultsFolder,
        IReadOnlyList<IReadOnlyList<string>> scenarioGroups,
        IReadOnlyList<IReadOnlyList<string>> eventGroups,
        CancellationToken ct)
    {
        if (!fleetUnassigned)
        {
            if (machineId is not int mid)
                return (false, "Pick a Flood machine, or add to the fleet (unassigned) queue.", 0);
            if (!await IsFloodEnrolledAsync(mid, ct))
                return (false, "Machine is not Flood-enrolled.", 0);
        }

        if (TuflowLaunchPath.ValidateLaunch(launchMode, exePath, tcfPath, cmdPath, workingDirectory, resultsFolder) is { } pathErr)
            return (false, pathErr, 0);

        var sCombos = CartesianScenarios(scenarioGroups);
        var eCombos = CartesianScenarios(eventGroups);
        var total = sCombos.Count * eCombos.Count;
        if (total > 500)
            return (false, $"That matrix is {total} simulations (cap is 500). Narrow the -s / -e groups.", 0);

        var queue = fleetUnassigned
            ? await EnsureFleetQueueAsync(ct)
            : await EnsureMachineQueueAsync(machineId, create: true, ct);
        if (queue is null)
            return (false, "Could not create queue.", 0);

        var maxPriority = await db.TuflowQueueItems.Where(i => i.QueueId == queue.Id).Select(i => (int?)i.Priority).MaxAsync(ct) ?? -1;
        var added = 0;
        var now = DateTimeOffset.UtcNow;

        foreach (var scenarios in sCombos)
        {
            foreach (var itemEvents in eCombos)
            {
                var suffix = FormatComboSuffix(scenarios, itemEvents);
                var itemName = string.IsNullOrWhiteSpace(runName)
                    ? suffix
                    : string.IsNullOrEmpty(suffix) ? runName!.Trim() : $"{runName.Trim()} {suffix}";

                db.TuflowQueueItems.Add(new TuflowQueueItem
                {
                    QueueId = queue.Id,
                    Priority = ++maxPriority,
                    State = TuflowQueueItemStates.Queued,
                    AssignedMachineId = fleetUnassigned ? null : machineId,
                    LaunchMode = string.Equals(launchMode, TuflowLaunchModes.Cmd, StringComparison.OrdinalIgnoreCase)
                        ? TuflowLaunchModes.Cmd
                        : TuflowLaunchModes.ExeTcf,
                    ExePath = exePath?.Trim() ?? "",
                    TcfPath = tcfPath?.Trim() ?? "",
                    CmdPath = string.IsNullOrWhiteSpace(cmdPath) ? null : cmdPath.Trim(),
                    WorkingDirectory = string.IsNullOrWhiteSpace(workingDirectory) ? null : workingDirectory.Trim(),
                    ScenariosJson = JsonSerializer.Serialize(scenarios),
                    EventsJson = JsonSerializer.Serialize(itemEvents),
                    ResultsFolder = string.IsNullOrWhiteSpace(resultsFolder) ? null : resultsFolder.Trim(),
                    RunName = itemName,
                    RequestedBy = requestedBy,
                    CreatedUtc = now
                });
                added++;
            }
        }

        queue.UpdatedUtc = now;
        await db.SaveChangesAsync(ct);
        return (true, null, added);
    }

    public async Task<(bool Ok, string? Error)> AddOneOffMirrorAsync(
        int machineId,
        string runId,
        string runName,
        string launchMode,
        string exePath,
        string tcfPath,
        string? cmdPath,
        string? workingDirectory,
        IReadOnlyList<string> scenarios,
        IReadOnlyList<string> events,
        string? resultsFolder,
        string? requestedBy,
        string state,
        CancellationToken ct)
    {
        var queue = await EnsureMachineQueueAsync(machineId, create: true, ct);
        if (queue is null)
            return (false, "Could not create queue.");

        var maxPriority = await db.TuflowQueueItems.Where(i => i.QueueId == queue.Id).Select(i => (int?)i.Priority).MaxAsync(ct) ?? -1;
        db.TuflowQueueItems.Add(new TuflowQueueItem
        {
            QueueId = queue.Id,
            Priority = maxPriority + 1,
            State = state,
            AssignedMachineId = machineId,
            LaunchMode = launchMode,
            ExePath = exePath,
            TcfPath = tcfPath,
            CmdPath = cmdPath,
            WorkingDirectory = workingDirectory,
            ScenariosJson = JsonSerializer.Serialize(scenarios),
            EventsJson = JsonSerializer.Serialize(events),
            ResultsFolder = resultsFolder,
            RunName = runName,
            RequestedBy = requestedBy,
            RunId = runId,
            CreatedUtc = DateTimeOffset.UtcNow,
            StartedUtc = TuflowRunService.IsActiveRunState(state) || state == TuflowQueueItemStates.Dispatching
                ? DateTimeOffset.UtcNow
                : null
        });
        queue.UpdatedUtc = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(ct);
        return (true, null);
    }

    public async Task<(bool Ok, string? Error)> CancelItemAsync(int itemId, CancellationToken ct)
    {
        var item = await db.TuflowQueueItems.FirstOrDefaultAsync(i => i.Id == itemId, ct);
        if (item is null)
            return (false, "Queue item not found.");
        if (item.State is not TuflowQueueItemStates.Queued)
            return (false, "Only queued items can be cancelled. Stop a running sim from TUFLOW Runs.");
        item.State = TuflowQueueItemStates.Cancelled;
        item.EndedUtc = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(ct);
        return (true, null);
    }

    public async Task<(bool Ok, string? Error)> RerunItemAsync(int itemId, CancellationToken ct)
    {
        var item = await db.TuflowQueueItems.AsNoTracking().FirstOrDefaultAsync(i => i.Id == itemId, ct);
        if (item is null)
            return (false, "Queue item not found.");

        var clone = new TuflowQueueItem
        {
            QueueId = item.QueueId,
            Priority = await NextPriorityAsync(item.QueueId, ct),
            State = TuflowQueueItemStates.Queued,
            AssignedMachineId = item.AssignedMachineId,
            LaunchMode = item.LaunchMode,
            ExePath = item.ExePath,
            TcfPath = item.TcfPath,
            CmdPath = item.CmdPath,
            WorkingDirectory = item.WorkingDirectory,
            ScenariosJson = item.ScenariosJson,
            EventsJson = item.EventsJson,
            ResultsFolder = item.ResultsFolder,
            RunName = item.RunName,
            RequestedBy = item.RequestedBy,
            CreatedUtc = DateTimeOffset.UtcNow
        };
        db.TuflowQueueItems.Add(clone);
        await db.SaveChangesAsync(ct);
        return (true, null);
    }

    public async Task<(bool Ok, string? Error)> MoveAsync(int itemId, int delta, CancellationToken ct)
    {
        var item = await db.TuflowQueueItems.FirstOrDefaultAsync(i => i.Id == itemId, ct);
        if (item is null)
            return (false, "Queue item not found.");
        if (item.State != TuflowQueueItemStates.Queued)
            return (false, "Only queued items can be reordered.");

        var siblings = await db.TuflowQueueItems
            .Where(i => i.QueueId == item.QueueId && i.State == TuflowQueueItemStates.Queued)
            .OrderBy(i => i.Priority)
            .ThenBy(i => i.Id)
            .ToListAsync(ct);
        var idx = siblings.FindIndex(s => s.Id == item.Id);
        var swap = idx + delta;
        if (idx < 0 || swap < 0 || swap >= siblings.Count)
            return (true, null);

        (siblings[idx].Priority, siblings[swap].Priority) = (siblings[swap].Priority, siblings[idx].Priority);
        await db.SaveChangesAsync(ct);
        return (true, null);
    }

    public async Task<(bool Ok, string? Error)> AssignItemAsync(int itemId, int? machineId, CancellationToken ct)
    {
        var item = await db.TuflowQueueItems.FirstOrDefaultAsync(i => i.Id == itemId, ct);
        if (item is null)
            return (false, "Queue item not found.");
        if (item.State != TuflowQueueItemStates.Queued)
            return (false, "Only queued items can be reassigned.");
        if (machineId is int mid && !await IsFloodEnrolledAsync(mid, ct))
            return (false, "Target is not Flood-enrolled.");
        item.AssignedMachineId = machineId;
        await db.SaveChangesAsync(ct);
        return (true, null);
    }

    public async Task<(bool Ok, string? Error)> SaveAsTemplateAsync(int sourceQueueId, string name, CancellationToken ct)
    {
        var source = await db.TuflowQueues.Include(q => q.Items).FirstOrDefaultAsync(q => q.Id == sourceQueueId, ct);
        if (source is null)
            return (false, "Queue not found.");
        var now = DateTimeOffset.UtcNow;
        var template = new TuflowQueue
        {
            MachineId = null,
            Name = string.IsNullOrWhiteSpace(name) ? $"Template {now:yyyy-MM-dd HH:mm}" : name.Trim(),
            IsTemplate = true,
            SavedUtc = now,
            UpdatedUtc = now
        };
        db.TuflowQueues.Add(template);
        await db.SaveChangesAsync(ct);

        var p = 0;
        foreach (var i in source.Items.Where(x => x.State != TuflowQueueItemStates.Cancelled).OrderBy(x => x.Priority).ThenBy(x => x.Id))
        {
            db.TuflowQueueItems.Add(new TuflowQueueItem
            {
                QueueId = template.Id,
                Priority = p++,
                State = TuflowQueueItemStates.Queued,
                AssignedMachineId = null,
                LaunchMode = i.LaunchMode,
                ExePath = i.ExePath,
                TcfPath = i.TcfPath,
                CmdPath = i.CmdPath,
                WorkingDirectory = i.WorkingDirectory,
                ScenariosJson = i.ScenariosJson,
                EventsJson = i.EventsJson,
                ResultsFolder = i.ResultsFolder,
                RunName = i.RunName,
                RequestedBy = i.RequestedBy,
                CreatedUtc = now
            });
        }

        await db.SaveChangesAsync(ct);
        return (true, null);
    }

    public async Task<(bool Ok, string? Error, int Added)> ApplyTemplateAsync(int templateId, int? machineId, bool fleetUnassigned, CancellationToken ct)
    {
        var template = await db.TuflowQueues.AsNoTracking()
            .Include(q => q.Items)
            .FirstOrDefaultAsync(q => q.Id == templateId && q.IsTemplate, ct);
        if (template is null)
            return (false, "Template not found.", 0);

        var queue = fleetUnassigned
            ? await EnsureFleetQueueAsync(ct)
            : await EnsureMachineQueueAsync(machineId, create: true, ct);
        if (queue is null)
            return (false, "Could not create queue.", 0);

        var maxPriority = await db.TuflowQueueItems.Where(i => i.QueueId == queue.Id).Select(i => (int?)i.Priority).MaxAsync(ct) ?? -1;
        var now = DateTimeOffset.UtcNow;
        var added = 0;
        foreach (var i in template.Items.OrderBy(x => x.Priority).ThenBy(x => x.Id))
        {
            db.TuflowQueueItems.Add(new TuflowQueueItem
            {
                QueueId = queue.Id,
                Priority = ++maxPriority,
                State = TuflowQueueItemStates.Queued,
                AssignedMachineId = fleetUnassigned ? null : machineId,
                LaunchMode = i.LaunchMode,
                ExePath = i.ExePath,
                TcfPath = i.TcfPath,
                CmdPath = i.CmdPath,
                WorkingDirectory = i.WorkingDirectory,
                ScenariosJson = i.ScenariosJson,
                EventsJson = i.EventsJson,
                ResultsFolder = i.ResultsFolder,
                RunName = i.RunName,
                RequestedBy = i.RequestedBy,
                CreatedUtc = now
            });
            added++;
        }

        queue.UpdatedUtc = now;
        await db.SaveChangesAsync(ct);
        return (true, null, added);
    }

    public async Task<(bool Ok, string? Error, int Added)> ImportJsonAsync(
        int? machineId, bool fleetUnassigned, string json, string? requestedBy, CancellationToken ct)
    {
        TuflowQueueFileDto? file;
        try
        {
            file = JsonSerializer.Deserialize<TuflowQueueFileDto>(json, JsonOptions);
        }
        catch (Exception ex)
        {
            return (false, "Could not parse JSON: " + ex.Message, 0);
        }

        if (file?.Items is null || file.Items.Count == 0)
            return (false, "No items in file. Use Heimdall format heimdall-tuflow-queue-v1 (TUFLOW Runner native files are not documented).", 0);

        var queue = fleetUnassigned
            ? await EnsureFleetQueueAsync(ct)
            : await EnsureMachineQueueAsync(machineId, create: true, ct);
        if (queue is null)
            return (false, "Could not create queue.", 0);

        var maxPriority = await db.TuflowQueueItems.Where(i => i.QueueId == queue.Id).Select(i => (int?)i.Priority).MaxAsync(ct) ?? -1;
        var now = DateTimeOffset.UtcNow;
        var added = 0;
        foreach (var row in file.Items)
        {
            if (TuflowLaunchPath.ValidateLaunch(
                    row.LaunchMode, row.ExePath, row.TcfPath, row.CmdPath, row.WorkingDirectory, row.ResultsFolder) is { } pathErr)
                return (false, pathErr + $" (item '{row.RunName}')", 0);

            db.TuflowQueueItems.Add(new TuflowQueueItem
            {
                QueueId = queue.Id,
                Priority = ++maxPriority,
                State = TuflowQueueItemStates.Queued,
                AssignedMachineId = fleetUnassigned ? null : machineId,
                LaunchMode = string.Equals(row.LaunchMode, TuflowLaunchModes.Cmd, StringComparison.OrdinalIgnoreCase)
                    ? TuflowLaunchModes.Cmd
                    : TuflowLaunchModes.ExeTcf,
                ExePath = row.ExePath ?? "",
                TcfPath = row.TcfPath ?? "",
                CmdPath = row.CmdPath,
                WorkingDirectory = row.WorkingDirectory,
                ScenariosJson = JsonSerializer.Serialize(row.Scenarios ?? []),
                EventsJson = JsonSerializer.Serialize(row.Events ?? []),
                ResultsFolder = row.ResultsFolder,
                RunName = row.RunName,
                RequestedBy = requestedBy,
                CreatedUtc = now
            });
            added++;
        }

        queue.UpdatedUtc = now;
        await db.SaveChangesAsync(ct);
        return (true, null, added);
    }

    public async Task<string> ExportJsonAsync(int queueId, CancellationToken ct)
    {
        var items = await db.TuflowQueueItems.AsNoTracking()
            .Where(i => i.QueueId == queueId && i.State != TuflowQueueItemStates.Cancelled)
            .OrderBy(i => i.Priority)
            .ThenBy(i => i.Id)
            .ToListAsync(ct);

        var file = new TuflowQueueFileDto
        {
            Format = "heimdall-tuflow-queue-v1",
            Items = items.Select(i => new TuflowQueueFileItemDto
            {
                RunName = i.RunName,
                LaunchMode = i.LaunchMode,
                ExePath = i.ExePath,
                TcfPath = i.TcfPath,
                CmdPath = i.CmdPath,
                WorkingDirectory = i.WorkingDirectory,
                Scenarios = DeserializeStringList(i.ScenariosJson),
                Events = DeserializeStringList(i.EventsJson),
                ResultsFolder = i.ResultsFolder
            }).ToList()
        };
        return JsonSerializer.Serialize(file, new JsonSerializerOptions { WriteIndented = true });
    }

    public async Task SyncItemFromRunAsync(string runId, string state, string? error, CancellationToken ct)
    {
        var item = await db.TuflowQueueItems.FirstOrDefaultAsync(i => i.RunId == runId, ct);
        if (item is null)
            return;

        if (TuflowRunService.IsActiveRunState(state) || state == TuflowQueueItemStates.Dispatching)
        {
            item.State = TuflowQueueItemStates.Running;
            item.StartedUtc ??= DateTimeOffset.UtcNow;
            return;
        }

        item.State = state switch
        {
            TuflowRunStates.Completed => TuflowQueueItemStates.Completed,
            TuflowRunStates.Failed => TuflowQueueItemStates.Failed,
            TuflowRunStates.Stopped => TuflowQueueItemStates.Stopped,
            _ => item.State
        };
        item.EndedUtc ??= DateTimeOffset.UtcNow;
        if (!string.IsNullOrWhiteSpace(error))
            item.ErrorSummary = error;
    }

    /// <summary>Atomically take the next queued item for this host (pinned first, then unassigned fleet).</summary>
    public async Task<TuflowQueueItem?> TryClaimNextForHostAsync(int machineId, CancellationToken ct)
    {
        var candidateId = await db.TuflowQueueItems
            .Where(i => i.State == TuflowQueueItemStates.Queued && i.AssignedMachineId == machineId)
            .OrderBy(i => i.Priority)
            .ThenBy(i => i.Id)
            .Select(i => (int?)i.Id)
            .FirstOrDefaultAsync(ct)
            ?? await db.TuflowQueueItems
                .Where(i => i.State == TuflowQueueItemStates.Queued && i.AssignedMachineId == null)
                .OrderBy(i => i.Priority)
                .ThenBy(i => i.Id)
                .Select(i => (int?)i.Id)
                .FirstOrDefaultAsync(ct);

        if (candidateId is null)
            return null;

        var n = await db.TuflowQueueItems
            .Where(i => i.Id == candidateId && i.State == TuflowQueueItemStates.Queued)
            .ExecuteUpdateAsync(s => s
                .SetProperty(x => x.State, TuflowQueueItemStates.Dispatching)
                .SetProperty(x => x.AssignedMachineId, machineId), ct);
        if (n == 0)
            return null;

        return await db.TuflowQueueItems.FirstOrDefaultAsync(i => i.Id == candidateId, ct);
    }

    public async Task<(int Waiting, int Active)> CountWorkAsync(CancellationToken ct)
    {
        var waiting = await db.TuflowQueueItems.CountAsync(i => i.State == TuflowQueueItemStates.Queued, ct);
        var active = await db.TuflowQueueItems.CountAsync(
            i => i.State == TuflowQueueItemStates.Running || i.State == TuflowQueueItemStates.Dispatching, ct);
        return (waiting, active);
    }

    public static List<string> DeserializeStringList(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
            return [];
        try
        {
            return JsonSerializer.Deserialize<List<string>>(json) ?? [];
        }
        catch
        {
            return [];
        }
    }

    /// <summary>
    /// Cartesian product of scenario groups (Runner: every category must have a selection).
    /// Empty groups are skipped. A single empty product yields one combo with no scenarios.
    /// </summary>
    public static List<List<string>> CartesianScenarios(IReadOnlyList<IReadOnlyList<string>> groups)
    {
        var cleaned = groups
            .Select(g => g.Where(s => !string.IsNullOrWhiteSpace(s)).Select(s => s.Trim()).Distinct(StringComparer.OrdinalIgnoreCase).ToList())
            .Where(g => g.Count > 0)
            .ToList();

        if (cleaned.Count == 0)
            return [[]];

        List<List<string>> acc = [[]];
        foreach (var group in cleaned)
        {
            var next = new List<List<string>>();
            foreach (var prefix in acc)
            foreach (var token in group)
            {
                var row = new List<string>(prefix) { token };
                next.Add(row);
            }
            acc = next;
        }

        return acc;
    }

    public static IReadOnlyList<IReadOnlyList<string>> ParseScenarioGroups(string? text)
    {
        // Lines = categories; commas/semicolons within a line = choices in that category.
        if (string.IsNullOrWhiteSpace(text))
            return [];
        return text.Replace("\r", "")
            .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(line => (IReadOnlyList<string>)line.Split([',', ';'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList())
            .Where(g => g.Count > 0)
            .ToList();
    }

    public static List<string> ParseTokenList(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return [];
        return text.Split([',', ';', ' ', '\t', '\n', '\r'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();
    }

    async Task<int> NextPriorityAsync(int queueId, CancellationToken ct) =>
        (await db.TuflowQueueItems.Where(i => i.QueueId == queueId).Select(i => (int?)i.Priority).MaxAsync(ct) ?? -1) + 1;

    async Task<TuflowQueue?> EnsureMachineQueueAsync(int? machineId, bool create, CancellationToken ct)
    {
        if (machineId is not int mid)
            return null;
        var existing = await db.TuflowQueues.FirstOrDefaultAsync(q => q.MachineId == mid && !q.IsTemplate, ct);
        if (existing is not null || !create)
            return existing;

        var now = DateTimeOffset.UtcNow;
        existing = new TuflowQueue
        {
            MachineId = mid,
            Name = "Machine queue",
            IsTemplate = false,
            SavedUtc = now,
            UpdatedUtc = now
        };
        db.TuflowQueues.Add(existing);
        await db.SaveChangesAsync(ct);
        return existing;
    }

    async Task<TuflowQueue> EnsureFleetQueueAsync(CancellationToken ct)
    {
        var existing = await db.TuflowQueues.FirstOrDefaultAsync(q => q.MachineId == null && !q.IsTemplate, ct);
        if (existing is not null)
            return existing;
        var now = DateTimeOffset.UtcNow;
        existing = new TuflowQueue
        {
            MachineId = null,
            Name = "Fleet unassigned",
            IsTemplate = false,
            SavedUtc = now,
            UpdatedUtc = now
        };
        db.TuflowQueues.Add(existing);
        await db.SaveChangesAsync(ct);
        return existing;
    }

    async Task<bool> IsFloodEnrolledAsync(int machineId, CancellationToken ct) =>
        await db.FleetDashboardMachines.AsNoTracking().AnyAsync(f => f.MachineId == machineId, ct);

    static string FormatComboSuffix(IReadOnlyList<string> scenarios, IReadOnlyList<string> events)
    {
        var parts = scenarios.Concat(events.Where(e => !string.IsNullOrEmpty(e))).ToList();
        return parts.Count == 0 ? "" : string.Join(" ", parts);
    }
}

public sealed record TuflowQueuePage(
    IReadOnlyList<TuflowQueueHostOption> Hosts,
    int? MachineId,
    string? Hostname,
    string? FriendlyName,
    int MaxConcurrentRuns,
    int? MaxGpuCards,
    int? MaxCpuThreads,
    int? QueueId,
    string QueueName,
    IReadOnlyList<TuflowQueueItemView> Items,
    IReadOnlyList<TuflowQueueItemView> FleetUnassigned,
    IReadOnlyList<TuflowQueueItemView> FleetActive,
    IReadOnlyList<TuflowQueueTemplateRow> Templates);

public sealed record TuflowQueueHostOption(int MachineId, string Hostname, string? FriendlyName, bool TuflowRunning, string? Status);

public sealed record TuflowQueueTemplateRow(int Id, string Name, int ItemCount, DateTimeOffset UpdatedUtc);

public sealed record TuflowQueueItemView(
    int Id,
    int Priority,
    string State,
    int? AssignedMachineId,
    string? AssignedHostname,
    string LaunchMode,
    string ExePath,
    string TcfPath,
    string? CmdPath,
    string? WorkingDirectory,
    string ScenariosJson,
    string EventsJson,
    string? ResultsFolder,
    string? RunName,
    string? RequestedBy,
    string? RunId,
    DateTimeOffset CreatedUtc,
    DateTimeOffset? StartedUtc,
    DateTimeOffset? EndedUtc,
    string? ErrorSummary);
