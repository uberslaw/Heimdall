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
// TuflowRunService.ResolveRunNameAsync falls back to the .tcf / .cmd filename, then "Sim {N}" — see that method.

using Heimdall.Api.Services;
using Heimdall.Shared.Contracts;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace Heimdall.Api.Pages;

public class TuflowRunsModel(TuflowRunService runs, TuflowBehaviourService behaviour, FloodAccessGuard flood) : PageModel
{
    public IReadOnlyList<TuflowRunRow> Rows { get; private set; } = [];
    public IReadOnlyList<TuflowBehaviourService.TuflowBehaviourListRow> RecentDetectedRuns { get; private set; } = [];
    public int EnrolledCount { get; private set; }
    public int RunningCount { get; private set; }

    public async Task<IActionResult> OnGetAsync(CancellationToken ct)
    {
        if (await flood.ForbidIfDeniedAsync(HttpContext) is { } denied)
            return denied;

        Rows = await runs.ListAsync(ct);
        EnrolledCount = Rows.Count;
        RunningCount = Rows.Count(r => r.TuflowRunningNow);
        RecentDetectedRuns = await behaviour.ListRecentAsync(take: 25, ct);
        return Page();
    }

    /// <summary>Prefills the "Your name" field on GET — best-effort, see class remarks.</summary>
    public string? SuggestedRequestedBy => User?.Identity?.Name;

    public async Task<IActionResult> OnPostStartAsync(
        string hostname,
        string? runName,
        string? launchMode,
        string? exePath,
        string? tcfPath,
        string? cmdPath,
        string? workingDirectory,
        string? scenarios,
        string? events,
        string? resultsFolder,
        string? requestedBy,
        CancellationToken ct)
    {
        if (await flood.ForbidIfDeniedAsync(HttpContext) is { } denied)
            return denied;

        if (string.IsNullOrWhiteSpace(hostname))
        {
            TempData["Error"] = "Machine is required.";
            return RedirectToPage();
        }

        var mode = string.Equals(launchMode, TuflowLaunchModes.Cmd, StringComparison.OrdinalIgnoreCase)
            ? TuflowLaunchModes.Cmd
            : TuflowLaunchModes.ExeTcf;

        if (mode == TuflowLaunchModes.ExeTcf
            && (string.IsNullOrWhiteSpace(exePath) || string.IsNullOrWhiteSpace(tcfPath)))
        {
            TempData["Error"] = "Machine, TUFLOW .exe path and .tcf path are all required for exe+.tcf launches.";
            return RedirectToPage();
        }

        if (mode == TuflowLaunchModes.Cmd && string.IsNullOrWhiteSpace(cmdPath))
        {
            TempData["Error"] = "Machine and CMD/BAT path are required for \"Use existing CMD\" launches.";
            return RedirectToPage();
        }

        // Typed "Your name" wins if the person filled it in; otherwise fall back to whatever the signed-in
        // Windows identity resolves to (may be null — see class remarks on RequestedBy).
        var effectiveRequestedBy = string.IsNullOrWhiteSpace(requestedBy) ? User?.Identity?.Name : requestedBy.Trim();

        var (ok, error, runId) = await runs.QueueStartAsync(
            hostname.Trim(),
            string.IsNullOrWhiteSpace(runName) ? null : runName.Trim(),
            string.IsNullOrWhiteSpace(exePath) ? null : exePath.Trim(),
            string.IsNullOrWhiteSpace(tcfPath) ? null : tcfPath.Trim(),
            workingDirectory,
            SplitTokens(scenarios),
            SplitTokens(events),
            resultsFolder,
            requestedBy: effectiveRequestedBy,
            launchMode: mode,
            cmdPath: string.IsNullOrWhiteSpace(cmdPath) ? null : cmdPath.Trim(),
            ct);

        TempData[ok ? "Message" : "Error"] = !ok
            ? error
            : string.Equals(error, "queued-wait", StringComparison.Ordinal)
                ? $"Host is busy — added to {hostname}'s wait queue. It starts when this machine is free (or use Flood → Run Queue to spread across the fleet)."
                : $"TUFLOW start queued for {hostname} (run {runId}). The agent picks this up within about 15-30s.";
        return RedirectToPage();
    }

    public async Task<IActionResult> OnPostStopGracefulAsync(string hostname, CancellationToken ct)
    {
        if (await flood.ForbidIfDeniedAsync(HttpContext) is { } denied)
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

    /// <summary>Prefer CMD/BAT filename when present; otherwise the .tcf path.</summary>
    public static string? StatusPrimaryPath(TuflowRunStatusDto? status)
    {
        if (status is null) return null;
        return !string.IsNullOrWhiteSpace(status.CmdPath) ? status.CmdPath : status.TcfPath;
    }

    public static string? StatusPrimaryFileName(TuflowRunStatusDto? status)
    {
        var path = StatusPrimaryPath(status);
        if (string.IsNullOrWhiteSpace(path)) return null;
        try { return Path.GetFileName(path); }
        catch { return path; }
    }

    private static List<string> SplitTokens(string? raw) =>
        string.IsNullOrWhiteSpace(raw)
            ? []
            : raw.Split([' ', ','], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();

    public static string FormatDuration(TimeSpan t) =>
        t.TotalHours >= 1 ? $"{(int)t.TotalHours}h {t.Minutes:D2}m" : $"{(int)t.TotalMinutes}m {t.Seconds:D2}s";

    public static string FormatSeconds(double sec) =>
        sec >= 3600 ? $"{sec / 3600.0:0.0}h" : sec >= 60 ? $"{sec / 60.0:0.0}m" : $"{sec:0}s";
}
