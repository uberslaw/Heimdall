using Heimdall.Api.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace Heimdall.Api.Pages;

/// <summary>
/// Flood hub — tab shell combining Historical (live/analytics), Fleet Sims, and Flood enrollment.
/// Full Flood: AdminEmails ∪ FloodTeamEmails. Live-only: also FloodLiveEmails (Live tab only).
/// </summary>
public class FloodModel(FloodAccessGuard flood) : PageModel
{
    public static readonly (string Key, string Label, string PartialPath)[] Tabs =
    [
        ("live", "Live", "/HistoricalDashboard"),
        ("historical", "Historical", "/HistoricalDashboard"),
        ("queue", "Run Queue", "/TuflowQueue"),
        ("sims", "Fleet Sims", "/FleetSimProgress"),
        ("behaviour", "Run behaviour", "/TuflowBehaviour"),
        ("enroll", "Enrollment", "/HistoricalDashboard"),
    ];

    [BindProperty(SupportsGet = true)]
    public string Tab { get; set; } = "live";

    public string ActiveTabKey { get; private set; } = "live";

    public bool LiveOnly { get; private set; }

    public IReadOnlyList<(string Key, string Label, string PartialPath)> VisibleTabs { get; private set; } = Tabs;

    public async Task<IActionResult> OnGetAsync()
    {
        if (await flood.ForbidIfLiveDeniedAsync(HttpContext) is { } denied)
            return denied;

        LiveOnly = flood.IsLiveOnly(HttpContext);
        ActiveTabKey = NormalizeTab(Tab);
        if (LiveOnly && ActiveTabKey != "live")
            return Redirect("/Flood?tab=live");

        if (ActiveTabKey != "live" && await flood.ForbidIfDeniedAsync(HttpContext) is { } fullDenied)
            return fullDenied;

        Tab = ActiveTabKey;
        VisibleTabs = LiveOnly
            ? Tabs.Where(t => t.Key == "live").ToArray()
            : Tabs;
        return Page();
    }

    public static string NormalizeTab(string? tab)
    {
        var key = (tab ?? "live").Trim().ToLowerInvariant();
        return Tabs.Any(t => t.Key == key) ? key : "live";
    }

    public static string PartialUrl(string tabKey, HttpRequest request)
    {
        var tab = Tabs.FirstOrDefault(t => t.Key == tabKey);
        var path = tab.PartialPath ?? "/HistoricalDashboard";
        var qs = new List<string> { "partial=1" };

        if (tabKey is "live" or "historical" or "enroll")
            qs.Add("tab=" + Uri.EscapeDataString(tabKey));

        foreach (var kv in request.Query)
        {
            if (string.Equals(kv.Key, "tab", StringComparison.OrdinalIgnoreCase))
                continue;
            if (string.Equals(kv.Key, "partial", StringComparison.OrdinalIgnoreCase))
                continue;
            foreach (var v in kv.Value)
            {
                if (v is not null)
                    qs.Add($"{Uri.EscapeDataString(kv.Key)}={Uri.EscapeDataString(v)}");
            }
        }

        return path + "?" + string.Join("&", qs);
    }
}
