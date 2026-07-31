using Heimdall.Api.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace Heimdall.Api.Pages;

public class MachineModel(StatsQueryService stats) : PageModel
{
    [BindProperty(SupportsGet = true)]
    public string? Hostname { get; set; }

    [BindProperty(SupportsGet = true)]
    public string Range { get; set; } = "7d";

    [BindProperty(SupportsGet = true)]
    public List<string> Apps { get; set; } = [];

    public MachineDetailSnapshot? Detail { get; private set; }
    public bool HostNotFound { get; private set; }
    public string RangeLabel { get; private set; } = "7 day";
    public int RangeDays { get; private set; } = 7;

    public async Task OnGetAsync(CancellationToken ct)
    {
        var (key, label, days) = IndexModel.ResolveRange(Range);
        Range = key;
        RangeLabel = label;
        RangeDays = days;

        if (string.IsNullOrWhiteSpace(Hostname))
            return;

        var fromUtc = DateTimeOffset.UtcNow.AddDays(-days);
        var toUtc = DateTimeOffset.UtcNow;
        var selectedApps = Apps.Count > 0 ? Apps : null;

        Detail = await stats.QueryMachineDetailAsync(Hostname.Trim(), fromUtc, toUtc, selectedApps, ct);
        HostNotFound = Detail is null;
    }

    public static string FormatDuration(double seconds) => StatsModel.FormatDuration(seconds);
}
