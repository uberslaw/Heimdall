using Heimdall.Api.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace Heimdall.Api.Pages;

/// <summary>
/// Flood hub tab: TUFLOW run-behaviour analytics from TuflowBehaviourRuns
/// (DetectedStart/End, username, machine). Read-only — does not touch Live Active stamps.
/// </summary>
public class TuflowBehaviourModel(TuflowBehaviourService behaviour, FloodAccessGuard flood) : PageModel
{
    public static readonly int[] DayChoices = [7, 14, 30, 90, 180];

    [BindProperty(SupportsGet = true)]
    public int Days { get; set; } = 30;

    public TuflowBehaviourService.TuflowBehaviourAnalytics Analytics { get; private set; } =
        new(30, DateTimeOffset.UtcNow.AddDays(-30), 0, 0, 0, null, null, null, [], [], []);

    public async Task<IActionResult> OnGetAsync(CancellationToken ct)
    {
        if (flood.ForbidIfDenied(HttpContext) is { } denied)
            return denied;

        if (!OpsPartial.IsPartial(Request))
            return OpsPartial.RedirectToFloodTab(Request, "behaviour");

        Days = DayChoices.Contains(Days) ? Days : 30;
        Analytics = await behaviour.GetBehaviourAnalyticsAsync(Days, ct);
        return Page();
    }

    public static string FormatDuration(TimeSpan? t) =>
        t is not { } span
            ? "—"
            : span.TotalHours >= 1
                ? $"{(int)span.TotalHours}h {span.Minutes:D2}m"
                : $"{(int)span.TotalMinutes}m {span.Seconds:D2}s";

    public static string FormatPeakHour(int? hour) =>
        hour is int h ? $"{h:00}:00" : "—";

    public static string FormatHourBar(int count, int max) =>
        max <= 0 || count <= 0
            ? "0%"
            : $"{Math.Clamp(100.0 * count / max, 0, 100).ToString("0.##", System.Globalization.CultureInfo.InvariantCulture)}%";
}
