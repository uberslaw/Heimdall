namespace Heimdall.Api.Services;

/// <summary>
/// Records a pageview for HTML navigations (Razor pages). Skips agent API, static assets, and health.
/// </summary>
public sealed class SiteUsageAnalyticsMiddleware(RequestDelegate next)
{
    public async Task InvokeAsync(HttpContext context, SiteUsageAnalyticsService usage)
    {
        if (usage.IsEnabled
            && HttpMethods.IsGet(context.Request.Method)
            && SiteUsageAnalyticsService.ShouldTrackPath(context.Request.Path)
            && AcceptsHtml(context.Request)
            && !IsSoftPartialRequest(context.Request))
        {
            // Ensure session cookie can still be written before the response starts.
            usage.EnsureSessionId(context);
            context.Response.OnStarting(() =>
            {
                // PageViewId is set when RecordPageViewAsync runs; expose for the layout script.
                if (context.Items.TryGetValue(SiteUsageAnalyticsService.PageViewItemKey, out var pvid)
                    && pvid is string id
                    && !string.IsNullOrEmpty(id))
                {
                    context.Response.Headers["X-Heimdall-PageViewId"] = id;
                }

                return Task.CompletedTask;
            });

            // Fire-and-forget style within the request: await so SaveChanges uses the request scope.
            await usage.RecordPageViewAsync(context, context.RequestAborted);
        }

        await next(context);
    }

    private static bool AcceptsHtml(HttpRequest request)
    {
        var accept = request.Headers.Accept.ToString();
        if (string.IsNullOrWhiteSpace(accept) || accept.Contains("*/*", StringComparison.Ordinal))
            return true;
        return accept.Contains("text/html", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Ops/Flood soft refresh fetch — not a real navigation.</summary>
    private static bool IsSoftPartialRequest(HttpRequest request)
    {
        if (request.Headers.ContainsKey("X-Ops-Partial")
            || request.Headers.ContainsKey("X-Fleet-Partial")
            || request.Headers.ContainsKey("X-Heimdall-Partial"))
            return true;

        if (request.Query.TryGetValue("partial", out var partial)
            && string.Equals(partial.ToString(), "1", StringComparison.OrdinalIgnoreCase))
            return true;

        return false;
    }
}
