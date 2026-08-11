using System.Text.Json;
using Heimdall.Api.Data;
using Heimdall.Api.Services;
using Heimdall.Shared.Contracts;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;

namespace Heimdall.Api.Pages;

public class ConfigModel(HeimdallDbContext db) : PageModel
{
    public List<TrackingConfig> TrackingConfigs { get; private set; } = [];
    public TrackingConfig? EditingConfig { get; private set; }
    public List<MetricPolicy> Policies { get; private set; } = [];
    public IReadOnlyList<MachineHierarchy.RegionNode> Tree { get; private set; } = [];
    public List<string> Countries { get; private set; } = [];
    public List<string> Groups { get; private set; } = [];
    public List<ProcessListItemVm> IncludeItems { get; private set; } = [];
    public List<ProcessListItemVm> ExcludeItems { get; private set; } = [];
    public int SoeCatalogCount { get; private set; }
    public HashSet<string> SoeProcessNames { get; private set; } = new(StringComparer.OrdinalIgnoreCase);

    [BindProperty]
    public int? EditingConfigId { get; set; }

    [BindProperty]
    public string ConfigName { get; set; } = "";

    [BindProperty]
    public ConfigScope SamplingScope { get; set; } = ConfigScope.All;

    [BindProperty]
    public int SampleIntervalSeconds { get; set; } = 30;

    [BindProperty]
    public int UploadIntervalSeconds { get; set; } = 60;

    [BindProperty]
    public double MinCpuPercentToTrack { get; set; }

    [BindProperty]
    public string IncludeProcessesJson { get; set; } = "[]";

    [BindProperty]
    public string ExcludeProcessesJson { get; set; } = "[]";

    [BindProperty]
    public List<string> SelectedRegions { get; set; } = [];

    [BindProperty]
    public List<string> SelectedOffices { get; set; } = [];

    [BindProperty]
    public List<string> SelectedMachines { get; set; } = [];

    [BindProperty]
    public List<string> SelectedCountries { get; set; } = [];

    [BindProperty]
    public List<string> SelectedGroups { get; set; } = [];

    // Process list actions
    [BindProperty]
    public ProcessListKind ListKind { get; set; }

    [BindProperty]
    public string? ProcessNameInput { get; set; }

    [BindProperty]
    public List<string> SelectedProcesses { get; set; } = [];

    [BindProperty]
    public int PauseDays { get; set; }

    [BindProperty]
    public int PauseHours { get; set; }

    [BindProperty]
    public int PauseMinutes { get; set; } = 30;

    [BindProperty]
    public string? PausePreset { get; set; }

    [BindProperty]
    public int DeleteConfigId { get; set; }

    // Metric policy form
    [BindProperty]
    public int? EditingPolicyId { get; set; }

    [BindProperty]
    public string PolicyName { get; set; } = "";

    [BindProperty]
    public MetricType PolicyMetricType { get; set; } = MetricType.HighRam;

    [BindProperty]
    public ConfigScope PolicyScope { get; set; } = ConfigScope.All;

    [BindProperty]
    public string? PolicyScopeValue { get; set; }

    [BindProperty]
    public bool PolicyEnabled { get; set; } = true;

    [BindProperty]
    public double? RamPercent { get; set; }

    [BindProperty]
    public double? RamMb { get; set; }

    [BindProperty]
    public double? GpuPercent { get; set; }

    [BindProperty]
    public double? DiskReadMBps { get; set; }

    [BindProperty]
    public double? DiskWriteMBps { get; set; }

    [BindProperty]
    public double? DiskCombinedMBps { get; set; }

    [BindProperty]
    public int PolicyId { get; set; }

    [BindProperty]
    public List<string> PolicySelectedRegions { get; set; } = [];

    [BindProperty]
    public List<string> PolicySelectedOffices { get; set; } = [];

    [BindProperty]
    public List<string> PolicySelectedMachines { get; set; } = [];

