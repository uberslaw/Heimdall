using Microsoft.AspNetCore.Mvc;

namespace Heimdall.Api.Pages;

/// <summary>Shared helpers for Ops lazy-loaded tab partials.</summary>
public static class OpsPartial
{
    public const string QueryKey = "partial";

    public static bool IsPartial(HttpRequest request) =>
        string.Equals(request.Query[QueryKey], "1", StringComparison.OrdinalIgnoreCase)
        || string.Equals(request.Headers["X-Ops-Partial"], "1", StringComparison.OrdinalIgnoreCase);

    /// <summary>302 to /Ops?tab=… preserving other query params (except partial).</summary>
    public static IActionResult RedirectToOpsTab(HttpRequest request, string tab)
    {
        var parts = new List<string> { "tab=" + Uri.EscapeDataString(tab) };
        foreach (var kv in request.Query)
        {
            if (string.Equals(kv.Key, "tab", StringComparison.OrdinalIgnoreCase))
                continue;
            if (string.Equals(kv.Key, QueryKey, StringComparison.OrdinalIgnoreCase))
                continue;
            foreach (var v in kv.Value)
            {
                if (v is null) continue;
                parts.Add($"{Uri.EscapeDataString(kv.Key)}={Uri.EscapeDataString(v)}");
            }
        }

        return new RedirectResult("/Ops?" + string.Join("&", parts));
    }
}
