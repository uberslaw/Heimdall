using Heimdall.Api.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace Heimdall.Api.Pages;

/// <summary>
/// Flood hub — tab shell combining Historical (live/analytics), Fleet Sims, and Flood enrollment.
/// Flood-gated; TUFLOW Runs remains a separate Flood nav link.
/// </summary>
public class FloodModel(FloodAccessGuard flood) : PageModel
{
    public static readonly (string Key, string Label, string PartialPath)[] Tabs =
    [
        ("live", "Live", "/HistoricalDashboard"),
        ("historical", "Historical", "/HistoricalDashboard"),
        ("sims", "Fleet Sims", "/FleetSimProgress"),
        ("behaviour", "Run behaviour", "/TuflowBehaviour"),
        ("enroll", "Enrollment", "/HistoricalDashboard"),
    ];

    [BindProperty(SupportsGet = true)]
    public string Tab { get; set; } = "live";

    public string ActiveTabKey { get; private set; } = "live";

    public IActionResult OnGet()
    {
        if (flood.ForbidIfDenied(HttpContext) is { } denied)
            return denied;

        ActiveTabKey = NormalizeTab(Tab);
        Tab = ActiveTabKey;
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