    public async Task OnGetAsync(int? edit, int? editPolicy, bool? @new)
    {
        await LoadAsync(@new == true ? null : edit);
        if (@new == true)
        {
            EditingConfig = null;
            EditingConfigId = null;
            ConfigName = "";
            SamplingScope = ConfigScope.Machine;
            SampleIntervalSeconds = 30;
            UploadIntervalSeconds = 60;
            MinCpuPercentToTrack = 0;
            IncludeProcessesJson = "[]";
            ExcludeProcessesJson = "[]";
            IncludeItems = [];
            ExcludeItems = [];
        }
        else
        {
            BindSamplingForm();
        }

        if (editPolicy is int pid)
        {
            var p = Policies.FirstOrDefault(x => x.Id == pid);
            if (p is not null)
                BindPolicy(p);
        }
        else
        {
            PolicyName = "";
            PolicyMetricType = MetricType.HighRam;
            PolicyScope = ConfigScope.All;
            PolicyScopeValue = null;
            PolicyEnabled = true;
            RamPercent = 85;
            RamMb = 16000;
            GpuPercent = 90;
            DiskReadMBps = 200;
            DiskWriteMBps = 200;
            DiskCombinedMBps = 350;
        }
    }

    public async Task<IActionResult> OnPostSaveTrackingAsync()
    {
        await LoadAsync(EditingConfigId);

        var sample = Math.Clamp(SampleIntervalSeconds, 10, 600);
        var upload = Math.Clamp(UploadIntervalSeconds, 30, 3600);
        var minCpu = Math.Clamp(MinCpuPercentToTrack, 0, 100);
        var includes = Deserialize(IncludeProcessesJson);
        var excludes = Deserialize(ExcludeProcessesJson);

        if (EditingConfigId is int id)
        {
            var cfg = await db.TrackingConfigs.FirstOrDefaultAsync(c => c.Id == id);
            if (cfg is null)
            {
                TempData["Error"] = "Sampling config not found.";
                return RedirectToPage();
            }

            if (!string.IsNullOrWhiteSpace(ConfigName))
                cfg.Name = ConfigName.Trim();
            cfg.SampleIntervalSeconds = sample;
            cfg.UploadIntervalSeconds = upload;
            cfg.MinCpuPercentToTrack = minCpu;
            cfg.IncludeProcessesJson = JsonSerializer.Serialize(includes);
            cfg.ExcludeProcessesJson = JsonSerializer.Serialize(excludes);
            cfg.Priority = ConfigService.ScopeRank(cfg.Scope) * 10;
            cfg.IsEnabled = true;

            await db.SaveChangesAsync();
            TempData["Message"] = $"Updated sampling config “{cfg.Name}”. Agents pick this up on next refresh.";
            return RedirectToPage(null, new { edit = cfg.Id });
        }

        var targets = BuildSamplingTargets();
        if (targets.Count == 0)
        {
            TempData["Error"] = SamplingScope == ConfigScope.All
                ? "Could not resolve global sampling config."
                : "Select at least one target for this scope.";
            return RedirectToPage();
        }

        var created = 0;
        var updated = 0;
        foreach (var (scope, scopeValue) in targets)
        {
            TrackingConfig? cfg;
            if (scope == ConfigScope.All)
            {
                cfg = await db.TrackingConfigs
                    .Where(c => c.Scope == ConfigScope.All)
                    .OrderByDescending(c => c.Priority)
                    .FirstOrDefaultAsync();
            }
            else
            {
                cfg = await db.TrackingConfigs.FirstOrDefaultAsync(c =>
                    c.Scope == scope && c.ScopeValue == scopeValue);
            }

            if (cfg is null)
            {
                cfg = new TrackingConfig
                {
                    Scope = scope,
                    ScopeValue = scope == ConfigScope.All ? null : scopeValue,
                    ConfigRefreshSeconds = 300
                };
                db.TrackingConfigs.Add(cfg);
                created++;
            }
            else
            {
                updated++;
            }

            cfg.Name = string.IsNullOrWhiteSpace(ConfigName)
                ? DefaultConfigName(scope, scopeValue)
                : (targets.Count == 1 ? ConfigName.Trim() : $"{ConfigName.Trim()} · {FormatScope(scope, scopeValue)}");
            cfg.SampleIntervalSeconds = sample;
            cfg.UploadIntervalSeconds = upload;
            cfg.MinCpuPercentToTrack = minCpu;
            cfg.IncludeProcessesJson = JsonSerializer.Serialize(includes);
            cfg.ExcludeProcessesJson = JsonSerializer.Serialize(excludes);
            cfg.Priority = ConfigService.ScopeRank(scope) * 10;
            cfg.IsEnabled = true;
        }

        await db.SaveChangesAsync();
        int? lastId = targets.Count == 1
            ? await db.TrackingConfigs
                .Where(c => c.Scope == targets[0].Scope &&
                            (targets[0].ScopeValue == null
                                ? c.ScopeValue == null
                                : c.ScopeValue == targets[0].ScopeValue))
                .OrderByDescending(c => c.Id)
                .Select(c => (int?)c.Id)
                .FirstOrDefaultAsync()
            : null;

        TempData["Message"] = targets.Count == 1
            ? "Sampling config saved. Agents pick this up on next refresh."
            : $"Saved {created} new / {updated} updated sampling config(s) across {targets.Count} scope target(s).";
        return lastId is int eid
            ? RedirectToPage(null, new { edit = eid })
            : RedirectToPage();
    }

