// NEW FILE — drop in as-is at:
//   Heimdall.Api/Pages/FleetSimProgress.cshtml.cs
//
// Backs the Fleet Sim Progress page — Chris's request: "who kicked it off ... is running which
// simulation ... on which machine, and stats on how long it has been going for (and how long left if
// we can figure it out) and avg GPU/Disk/Network usage ... over the [polling] lifetime". All the actual
// data assembly lives in TuflowRunService.GetFleetProgressAsync — this page model just calls it and
// formats a couple of display fields, mirroring TuflowRunsModel's thin-model style.
//
// No machine picker, no filters beyond what's already implicit: GetFleetProgressAsync only ever returns
// active runs on Flood-enrolled machines (it queries TuflowRunRecords, which are only ever created by
// TuflowRunService.QueueStartAsync, which itself already checks IsFloodEnrolledAsync before creating one).

using Heimdall.Api.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace Heimdall.Api.Pages;

public class FleetSimProgressModel(TuflowRunService runs, TuflowQueueService queues, FloodAccessGuard flood) : PageModel
{
    public IReadOnlyList<FleetSimProgressRow> Rows { get; private set; } = [];
    public int QueueWaiting { get; private set; }
    public int QueueActive { get; private set; }

    public async Task<IActionResult> OnGetAsync(CancellationToken ct)
    {
        if (flood.ForbidIfDenied(HttpContext) is { } denied)
            return denied;

        if (!OpsPartial.IsPartial(Request))
            return OpsPartial.RedirectToFloodTab(Request, "sims");

        Rows = await runs.GetFleetProgressAsync(ct);
        (QueueWaiting, QueueActive) = await queues.CountWorkAsync(ct);
        return Page();
    }

    public static string FormatSpan(TimeSpan span) => span.TotalHours >= 1
        ? $"{(int)span.TotalHours}h {span.Minutes}m"
        : $"{(int)span.TotalMinutes}m {span.Seconds}s";

    public static string FormatHours(double? hours) => hours is double h
        ? h >= 1 ? $"{h:0.#}h" : $"{(int)(h * 60)}m"
        : "—";

    public static string FormatRate(double? mbps) => mbps is double v ? $"{v:0.#} MB/s" : "—";

    public static string FormatPercent(double? pct) => pct is double v ? $"{v:0.#}%" : "—";

    public static string RunStateBadgeClass(string state) => state switch
    {
        Heimdall.Shared.Contracts.TuflowRunStates.Running => "badge-active",
        _ => "badge-ended" // Starting / StopRequested — only active states ever reach this page, see class remarks
    };
}
