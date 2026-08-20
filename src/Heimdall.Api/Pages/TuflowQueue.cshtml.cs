using Heimdall.Api.Services;
using Heimdall.Shared.Contracts;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace Heimdall.Api.Pages;

public class TuflowQueueModel(TuflowQueueService queues, TuflowRunService runs, FloodAccessGuard flood) : PageModel
{
    public TuflowQueuePage Data { get; private set; } = new(
        [], null, null, null, 1, null, null, null, "Queue", [], [], [], [],
        TuflowScratchSettingsService.DefaultArchiveShareTemplate, true);

    public async Task<IActionResult> OnGetAsync(int? machineId, CancellationToken ct)
    {
        if (await flood.ForbidIfDeniedAsync(HttpContext) is { } denied)
            return denied;

        if (!OpsPartial.IsPartial(Request))
            return OpsPartial.RedirectToFloodTab(Request, "queue");

        Data = await queues.GetPageAsync(machineId, ct);
        return Page();
    }

    public string? SuggestedRequestedBy => User?.Identity?.Name;

    IActionResult Back(int? machineId) =>
        Redirect($"/Flood?tab=queue{(machineId is int id ? "&machineId=" + id : "")}");

    public async Task<IActionResult> OnPostAddMatrixAsync(
        int? machineId,
        string? target,
        string? runName,
        string? requestedBy,
        string? exePath,
        string? tcfPath,
        string? workingDirectory,
        string? resultsFolder,
        string? s1,
        string? s2,
        string? s3,
        string? s4,
        string? e1,
        string? e2,
        string? e3,
        string? e4,
        string? useLocalScratch,
        string? archiveShare,
        string? autoCleanAfterVerify,
        CancellationToken ct)
    {
        if (await flood.ForbidIfDeniedAsync(HttpContext) is { } denied)
            return denied;

        var fleet = !string.Equals(target, "machine", StringComparison.OrdinalIgnoreCase);
        var scenarioGroups = new[] { s1, s2, s3, s4 }
            .Select(TuflowQueueService.ParseTokenList)
            .Cast<IReadOnlyList<string>>()
            .ToList();
        var eventGroups = new[] { e1, e2, e3, e4 }
            .Select(TuflowQueueService.ParseTokenList)
            .Cast<IReadOnlyList<string>>()
            .ToList();

        var scratch = IsTruthy(useLocalScratch);
        var autoClean = IsTruthy(autoCleanAfterVerify);

        var (ok, error, added) = await queues.AddMatrixAsync(
            machineId,
            fleet,
            runName,
            string.IsNullOrWhiteSpace(requestedBy) ? User?.Identity?.Name : requestedBy.Trim(),
            TuflowLaunchModes.ExeTcf,
            exePath,
            tcfPath,
            cmdPath: null,
            workingDirectory,
            resultsFolder,
            scenarioGroups,
            eventGroups,
            scratch,
            archiveShare,
            autoClean,
            ct);

        TempData[ok ? "Message" : "Error"] = ok
            ? $"Added {added} simulation{(added == 1 ? "" : "s")} to the {(fleet ? "fleet" : "machine")} queue. Idle Flood hosts pick up the next item within ~20s."
            : error;
        return Back(fleet ? null : machineId);
    }

    static bool IsTruthy(string? value) =>
        !string.IsNullOrWhiteSpace(value)
        && (string.Equals(value, "on", StringComparison.OrdinalIgnoreCase)
            || string.Equals(value, "true", StringComparison.OrdinalIgnoreCase)
            || value == "1");

    public async Task<IActionResult> OnPostCancelAsync(int itemId, int? machineId, CancellationToken ct)
    {
        if (await flood.ForbidIfDeniedAsync(HttpContext) is { } denied)
            return denied;
        var (ok, error) = await queues.CancelItemAsync(itemId, ct);
        TempData[ok ? "Message" : "Error"] = ok ? "Cancelled." : error;
        return Back(machineId);
    }

    public async Task<IActionResult> OnPostMoveAsync(int itemId, int delta, int? machineId, CancellationToken ct)
    {
        if (await flood.ForbidIfDeniedAsync(HttpContext) is { } denied)
            return denied;
        var (ok, error) = await queues.MoveAsync(itemId, delta, ct);
        if (!ok)
            TempData["Error"] = error;
        return Back(machineId);
    }

    public async Task<IActionResult> OnPostAssignAsync(int itemId, int? assignMachineId, int? machineId, CancellationToken ct)
    {
        if (await flood.ForbidIfDeniedAsync(HttpContext) is { } denied)
            return denied;
        var (ok, error) = await queues.AssignItemAsync(itemId, assignMachineId, ct);
        TempData[ok ? "Message" : "Error"] = ok
            ? (assignMachineId is null ? "Item returned to the fleet queue." : "Item pinned to that host.")
            : error;
        return Back(machineId);
    }

    public async Task<IActionResult> OnPostRerunAsync(int itemId, int? machineId, CancellationToken ct)
    {
        if (await flood.ForbidIfDeniedAsync(HttpContext) is { } denied)
            return denied;
        var (ok, error) = await queues.RerunItemAsync(itemId, ct);
        TempData[ok ? "Message" : "Error"] = ok ? "Re-queued a copy at the end of the queue." : error;
        return Back(machineId);
    }

    public async Task<IActionResult> OnPostStopGracefulAsync(string hostname, int? machineId, CancellationToken ct)
    {
        if (await flood.ForbidIfDeniedAsync(HttpContext) is { } denied)
            return denied;
        if (string.IsNullOrWhiteSpace(hostname))
            return Back(machineId);
        var (ok, error) = await runs.QueueStopGracefulAsync(hostname.Trim(), ct);
        TempData[ok ? "Message" : "Error"] = ok
            ? $"Graceful stop queued for {hostname}."
            : error;
        return Back(machineId);
    }

    public async Task<IActionResult> OnPostAbandonAsync(string hostname, int? machineId, CancellationToken ct)
    {
        if (await flood.ForbidIfDeniedAsync(HttpContext) is { } denied)
            return denied;
        if (string.IsNullOrWhiteSpace(hostname))
            return Back(machineId);
        var (ok, error) = await runs.AbandonActiveRunAsync(hostname.Trim(), ct);
        TempData[ok ? "Message" : "Error"] = ok
            ? $"Cleared stuck run on {hostname}. Host can take new queue work."
            : error;
        return Back(machineId);
    }
}
