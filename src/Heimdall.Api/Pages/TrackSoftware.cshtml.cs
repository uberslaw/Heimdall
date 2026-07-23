using Heimdall.Api.Data;
using Heimdall.Api.Services;
using Heimdall.Shared.Contracts;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;

namespace Heimdall.Api.Pages;

public class TrackSoftwareModel(HeimdallDbContext db, ConfigService config) : PageModel
{
    public IReadOnlyList<MachineHierarchy.RegionNode> Tree { get; private set; } = [];
    public List<KnownApp> KnownApps { get; private set; } = [];
    public List<DiscoveredApp> Discovered { get; private set; } = [];

    [BindProperty]
    public string Source { get; set; } = "Known";

    [BindProperty]
    public int? KnownAppId { get; set; }

    [BindProperty]
    public string? DiscoveredKey { get; set; }

    [BindProperty]
    public string? CustomProcessName { get; set; }

    [BindProperty]
    public string? CustomDisplayName { get; set; }

    [BindProperty]
    public string? CustomPath { get; set; }

    [BindProperty]
    public List<string> SelectedRegions { get; set; } = [];

    [BindProperty]
    public List<string> SelectedOffices { get; set; } = [];

    [BindProperty]
    public List<string> SelectedMachines { get; set; } = [];

    public async Task OnGetAsync()
    {
        await LoadAsync();
    }

    public async Task<IActionResult> OnPostAsync()
    {
        await LoadAsync();

        var scopes = BuildScopes();
        if (scopes.Count == 0)
        {
            TempData["Error"] = "Select at least one region, office, or machine.";
            return Page();
        }

        string processName;
        string? displayName;
        string? path = null;

        switch (Source)
        {
            case "Known":
            {
                var app = KnownApps.FirstOrDefault(a => a.Id == KnownAppId);
                if (app is null)
                {
                    TempData["Error"] = "Pick a known application.";
                    return Page();
                }
                processName = app.ProcessName;
                displayName = app.DisplayName;
                break;
            }
            case "Discovered":
            {
                var d = Discovered.FirstOrDefault(x => x.Key == DiscoveredKey);
                if (d is null)
                {
                    TempData["Error"] = "Pick a discovered process.";
                    return Page();
                }
                processName = d.ProcessName;
                displayName = d.ProcessName;
                path = d.Path;
                break;
            }
            default:
            {
                if (string.IsNullOrWhiteSpace(CustomProcessName))
                {
                    TempData["Error"] = "Enter an executable name.";
                    return Page();
                }
                processName = CustomProcessName;
                displayName = string.IsNullOrWhiteSpace(CustomDisplayName) ? CustomProcessName : CustomDisplayName;
                path = CustomPath;
                break;
            }
        }

        await config.TrackSoftwareAsync(processName, displayName, path, scopes, HttpContext.RequestAborted);

        var scopeSummary = string.Join(", ", scopes.Select(s =>
            s.Scope == ConfigScope.All ? "All" : $"{s.Scope}:{s.ScopeValue}"));
        TempData["Message"] = $"Tracking “{ConfigService.NormalizeProcessName(processName)}” for {scopeSummary}. Agents pick this up on next config refresh.";
        return RedirectToPage("/Apps");
    }

    private List<(ConfigScope Scope, string ScopeValue)> BuildScopes()
    {
        var scopes = new List<(ConfigScope, string)>();

        foreach (var r in SelectedRegions.Where(s => !string.IsNullOrWhiteSpace(s)))
            scopes.Add((ConfigScope.Region, r.Trim()));

        foreach (var o in SelectedOffices.Where(s => !string.IsNullOrWhiteSpace(s)))
        {
            // Skip office if its parent region is already selected
            var region = o.Contains('/') ? o.Split('/')[0] : null;
            if (region is not null && SelectedRegions.Contains(region, StringComparer.OrdinalIgnoreCase))
                continue;
            scopes.Add((ConfigScope.Office, o.Trim()));
        }

        var machinesInSelectedParents = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var region in Tree)
        {
            var regionSelected = SelectedRegions.Contains(region.Name, StringComparer.OrdinalIgnoreCase);
            foreach (var office in region.Offices)
            {
                var officeKey = $"{region.Name}/{office.Name}";
                var officeSelected = regionSelected ||
                                     SelectedOffices.Contains(officeKey, StringComparer.OrdinalIgnoreCase);
                if (officeSelected)
                {
                    foreach (var m in office.Machines)
                        machinesInSelectedParents.Add(m.Hostname);
                }
            }
        }

        foreach (var host in SelectedMachines.Where(s => !string.IsNullOrWhiteSpace(s)))
        {
            if (machinesInSelectedParents.Contains(host))
                continue;
            scopes.Add((ConfigScope.Machine, host.Trim()));
        }

        return scopes;
    }

    private async Task LoadAsync()
    {
        var machines = await db.Machines.AsNoTracking().ToListAsync();
        foreach (var m in machines)
            MachineHierarchy.EnsureDefaults(m);
        Tree = MachineHierarchy.BuildTree(machines);
        KnownApps = await db.KnownApps.AsNoTracking().OrderBy(a => a.DisplayName).ToListAsync();

        var knownNames = new HashSet<string>(KnownApps.Select(a => a.ProcessName), StringComparer.OrdinalIgnoreCase);
        var runs = await db.ProcessRuns.AsNoTracking()
            .Select(r => new { r.ProcessName, r.ExecutablePath })
            .ToListAsync();

        Discovered = runs
            .GroupBy(r => r.ProcessName, StringComparer.OrdinalIgnoreCase)
            .Select(g =>
            {
                var path = g.Select(x => x.ExecutablePath).FirstOrDefault(p => !string.IsNullOrWhiteSpace(p));
                return new DiscoveredApp(
                    $"{g.Key}|{path}",
                    g.Key,
                    path
                );
            })
            .Where(d => !knownNames.Contains(d.ProcessName))
            .OrderBy(d => d.ProcessName, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    public record DiscoveredApp(string Key, string ProcessName, string? Path);
}
