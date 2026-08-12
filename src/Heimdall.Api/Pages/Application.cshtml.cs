using Heimdall.Api.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace Heimdall.Api.Pages;

public class ApplicationModel(StatsQueryService stats) : PageModel
{
    [BindProperty(SupportsGet = true)]
    public string? Process { get; set; }

    [BindProperty(SupportsGet = true)]
    public string Range { get; set; } = "7d";

    [BindProperty(SupportsGet = true)]
    public string? Hostname { get; set; }

    public ApplicationDetailSnapshot? Detail { get; private set; }
    public bool MissingProcess { get; private set; }
    public string RangeLabel { get; private set; } = "7 day";
    public int RangeDays { get; private set; } = 7;

    public async Task OnGetAsync(CancellationToken ct)
    {
        var (key, label, days) = IndexModel.ResolveRange(Range);
        Range = key;
        RangeLabel = label;
        RangeDays = days;

        if (string.IsNullOrWhiteSpace(Process))
        {
            MissingProcess = true;
            return;
        }

        var (fromUtc, toUtc) = IndexModel.ResolveRangeWindow(Range);

        Detail = await stats.QueryApplicationDetailAsync(
            Process.Trim(),
            fromUtc,
            toUtc,
            string.IsNullOrWhiteSpace(Hostname) ? null : Hostname.Trim(),
            ct);
    }

}