    public async Task<IActionResult> OnPostDeleteTrackingAsync()
    {
        var cfg = await db.TrackingConfigs.FirstOrDefaultAsync(c => c.Id == DeleteConfigId);
        if (cfg is null)
            return RedirectToPage();

        if (cfg.Scope == ConfigScope.All)
        {
            var allCount = await db.TrackingConfigs.CountAsync(c => c.Scope == ConfigScope.All);
            if (allCount <= 1)
            {
                TempData["Error"] = "Cannot delete the last global (All machines) sampling config.";
                return RedirectToPage();
            }
        }

        db.TrackingConfigs.Remove(cfg);
        await db.SaveChangesAsync();
        TempData["Message"] = $"Deleted sampling config “{cfg.Name}”.";
        return RedirectToPage();
    }

    public async Task<IActionResult> OnPostAddProcessAsync()
    {
        if (EditingConfigId is not int id)
        {
            TempData["Error"] = "Save a sampling config first, then add processes.";
            return RedirectToPage();
        }

        var name = ConfigService.NormalizeProcessName(ProcessNameInput ?? "");
        if (name.Length == 0)
        {
            TempData["Error"] = "Enter a process name.";
            return RedirectToPage(null, new { edit = id });
        }

        var cfg = await db.TrackingConfigs.FirstAsync(c => c.Id == id);
        var list = ListKind == ProcessListKind.Include
            ? Deserialize(cfg.IncludeProcessesJson)
            : Deserialize(cfg.ExcludeProcessesJson);

        if (!list.Contains(name, StringComparer.OrdinalIgnoreCase))
            list.Add(name);

        if (ListKind == ProcessListKind.Include)
            cfg.IncludeProcessesJson = JsonSerializer.Serialize(list);
        else
            cfg.ExcludeProcessesJson = JsonSerializer.Serialize(list);

        await db.SaveChangesAsync();
        TempData["Message"] = $"Added “{name}” to {ListKind} list.";
        return RedirectToPage(null, new { edit = id });
    }

    public async Task<IActionResult> OnPostAutogenerateSoeExcludesAsync()
    {
        if (EditingConfigId is not int id)
        {
            TempData["Error"] = "Save / open a sampling config first, then autogenerate SOE excludes.";
            return RedirectToPage();
        }

        var cfg = await db.TrackingConfigs.FirstAsync(c => c.Id == id);
        var list = Deserialize(cfg.ExcludeProcessesJson);
        var before = list.Count;
        var soeNames = await db.SoeApps.AsNoTracking()
            .Select(s => s.ProcessName)
            .ToListAsync();
        if (soeNames.Count == 0)
        {
            // Fallback to in-code catalog if DB empty
            soeNames = SoeCatalog.Entries.Select(e => e.ProcessName).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        }

        foreach (var raw in soeNames)
        {
            var name = ConfigService.NormalizeProcessName(raw);
            if (name.Length == 0) continue;
            if (!list.Contains(name, StringComparer.OrdinalIgnoreCase))
                list.Add(name);
        }

        cfg.ExcludeProcessesJson = JsonSerializer.Serialize(list);
        await db.SaveChangesAsync();
        var added = list.Count - before;
        TempData["Message"] = added == 0
            ? $"SOE catalog already covered ({list.Count} excludes). Select SOE items and use Allow for diagnostics to temporarily track them."
            : $"Merged {added} SOE process(es) into Exclude ({list.Count} total). Pause/Allow for diagnostics expires automatically.";
        return RedirectToPage(null, new { edit = id });
    }

