using Heimdall.Api.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace Heimdall.Api.Pages;

/// <summary>Fleet → Live: estate-wide live util dashboard (all known Machines). Not Flood-gated.</summary>
public class FleetLiveModel(FleetDashboardService fleet) : PageModel
{
    [BindProperty(SupportsGet = true)]
    public string? Q { get; set; }

    [BindProperty(SupportsGet = true)]
    public string StatusFilter { get; set; } = "all";

    public IReadOnlyList<FleetDashboardService.LiveFleetRow> LiveRows { get; private set; } = [];
    public IReadOnlyList<FleetDashboardService.LiveFleetRow> FilteredLiveRows { get; private set; } = [];
    public IReadOnlyList<TeamSection> Sections { get; private set; } = [];
    public int MachineCount { get; private set; }

    public async Task<IActionResult> OnGetAsync(CancellationToken ct)
    {
        if (!OpsPartial.IsPartial(Request))
            return OpsPartial.RedirectToFleetTab(Request, "live");

        LiveRows = await fleet.GetLiveFleetAsync(enrolledOnly: false, ct);
        MachineCount = LiveRows.Count;

        IEnumerable<FleetDashboardService.LiveFleetRow> filtered = LiveRows;
        if (!string.Equals(StatusFilter, "all", StringComparison.OrdinalIgnoreCase))
            filtered = filtered.Where(r => StatusMatches(r.Status, StatusFilter));

        if (!string.IsNullOrWhiteSpace(Q))
        {
            var q = Q.Trim();
            filtered = filtered.Where(r =>
                r.Hostname.Contains(q, StringComparison.OrdinalIgnoreCase)
                || (r.FriendlyName?.Contains(q, StringComparison.OrdinalIgnoreCase) ?? false)
                || (r.Username?.Contains(q, StringComparison.OrdinalIgnoreCase) ?? false)
                || (r.LastIp?.Contains(q, StringComparison.OrdinalIgnoreCase) ?? false)
                || (r.TeamName?.Contains(q, StringComparison.OrdinalIgnoreCase) ?? false));
        }

        FilteredLiveRows = filtered
            .OrderBy(r => r.TeamName ?? "\uFFFF")
            .ThenBy(r => r.DisplayName, StringComparer.OrdinalIgnoreCase)
            .ToList();

        Sections = BuildSections(FilteredLiveRows);
        return Page();
    }

    private static IReadOnlyList<TeamSection> BuildSections(IReadOnlyList<FleetDashboardService.LiveFleetRow> rows)
    {
        var sections = new List<TeamSection>();
        foreach (var g in rows
                     .GroupBy(r => r.TeamId)
                     .OrderBy(g => g.First().TeamName ?? "\uFFFF"))
        {
            var name = string.IsNullOrWhiteSpace(g.First().TeamName) ? "Unassigned" : g.First().TeamName!;
            sections.Add(new TeamSection(g.Key, name, g.OrderBy(r => r.DisplayName, StringComparer.OrdinalIgnoreCase).ToList()));
        }

        return sections;
    }

    private static bool StatusMatches(FleetDashboardService.FleetStatus status, string filter) =>
        filter.Trim().ToLowerInvariant() switch
        {
            "active" => status == FleetDashboardService.FleetStatus.Active,
            "idle" => status == FleetDashboardService.FleetStatus.Idle,
            "notrunning" or "not-running" or "not_running" or "na" or "n/a" =>
                status == FleetDashboardService.FleetStatus.NotRunning,
            _ => true
        };

    public sealed record TeamSection(
        int? TeamId,
        string TeamName,
        IReadOnlyList<FleetDashboardService.LiveFleetRow> Machines);
}
