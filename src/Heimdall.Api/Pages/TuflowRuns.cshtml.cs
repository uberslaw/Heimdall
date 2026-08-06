// NEW FILE — drop in as-is at:
//   Heimdall.Api/Pages/TuflowRuns.cshtml.cs
//
// Mirrors RemoteMachinesModel's shape (OnGetAsync lists rows, OnPost* handlers queue an action and
// redirect with TempData Message/Error) — see Heimdall.Api/Pages/RemoteMachines.cshtml.cs for the
// pattern this follows.
//
// Rows are already scoped to Flood-enrolled machines by TuflowRunService.ListAsync — there is no
// separate machine picker on this page by design; each row's Start/Stop buttons act on that row's own
// machine only, so it's impossible to target a non-Flood machine from this UI. Enroll/unenroll Flood
// machines from wherever the existing Historical Dashboard enrollment page lives.
//
// NOTE on RequestedBy ("who kicked it off, from the website"): the start form has a "Your name" text
// field, prefilled from User?.Identity?.Name where available. I have not seen how Heimdall's staff pages
// resolve the signed-in person's identity end to end (RemoteMachines.cshtml.cs itself doesn't attribute
// actions to a user, and Negotiate is wired globally in Program.cs but whether it actually populates
// HttpContext.User on this specific page in your deployment isn't verified — see README). Keeping the
// field editable means RequestedBy is never silently blank even if the prefill doesn't work; if you later
// confirm Windows auth reliably populates User.Identity.Name here, you can make the field readonly.
//
// NOTE on RunName ("which simulation"): the start form has an optional "Run name" text field. Left blank,
// TuflowRunService.ResolveRunNameAsync falls back to the .tcf filename, then "Sim {N}" — see that method.

using Heimdall.Api.Services;
using Heimdall.Shared.Contracts;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace Heimdall.Api.Pages;

public class TuflowRunsModel(TuflowRunService runs, FloodAccessGuard flood) : PageModel
{
    public IReadOnlyList<TuflowRunRow> Rows { get; private set; } = [];
    public int EnrolledCount { get; private set; }
    public int RunningCount { get; private set; }

    public async Task<IActionResult> OnGetAsync(CancellationToken ct)
    {
        if (flood.ForbidIfDenied(HttpContext) is { } denied)
            return denied;

        Rows = await runs.ListAsync(ct);
        EnrolledCount = Rows.Count;
        RunningCount = Rows.Count(r => r.TuflowRunningNow);
        return Page();
    }

    /// <summary>Prefills the "Your name" field on GET — best-effort, see class remarks.</summary>
    public string? SuggestedRequestedBy => User?.Identity?.Name;

    public async Task<IActionResult> OnPostStartAsync(
        string hostname,
        string? runName,
        string exePath,
        string tcfPath,
        string? workingDirectory,
        string? scenarios,
        string? events,
        string? resultsFolder,
        string? requestedBy,
        CancellationToken ct)
    {
        if (flood.ForbidIfDenied(HttpContext) is { } denied)
            return denied;

        if (string.IsNullOrWhiteSpace(hostname) || string.IsNullOrWhiteSpace(exePath) || string.IsNullOrWhiteSpace(tcfPath))
        {
            TempData["Error"] = "Machine, TUFLOW .exe path and .tcf path are all required.";
            return RedirectToPage();
        }

        // Typed "Your name" wins if the person filled it in; otherwise fall back to whatever the signed-in
        // Windows identity resolves to (may be null — see class remarks on RequestedBy).
        var effectiveRequestedBy = string.IsNullOrWhiteSpace(requestedBy) ? User?.Identity?.Name : requestedBy.Trim();

        var (ok, error, runId) = await runs.QueueStartAsync(
            hostname.Trim(),
            string.IsNullOrWhiteSpace(runName) ? null : runName.Trim(),
            exePath.Trim(),
            tcfPath.Trim(),
            workingDirectory,
            SplitTokens(scenarios),
            SplitTokens(events),
            resultsFolder,
            requestedBy: effectiveRequestedBy,
            ct);

        TempData[ok ? "Message" : "Error"] = ok
            ? $"TUFLOW start queued for {hostname} (run {runId}). The agent picks this up within about 15-30s via the fast TUFLOW poll (see RunTuflowPollTickAsync), not the slower ConfigRefreshSeconds cycle."
            : error;
        return RedirectToPage();
    }

    public async Task<IActionResult> OnPostStopGracefulAsync(string hostname, CancellationToken ct)
    {
        if (flood.ForbidIfDenied(HttpContext) is { } denied)
            return denied;

        if (string.IsNullOrWhiteSpace(hostname))
            return RedirectToPage();

        var (ok, error) = await runs.QueueStopGracefulAsync(hostname.Trim(), ct);
        TempData[ok ? "Message" : "Error"] = ok
            ? $"Graceful stop queued for {hostname}. TUFLOW will finish writing its current output/checkpoint, release the licence, and log INTERRUPTED — resume later from the last .trf/.erf it wrote."
            : error;
        return RedirectToPage();
    }

    public static string RunStateBadgeClass(string? state) => state switch
    {
        TuflowRunStates.Running => "badge-active",
        TuflowRunStates.Starting or TuflowRunStates.StopRequested => "badge-ended",
        TuflowRunStates.Stopped or TuflowRunStates.Completed => "badge-expired",
        TuflowRunStates.Failed => "badge-expired",
        _ => "badge-ended"
    };

    public static bool CanStart(TuflowRunRow row) =>
        row.PendingStart is null
        && !TuflowRunService.IsActiveRunState(row.Status?.State)
        && !row.TuflowRunningNow; // don't offer Start over a TUFLOW instance running outside Heimdall's tracking

    public static bool CanStop(TuflowRunRow row) =>
        TuflowRunService.IsActiveRunState(row.Status?.State) && row.Status?.State != TuflowRunStates.StopRequested;

    /// <summary>Thin wrapper so the .cshtml doesn't need its own `@@using Heimdall.Api.Services`.</summary>
    public static bool IsHeimdallTrackedRunActive(TuflowRunRow row) => TuflowRunService.IsActiveRunState(row.Status?.State);

    public static string FleetStatusLabel(FleetDashboardService.FleetStatus status) => status switch
    {
        FleetDashboardService.FleetStatus.Active => "Running (active)",
        FleetDashboardService.FleetStatus.Idle => "Running (idle)",
        FleetDashboardService.FleetStatus.NotRunning => "Not running",
        _ => "Unknown"
    };

    public static string FleetStatusBadgeClass(FleetDashboardService.FleetStatus status) => status switch
    {
        FleetDashboardService.FleetStatus.Active => "badge-active",
        FleetDashboardService.FleetStatus.Idle => "badge-active",
        FleetDashboardService.FleetStatus.NotRunning => "badge-expired",
        _ => "badge-ended"
    };

    private static List<string> SplitTokens(string? raw) =>
        string.IsNullOrWhiteSpace(raw)
            ? []
            : raw.Split([' ', ','], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();
}
