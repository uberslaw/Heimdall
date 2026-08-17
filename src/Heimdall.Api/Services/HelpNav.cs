namespace Heimdall.Api.Services;

/// <summary>
/// Resolves the Help page fragment for the global header Help control.
/// Pages may override via <c>ViewData["HelpFragment"]</c>.
/// </summary>
public static class HelpNav
{
    public const string ViewDataKey = "HelpFragment";

    public static string? ResolveFragment(HttpRequest request, object? viewDataFragment)
    {
        if (viewDataFragment is string s && !string.IsNullOrWhiteSpace(s))
            return s.Trim().TrimStart('#');

        var path = request.Path.Value ?? "";
        if (path.Length > 1 && path.EndsWith('/'))
            path = path.TrimEnd('/');

        if (path.Equals("/", StringComparison.OrdinalIgnoreCase)
            || path.Equals("/Fleet", StringComparison.OrdinalIgnoreCase)
            || path.Equals("/Ops", StringComparison.OrdinalIgnoreCase))
        {
            var tab = (request.Query["tab"].FirstOrDefault() ?? "machines").Trim().ToLowerInvariant();
            return tab switch
            {
                "live" => "fleet-live",
                "sessions" => "sessions",
                "online" => "remote-machines",
                "storage" => "storage",
                "clients" => "client-version",
                "cost" => "cost",
                "stats" => "stats",
                "machines" => "machines",
                _ => "machines"
            };
        }

        if (path.Equals("/Flood", StringComparison.OrdinalIgnoreCase)
            || path.Equals("/HistoricalDashboard", StringComparison.OrdinalIgnoreCase)
            || path.Equals("/HistoricalDashboardMachine", StringComparison.OrdinalIgnoreCase))
        {
            var tab = request.Query["tab"].FirstOrDefault() ?? "live";
            if (string.Equals(tab, "sims", StringComparison.OrdinalIgnoreCase))
                return "fleet-sims";
            if (string.Equals(tab, "behaviour", StringComparison.OrdinalIgnoreCase))
                return "tuflow-behaviour";
            return "historical-dashboard";
        }

        if (path.Equals("/Index", StringComparison.OrdinalIgnoreCase)
            || path.Equals("/MachineUtilDrilldown", StringComparison.OrdinalIgnoreCase))
            return "machines";

        if (path.Equals("/Machine", StringComparison.OrdinalIgnoreCase))
            return "machine-detail";

        if (path.Equals("/User", StringComparison.OrdinalIgnoreCase))
            return "user-detail";

        if (path.Equals("/Apps", StringComparison.OrdinalIgnoreCase))
            return "applications";

        if (path.Equals("/AppLists", StringComparison.OrdinalIgnoreCase))
            return "app-lists";

        if (path.Equals("/AppListChangelog", StringComparison.OrdinalIgnoreCase))
            return "app-list-changelog";

        if (path.Equals("/Discovery", StringComparison.OrdinalIgnoreCase))
            return "discovery";

        if (path.Equals("/Socratize", StringComparison.OrdinalIgnoreCase))
            return "socratize";

        if (path.Equals("/Sessions", StringComparison.OrdinalIgnoreCase))
            return "sessions";

        if (path.Equals("/RemoteMachines", StringComparison.OrdinalIgnoreCase)
            || path.Equals("/FleetLive", StringComparison.OrdinalIgnoreCase))
            return path.Equals("/FleetLive", StringComparison.OrdinalIgnoreCase) ? "fleet-live" : "remote-machines";

        if (path.Equals("/TuflowRuns", StringComparison.OrdinalIgnoreCase))
            return "tuflow-runs";

        if (path.Equals("/TuflowBehaviour", StringComparison.OrdinalIgnoreCase))
            return "tuflow-behaviour";

        if (path.Equals("/StaffAccess", StringComparison.OrdinalIgnoreCase))
            return "staff-access";

        if (path.Equals("/RemoteWorkstations", StringComparison.OrdinalIgnoreCase)
            || path.Equals("/RemoteWorkstationSoftware", StringComparison.OrdinalIgnoreCase))
            return "remote-workstations";

        if (path.Equals("/RemoteAccessGroups", StringComparison.OrdinalIgnoreCase))
            return "remote-access-groups";

        if (path.Equals("/Auth", StringComparison.OrdinalIgnoreCase))
            return "auth";

        if (path.Equals("/Config", StringComparison.OrdinalIgnoreCase))
            return "tracking-config";

        if (path.StartsWith("/Teams", StringComparison.OrdinalIgnoreCase))
            return "teams";

        if (path.Equals("/Utilization", StringComparison.OrdinalIgnoreCase))
            return "utilization";

        if (path.Equals("/Finance", StringComparison.OrdinalIgnoreCase))
            return "finance";

        if (path.Equals("/Cost", StringComparison.OrdinalIgnoreCase))
            return "cost";

        if (path.Equals("/Storage", StringComparison.OrdinalIgnoreCase))
            return "storage";

        if (path.Equals("/Stats", StringComparison.OrdinalIgnoreCase)
            || path.Equals("/ClientVersion", StringComparison.OrdinalIgnoreCase))
            return path.Equals("/Stats", StringComparison.OrdinalIgnoreCase) ? "stats" : "client-version";

        if (path.Equals("/Theme", StringComparison.OrdinalIgnoreCase))
            return "admin-preferences";

        if (path.Equals("/Usage", StringComparison.OrdinalIgnoreCase))
            return "site-usage";

        if (path.Equals("/Diagnostics", StringComparison.OrdinalIgnoreCase))
            return "diagnostics";

        return null;
    }
}