    public async Task<IActionResult> OnPostRemoveProcessesAsync()
    {
        if (EditingConfigId is not int id)
            return RedirectToPage();

        var selected = SelectedProcesses
            .Select(ConfigService.NormalizeProcessName)
            .Where(s => s.Length > 0)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (selected.Count == 0)
        {
            TempData["Error"] = "Select one or more processes to remove.";
            return RedirectToPage(null, new { edit = id });
        }

        var cfg = await db.TrackingConfigs.FirstAsync(c => c.Id == id);
        var list = ListKind == ProcessListKind.Include
            ? Deserialize(cfg.IncludeProcessesJson)
            : Deserialize(cfg.ExcludeProcessesJson);
        list = list.Where(p => !selected.Contains(p)).ToList();

        if (ListKind == ProcessListKind.Include)
            cfg.IncludeProcessesJson = JsonSerializer.Serialize(list);
        else
            cfg.ExcludeProcessesJson = JsonSerializer.Serialize(list);

        var pauses = await db.ProcessPauses
            .Where(p => p.TrackingConfigId == id && p.ListKind == ListKind)
            .ToListAsync();
        foreach (var pause in pauses.Where(p => selected.Contains(p.ProcessName)))
            db.ProcessPauses.Remove(pause);

        await db.SaveChangesAsync();
        TempData["Message"] = $"Removed {selected.Count} process(es) from {ListKind} list.";
        return RedirectToPage(null, new { edit = id });
    }

