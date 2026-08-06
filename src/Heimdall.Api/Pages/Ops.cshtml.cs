using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace Heimdall.Api.Pages;

/// <summary>
/// Digital Technology ops console — one shell with lazy-loaded tabs.
/// Only the active tab's data is loaded on first paint; other tabs fetch via AJAX partial.
/// </summary>
public class OpsModel : PageModel
{
    public static readonly (string Key, string Label, string PartialPath)[] Tabs =
    [
        ("machines", "All machines", "/Index"),
        ("sessions", "Sessions", "/Sessions"),
        ("online", "Online status", "/RemoteMachines"),
        ("clients", "Client version", "/ClientVersion"),
        ("cost", "Cost", "/Cost"),
        ("stats", "Stats", "/Stats"),
    ];

    [BindProperty(SupportsGet = true)]
    public string Tab { get; set; } = "machines";

    /// <summary>HTML fragment for the active tab (SSR first paint only).</summary>
    public string? ActiveTabHtml { get; private set; }

    public string ActiveTabKey { get; private set; } = "machines";

    public async Task OnGetAsync(CancellationToken ct)
    {
        ActiveTabKey = NormalizeTab(Tab);
        Tab = ActiveTabKey;

        // SSR first paint: fetch the same partial endpoint the client uses for other tabs.
        // Uses an internal request path via IHttpClientFactory would be heavy; instead we
        // return a marker and let the page include via iframe-free server-side render of a ViewComponent.
        // Concrete: Ops view calls Html.RenderPartial via child action simulation — we set
        // ActiveTabPath for the view to know which page to embed via fetch on load if empty.
        // For true SSR without duplicating loaders, we issue a same-process render by
        // marking that the view should use <partial> from a dedicated loader.
        await Task.CompletedTask;
    }

    public static string NormalizeTab(string? tab)
    {
        var key = (tab ?? "machines").Trim().ToLowerInvariant();
        return Tabs.Any(t => t.Key == key) ? key : "machines";
    }

    public static string PartialUrl(string tabKey, HttpRequest request)
    {
        var tab = Tabs.FirstOrDefault(t => t.Key == tabKey);
        var path = tab.PartialPath ?? "/Index";
        var qs = new List<string> { "partial=1" };
        // Forward filter query params that belong to the underlying page (except tab).
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
