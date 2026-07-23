using Heimdall.Api.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace Heimdall.Api.Pages;

public class SocratizeModel(SocratizeQueryService socratize) : PageModel
{
    [BindProperty(SupportsGet = true)]
    public string? Host { get; set; }

    /// <summary>Quick range in days; default 30. Use 0 with From/To for custom.</summary>
    [BindProperty(SupportsGet = true)]
    public int Days { get; set; } = 30;

    [BindProperty(SupportsGet = true)]
    public string? From { get; set; }

    [BindProperty(SupportsGet = true)]
    public string? To { get; set; }

    public IReadOnlyList<string> Hostnames { get; private set; } = [];
    public SocratizeBrief? Brief { get; private set; }
    public bool HostNotFound { get; private set; }
    public DateTimeOffset FromUtc { get; private set; }
    public DateTimeOffset ToUtc { get; private set; }
    public string PeriodLabel { get; private set; } = "Last 30 days";

    public async Task OnGetAsync(CancellationToken ct)
    {
        Hostnames = await socratize.ListHostnamesAsync(ct);
        (FromUtc, ToUtc, PeriodLabel) = ResolvePeriod(Days, From, To);

        if (string.IsNullOrWhiteSpace(Host))
            return;

        Brief = await socratize.BuildBriefAsync(Host.Trim(), FromUtc, ToUtc, ct);
        HostNotFound = Brief is null;
    }

    private static (DateTimeOffset From, DateTimeOffset To, string Label) ResolvePeriod(
        int days, string? from, string? to)
    {
        var now = DateTimeOffset.UtcNow;
        if (days == 0 && DateTime.TryParse(from, out var f) && DateTime.TryParse(to, out var t))
        {
            var fromUtc = new DateTimeOffset(DateTime.SpecifyKind(f.Date, DateTimeKind.Utc));
            var toUtc = new DateTimeOffset(DateTime.SpecifyKind(t.Date.AddDays(1).AddTicks(-1), DateTimeKind.Utc));
            if (toUtc < fromUtc)
                (fromUtc, toUtc) = (toUtc, fromUtc);
            return (fromUtc, toUtc, $"{fromUtc:yyyy-MM-dd} → {toUtc:yyyy-MM-dd} UTC");
        }

        var d = days is 7 or 30 or 90 ? days : 30;
        var label = d switch
        {
            7 => "Last 7 days",
            90 => "Last 90 days",
            _ => "Last 30 days"
        };
        return (now.AddDays(-d), now, label);
    }
}