    public async Task<IActionResult> OnPostPauseProcessesAsync()
    {
        if (EditingConfigId is not int id)
        {
            TempData["Error"] = "Save a sampling config first, then pause processes.";
            return RedirectToPage();
        }

        var selected = SelectedProcesses
            .Select(ConfigService.NormalizeProcessName)
            .Where(s => s.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (selected.Count == 0)
        {
            TempData["Error"] = "Select one or more processes to pause.";
            return RedirectToPage(null, new { edit = id });
        }

        var until = ResolvePauseUntil();
        if (until <= DateTimeOffset.UtcNow)
        {
            TempData["Error"] = "Pick a pause duration (preset or days/hours/minutes).";
            return RedirectToPage(null, new { edit = id });
        }

        var cfg = await db.TrackingConfigs.FirstAsync(c => c.Id == id);
        var list = ListKind == ProcessListKind.Include
            ? Deserialize(cfg.IncludeProcessesJson)
            : Deserialize(cfg.ExcludeProcessesJson);
        var listSet = list.ToHashSet(StringComparer.OrdinalIgnoreCase);

        foreach (var name in selected)
        {
            if (!listSet.Contains(name))
                continue;

            var existing = await db.ProcessPauses.FirstOrDefaultAsync(p =>
                p.TrackingConfigId == id && p.ListKind == ListKind && p.ProcessName == name);
            if (existing is null)
            {
                db.ProcessPauses.Add(new ProcessPause
                {
                    TrackingConfigId = id,
                    ProcessName = name,
                    ListKind = ListKind,
                    PausedUntilUtc = until,
                    Reason = ListKind == ProcessListKind.Exclude ? "diagnostics" : "pause"
                });
            }
            else
            {
                existing.PausedUntilUtc = until;
                existing.Reason = ListKind == ProcessListKind.Exclude ? "diagnostics" : "pause";
            }
        }

        await db.SaveChangesAsync();
        var meaning = ListKind == ProcessListKind.Include
            ? "not tracked"
            : "exclude paused — allowed through for diagnostics";
        TempData["Message"] = $"Paused {selected.Count} {ListKind} process(es) until {until:u} ({meaning}).";
        return RedirectToPage(null, new { edit = id });
    }

    public async Task<IActionResult> OnPostUnpauseProcessesAsync()
    {
        if (EditingConfigId is not int id)
            return RedirectToPage();

        var selected = SelectedProcesses
            .Select(ConfigService.NormalizeProcessName)
            .Where(s => s.Length > 0)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (selected.Count == 0)
        {
            TempData["Error"] = "Select one or more paused processes to unpause.";
            return RedirectToPage(null, new { edit = id });
        }

        var pauses = await db.ProcessPauses
            .Where(p => p.TrackingConfigId == id && p.ListKind == ListKind)
            .ToListAsync();
        var removed = 0;
        foreach (var pause in pauses.Where(p => selected.Contains(p.ProcessName)))
        {
            db.ProcessPauses.Remove(pause);
            removed++;
        }

        await db.SaveChangesAsync();
        TempData["Message"] = removed == 0
            ? "No matching pauses to clear."
            : $"Unpaused {removed} process(es).";
        return RedirectToPage(null, new { edit = id });
    }

    public async Task<IActionResult> OnPostSavePolicyAsync()
    {
        ResolvePolicyScopeFromTree();

        if (string.IsNullOrWhiteSpace(PolicyName))
        {
            TempData["Error"] = "Policy name is required.";
            return RedirectToPage(null, EditingConfigId is int cid ? new { edit = cid } : null);
        }

        if (PolicyScope != ConfigScope.All && string.IsNullOrWhiteSpace(PolicyScopeValue))
        {
            TempData["Error"] = "Pick a scope target (region, office, or machine) or type a scope value.";
            return RedirectToPage(null, new { edit = EditingConfigId, editPolicy = EditingPolicyId });
        }

        MetricPolicy policy;
        if (EditingPolicyId is int id)
        {
            policy = await db.MetricPolicies.FirstAsync(p => p.Id == id);
        }
        else
        {
            policy = new MetricPolicy();
            db.MetricPolicies.Add(policy);
        }

        policy.Name = PolicyName.Trim();
        policy.MetricType = PolicyMetricType;
        policy.Scope = PolicyScope;
        policy.ScopeValue = PolicyScope == ConfigScope.All ? null : PolicyScopeValue?.Trim();
        policy.IsEnabled = PolicyEnabled;
        policy.RamPercentThreshold = PolicyMetricType == MetricType.HighRam ? RamPercent : null;
        policy.RamMbThreshold = PolicyMetricType == MetricType.HighRam ? RamMb : null;
        policy.GpuPercentThreshold = PolicyMetricType == MetricType.HighGpu ? GpuPercent : null;
        policy.DiskReadMBpsThreshold = PolicyMetricType == MetricType.HighDisk ? DiskReadMBps : null;
        policy.DiskWriteMBpsThreshold = PolicyMetricType == MetricType.HighDisk ? DiskWriteMBps : null;
        policy.DiskCombinedMBpsThreshold = PolicyMetricType == MetricType.HighDisk ? DiskCombinedMBps : null;

        await db.SaveChangesAsync();
        TempData["Message"] = EditingPolicyId.HasValue ? "Metric policy updated." : "Metric policy created.";
        return RedirectToPage(null, EditingConfigId is int eid ? new { edit = eid } : null);
    }

    public async Task<IActionResult> OnPostTogglePolicyAsync()
    {
        var policy = await db.MetricPolicies.FirstOrDefaultAsync(p => p.Id == PolicyId);
        if (policy is not null)
        {
            policy.IsEnabled = !policy.IsEnabled;
            await db.SaveChangesAsync();
            TempData["Message"] = $"Policy “{policy.Name}” {(policy.IsEnabled ? "enabled" : "disabled")}.";
        }
        return RedirectToPage(null, EditingConfigId is int id ? new { edit = id } : null);
    }

    public async Task<IActionResult> OnPostDeletePolicyAsync()
    {
        var policy = await db.MetricPolicies.FirstOrDefaultAsync(p => p.Id == PolicyId);
        if (policy is not null)
        {
            db.MetricPolicies.Remove(policy);
            await db.SaveChangesAsync();
            TempData["Message"] = $"Deleted policy “{policy.Name}”.";
        }
        return RedirectToPage(null, EditingConfigId is int id ? new { edit = id } : null);
    }

    public static string FormatScope(ConfigScope scope, string? scopeValue) =>
        scope == ConfigScope.All ? "Global (all machines)" : $"{scope}: {scopeValue}";

    private DateTimeOffset ResolvePauseUntil()
    {
        var now = DateTimeOffset.UtcNow;
        if (!string.IsNullOrWhiteSpace(PausePreset))
        {
            return PausePreset.Trim().ToLowerInvariant() switch
            {
                "15m" => now.AddMinutes(15),
                "1h" => now.AddHours(1),
                "4h" => now.AddHours(4),
                "1d" => now.AddDays(1),
                "7d" => now.AddDays(7),
                _ => now
            };
        }

        var span = TimeSpan.FromDays(Math.Max(0, PauseDays))
                   + TimeSpan.FromHours(Math.Max(0, PauseHours))
                   + TimeSpan.FromMinutes(Math.Max(0, PauseMinutes));
        return span <= TimeSpan.Zero ? now : now.Add(span);
    }

    private List<(ConfigScope Scope, string? ScopeValue)> BuildSamplingTargets()
    {
        var targets = new List<(ConfigScope Scope, string? ScopeValue)>();

        switch (SamplingScope)
        {
            case ConfigScope.All:
                targets.Add((ConfigScope.All, null));
                break;
            case ConfigScope.Region:
                foreach (var r in SelectedRegions.Where(s => !string.IsNullOrWhiteSpace(s)))
                    targets.Add((ConfigScope.Region, r.Trim()));
                break;
            case ConfigScope.Office:
                foreach (var o in SelectedOffices.Where(s => !string.IsNullOrWhiteSpace(s)))
                    targets.Add((ConfigScope.Office, o.Trim()));
                break;
            case ConfigScope.Machine:
                foreach (var m in SelectedMachines.Where(s => !string.IsNullOrWhiteSpace(s)))
                    targets.Add((ConfigScope.Machine, m.Trim()));
                break;
            case ConfigScope.Country:
                foreach (var c in SelectedCountries.Where(s => !string.IsNullOrWhiteSpace(s)))
                    targets.Add((ConfigScope.Country, c.Trim()));
                break;
            case ConfigScope.Group:
                foreach (var g in SelectedGroups.Where(s => !string.IsNullOrWhiteSpace(s)))
                    targets.Add((ConfigScope.Group, g.Trim()));
                break;
        }

        return targets
            .DistinctBy(t => (t.Scope, t.ScopeValue?.ToLowerInvariant() ?? ""))
            .ToList();
    }

    private void BindSamplingForm()
    {
        if (EditingConfig is not null)
        {
            EditingConfigId = EditingConfig.Id;
            ConfigName = EditingConfig.Name;
            SamplingScope = EditingConfig.Scope;
            SampleIntervalSeconds = EditingConfig.SampleIntervalSeconds;
            UploadIntervalSeconds = EditingConfig.UploadIntervalSeconds;
            MinCpuPercentToTrack = EditingConfig.MinCpuPercentToTrack;
            IncludeProcessesJson = EditingConfig.IncludeProcessesJson;
            ExcludeProcessesJson = EditingConfig.ExcludeProcessesJson;
            return;
        }

        // Default: edit global All config if present
        var global = TrackingConfigs.FirstOrDefault(c => c.Scope == ConfigScope.All);
        if (global is not null)
        {
            EditingConfig = global;
            EditingConfigId = global.Id;
            ConfigName = global.Name;
            SamplingScope = ConfigScope.All;
            SampleIntervalSeconds = global.SampleIntervalSeconds;
            UploadIntervalSeconds = global.UploadIntervalSeconds;
            MinCpuPercentToTrack = global.MinCpuPercentToTrack;
            IncludeProcessesJson = global.IncludeProcessesJson;
            ExcludeProcessesJson = global.ExcludeProcessesJson;
            BuildProcessItems(global);
        }
        else
        {
            SampleIntervalSeconds = 30;
            UploadIntervalSeconds = 60;
            MinCpuPercentToTrack = 0;
            IncludeProcessesJson = "[]";
            ExcludeProcessesJson = "[]";
            SamplingScope = ConfigScope.All;
            ConfigName = "Default (all machines)";
        }
    }

    private void BuildProcessItems(TrackingConfig cfg)
    {
        var now = DateTimeOffset.UtcNow;
        // SQLite EF DateTimeOffset filters are unreliable — load then filter in memory.
        var pauses = db.ProcessPauses.AsNoTracking()
            .Where(p => p.TrackingConfigId == cfg.Id)
            .AsEnumerable()
            .Where(p => p.PausedUntilUtc > now)
            .ToList();

        IncludeItems = ToItems(Deserialize(cfg.IncludeProcessesJson), pauses, ProcessListKind.Include);
        ExcludeItems = ToItems(Deserialize(cfg.ExcludeProcessesJson), pauses, ProcessListKind.Exclude);
    }

    private List<ProcessListItemVm> ToItems(
        List<string> names, List<ProcessPause> pauses, ProcessListKind kind)
    {
        return names
            .OrderBy(n => n, StringComparer.OrdinalIgnoreCase)
            .Select(n =>
            {
                var pause = pauses
                    .Where(p => p.ListKind == kind &&
                                string.Equals(p.ProcessName, n, StringComparison.OrdinalIgnoreCase))
                    .OrderByDescending(p => p.PausedUntilUtc)
                    .FirstOrDefault();
                return new ProcessListItemVm(
                    n,
                    pause?.PausedUntilUtc,
                    SoeProcessNames.Contains(n),
                    pause?.Reason);
            })
            .ToList();
    }

    private void ResolvePolicyScopeFromTree()
    {
        if (PolicyScope == ConfigScope.All)
            return;

        if (!string.IsNullOrWhiteSpace(PolicyScopeValue))
            return;

        if (PolicySelectedRegions.Count > 0)
        {
            PolicyScope = ConfigScope.Region;
            PolicyScopeValue = PolicySelectedRegions[0];
        }
        else if (PolicySelectedOffices.Count > 0)
        {
            PolicyScope = ConfigScope.Office;
            PolicyScopeValue = PolicySelectedOffices[0];
        }
        else if (PolicySelectedMachines.Count > 0)
        {
            PolicyScope = ConfigScope.Machine;
            PolicyScopeValue = PolicySelectedMachines[0];
        }
    }

    private void BindPolicy(MetricPolicy p)
    {
        EditingPolicyId = p.Id;
        PolicyName = p.Name;
        PolicyMetricType = p.MetricType;
        PolicyScope = p.Scope;
        PolicyScopeValue = p.ScopeValue;
        PolicyEnabled = p.IsEnabled;
        RamPercent = p.RamPercentThreshold;
        RamMb = p.RamMbThreshold;
        GpuPercent = p.GpuPercentThreshold;
        DiskReadMBps = p.DiskReadMBpsThreshold;
        DiskWriteMBps = p.DiskWriteMBpsThreshold;
        DiskCombinedMBps = p.DiskCombinedMBpsThreshold;
    }

    private async Task LoadAsync(int? editId)
    {
        TrackingConfigs = await db.TrackingConfigs.AsNoTracking()
            .OrderByDescending(c => c.Priority)
            .ThenBy(c => c.Name)
            .ToListAsync();

        Policies = await db.MetricPolicies.AsNoTracking().OrderBy(p => p.MetricType).ThenBy(p => p.Name).ToListAsync();

        var soeApps = await db.SoeApps.AsNoTracking().ToListAsync();
        if (soeApps.Count == 0)
            soeApps = SoeCatalog.CreateSeedEntities().ToList();
        SoeCatalogCount = soeApps.Count;
        SoeProcessNames = soeApps.Select(s => s.ProcessName).ToHashSet(StringComparer.OrdinalIgnoreCase);

        var machines = await db.Machines.AsNoTracking().ToListAsync();
        foreach (var m in machines)
            MachineHierarchy.EnsureDefaults(m);
        Tree = MachineHierarchy.BuildTree(machines);
        Countries = machines
            .Select(m => m.Country)
            .Where(c => !string.IsNullOrWhiteSpace(c))
            .Select(c => c!)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(c => c, StringComparer.OrdinalIgnoreCase)
            .ToList();
        Groups = machines
            .Select(m => m.MachineGroup)
            .Where(g => !string.IsNullOrWhiteSpace(g))
            .Select(g => g!)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(g => g, StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (editId is int id)
        {
            EditingConfig = await db.TrackingConfigs.FirstOrDefaultAsync(c => c.Id == id);
            if (EditingConfig is not null)
                BuildProcessItems(EditingConfig);
        }
    }

    private static string DefaultConfigName(ConfigScope scope, string? scopeValue) =>
        scope == ConfigScope.All
            ? "Default (all machines)"
            : $"Sampling · {scope} · {scopeValue}";

    private static List<string> Deserialize(string json)
    {
        try
        {
            return JsonSerializer.Deserialize<List<string>>(json)?
                .Select(ConfigService.NormalizeProcessName)
                .Where(s => s.Length > 0)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList() ?? [];
        }
        catch
        {
            return [];
        }
    }

    public record ProcessListItemVm(
        string Name,
        DateTimeOffset? PausedUntilUtc,
        bool IsSoe = false,
        string? PauseReason = null);
}
