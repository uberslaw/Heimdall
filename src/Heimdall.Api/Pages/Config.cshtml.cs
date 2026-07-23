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
    public TrackingConfig Config { get; private set; } = null!;
    public List<KnownApp> KnownApps { get; private set; } = [];
    public List<MetricPolicy> Policies { get; private set; } = [];
    public IReadOnlyList<MachineHierarchy.RegionNode> Tree { get; private set; } = [];

    [BindProperty]
    public int SampleIntervalSeconds { get; set; }

    [BindProperty]
    public int UploadIntervalSeconds { get; set; }

    [BindProperty]
    public double MinCpuPercentToTrack { get; set; }

    [BindProperty]
    public string IncludeProcessesText { get; set; } = "";

    [BindProperty]
    public string ExcludeProcessesText { get; set; } = "";

    [BindProperty]
    public string? NewAppDisplayName { get; set; }

    [BindProperty]
    public string? NewAppProcessName { get; set; }

    [BindProperty]
    public List<int> EnabledAppIds { get; set; } = [];

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

    public async Task OnGetAsync(int? edit)
    {
        await LoadAsync();
        SampleIntervalSeconds = Config.SampleIntervalSeconds;
        UploadIntervalSeconds = Config.UploadIntervalSeconds;
        MinCpuPercentToTrack = Config.MinCpuPercentToTrack;
        IncludeProcessesText = string.Join(Environment.NewLine, Deserialize(Config.IncludeProcessesJson));
        ExcludeProcessesText = string.Join(Environment.NewLine, Deserialize(Config.ExcludeProcessesJson));
        EnabledAppIds = KnownApps.Where(a => a.EnabledByDefault).Select(a => a.Id).ToList();

        if (edit is int id)
        {
            var p = Policies.FirstOrDefault(x => x.Id == id);
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
        await LoadAsync();
        Config.SampleIntervalSeconds = Math.Clamp(SampleIntervalSeconds, 10, 600);
        Config.UploadIntervalSeconds = Math.Clamp(UploadIntervalSeconds, 30, 3600);
        Config.MinCpuPercentToTrack = Math.Clamp(MinCpuPercentToTrack, 0, 100);
        Config.IncludeProcessesJson = JsonSerializer.Serialize(SplitLines(IncludeProcessesText));
        Config.ExcludeProcessesJson = JsonSerializer.Serialize(SplitLines(ExcludeProcessesText));

        foreach (var app in KnownApps)
            app.EnabledByDefault = EnabledAppIds.Contains(app.Id);

        await db.SaveChangesAsync();
        TempData["Message"] = "Tracking config saved. Agents pick this up on next refresh.";
        return RedirectToPage();
    }

    public async Task<IActionResult> OnPostAddAppAsync()
    {
        if (!string.IsNullOrWhiteSpace(NewAppProcessName) && !string.IsNullOrWhiteSpace(NewAppDisplayName))
        {
            var process = ConfigService.NormalizeProcessName(NewAppProcessName);
            if (!await db.KnownApps.AnyAsync(a => a.ProcessName == process))
            {
                db.KnownApps.Add(new KnownApp
                {
                    DisplayName = NewAppDisplayName.Trim(),
                    ProcessName = process,
                    EnabledByDefault = true
                });
                await db.SaveChangesAsync();
                TempData["Message"] = $"Added {NewAppDisplayName.Trim()}.";
            }
        }

        return RedirectToPage();
    }

    public async Task<IActionResult> OnPostSavePolicyAsync()
    {
        ResolveScopeFromTree();

        if (string.IsNullOrWhiteSpace(PolicyName))
        {
            TempData["Error"] = "Policy name is required.";
            return RedirectToPage();
        }

        if (PolicyScope != ConfigScope.All && string.IsNullOrWhiteSpace(PolicyScopeValue))
        {
            TempData["Error"] = "Pick a scope target (region, office, or machine) or type a scope value.";
            return RedirectToPage(null, new { edit = EditingPolicyId });
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
        return RedirectToPage();
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
        return RedirectToPage();
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
        return RedirectToPage();
    }

    private void ResolveScopeFromTree()
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

    private async Task LoadAsync()
    {
        Config = await db.TrackingConfigs.OrderByDescending(c => c.Priority).FirstAsync(c => c.Scope == ConfigScope.All);
        KnownApps = await db.KnownApps.OrderBy(a => a.DisplayName).ToListAsync();
        Policies = await db.MetricPolicies.AsNoTracking().OrderBy(p => p.MetricType).ThenBy(p => p.Name).ToListAsync();

        var machines = await db.Machines.AsNoTracking().ToListAsync();
        foreach (var m in machines)
            MachineHierarchy.EnsureDefaults(m);
        Tree = MachineHierarchy.BuildTree(machines);
    }

    private static List<string> Deserialize(string json)
    {
        try { return JsonSerializer.Deserialize<List<string>>(json) ?? []; }
        catch { return []; }
    }

    private static List<string> SplitLines(string text) =>
        text.Split(['\r', '\n', ',', ';'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(ConfigService.NormalizeProcessName)
            .Where(s => s.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
}
