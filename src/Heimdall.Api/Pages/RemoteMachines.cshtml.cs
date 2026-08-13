using Heimdall.Api.Services;
using Heimdall.Shared.Contracts;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace Heimdall.Api.Pages;

public class RemoteMachinesModel(RemoteMachineService remote) : PageModel
{
    public IReadOnlyList<RemoteMachineService.RemoteMachineRow> Rows { get; private set; } = [];
    public int OnlineCount { get; private set; }
    public int OfflineCount { get; private set; }
    public int RdpAcceptingCount { get; private set; }
    public bool HasActiveRestartOperations { get; private set; }

    public async Task<IActionResult> OnGetAsync(CancellationToken ct)
    {
        if (!OpsPartial.IsPartial(Request))
            return OpsPartial.RedirectToOpsTab(Request, "online");

        Rows = await remote.ListAsync(ct);
        OnlineCount = Rows.Count(r => r.IsOnline);
        OfflineCount = Rows.Count - OnlineCount;
        RdpAcceptingCount = Rows.Count(r => r.RdpResponding == true);
        HasActiveRestartOperations = Rows.Any(r =>
            r.RestartProgress is not null
            && RemoteMachineService.IsActiveRestartPhase(r.RestartProgress.Phase));
        return Page();
    }

    /// <summary>
    /// Serves a tiny .rdp file so Windows opens mstsc. Prefers the machine's LastIp (agent-reported)
    /// when set; falls back to hostname. Lookup is always by catalogue hostname.
    /// </summary>
    public async Task<IActionResult> OnGetConnectRdpAsync(string hostname, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(hostname))
            return BadRequest();

        var row = await remote.GetRowAsync(hostname.Trim(), ct);
        var target = row is not null && !string.IsNullOrWhiteSpace(row.LastIp)
            ? row.LastIp!
            : hostname.Trim();

        var file = RdpConnectFile.TryCreate(target);
        return file is null ? BadRequest() : file;
    }

    public async Task<IActionResult> OnPostPingAsync(string hostname, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(hostname))
        {
            if (WantsAjax())
                return new JsonResult(new { ok = false, error = "Missing hostname." }) { StatusCode = 400 };
            return RedirectToOpsOnline();
        }

        var result = await remote.PingAsync(hostname.Trim(), ct);
        var message = result.Reachable
            ? $"Ping {result.Target}: reachable ({result.Detail})"
            : $"Ping {result.Target ?? hostname}: unreachable ({result.Detail})";

        if (WantsAjax())
        {
            return new JsonResult(new
            {
                ok = true,
                hostname = hostname.Trim(),
                action = "ping",
                reachable = result.Reachable,
                detail = result.Detail,
                label = FormatPingStatus(result.Reachable, result.Detail),
                message
            });
        }

        TempData["Message"] = message;
        return RedirectToOpsOnline();
    }

    public async Task<IActionResult> OnPostProbeRdpAsync(string hostname, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(hostname))
        {
            if (WantsAjax())
                return new JsonResult(new { ok = false, error = "Missing hostname." }) { StatusCode = 400 };
            return RedirectToPage();
        }

        var result = await remote.ProbeRdpAsync(hostname.Trim(), ct);
        if (result is null)
        {
            if (WantsAjax())
                return new JsonResult(new { ok = false, error = $"Machine '{hostname}' not found." }) { StatusCode = 404 };
            TempData["Error"] = $"Machine '{hostname}' not found.";
            return RedirectToOpsOnline();
        }

        var message = result.RdpResponding
            ? $"RDP probe {result.ComputerName}: accepting connections ({result.ElapsedMs} ms)"
            : $"RDP probe {result.ComputerName}: not responding — {result.Error ?? "unknown"}";

        if (WantsAjax())
        {
            return new JsonResult(new
            {
                ok = true,
                hostname = hostname.Trim(),
                action = "probeRdp",
                rdpResponding = result.RdpResponding,
                error = result.Error,
                label = RdpStatusLabel(result.RdpResponding),
                badgeClass = RdpBadgeClass(result.RdpResponding),
                message
            });
        }

        TempData["Message"] = message;
        return RedirectToOpsOnline();
    }

    public async Task<IActionResult> OnPostRestartRdsAsync(string hostname, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(hostname))
        {
            if (WantsAjax())
                return new JsonResult(new { ok = false, error = "Missing hostname." }) { StatusCode = 400 };
            return RedirectToPage();
        }

        var ok = await remote.QueueRestartTermServiceAsync(hostname.Trim(), ct);
        if (!ok)
        {
            if (WantsAjax())
                return new JsonResult(new { ok = false, error = $"Machine '{hostname}' not found." }) { StatusCode = 404 };
            TempData["Error"] = $"Machine '{hostname}' not found.";
            return RedirectToOpsOnline();
        }

        var message =
            $"Restart TermService queued for {hostname}. A countdown shows expected agent pickup; RDP will be tested automatically when the agent acknowledges.";

        if (WantsAjax())
        {
            return new JsonResult(new
            {
                ok = true,
                hostname = hostname.Trim(),
                action = "restartRds",
                message,
                reload = true
            });
        }

        TempData["Message"] = message;
        return RedirectToOpsOnline();
    }

    private IActionResult RedirectToOpsOnline() => RedirectToPage("/Fleet", new { tab = "online" });

    private bool WantsAjax() =>
        string.Equals(Request.Headers.Accept.ToString(), "application/json", StringComparison.OrdinalIgnoreCase)
        || string.Equals(Request.Query["ajax"], "1", StringComparison.OrdinalIgnoreCase)
        || Request.Headers.XRequestedWith == "XMLHttpRequest";

    public static string FormatContact(DateTimeOffset utc) =>
        RemoteMachineService.FormatAgentContact(utc);

    public static string FormatPingStatus(bool reachable, string? detail)
    {
        if (!reachable)
            return "Unreachable";

        if (!string.IsNullOrWhiteSpace(detail) && detail.EndsWith(" ms", StringComparison.Ordinal))
            return detail;

        return "Reachable";
    }

    public static string TermServiceBadgeClass(string? status) => status?.Trim() switch
    {
        "Running" => "badge-active",
        "Stopped" => "badge-expired",
        _ => "badge-ended"
    };

    public static string RdpBadgeClass(bool? responding) => responding switch
    {
        true => "badge-rdp",
        false => "badge-expired",
        _ => "badge-ended"
    };

    public static string RdpStatusLabel(bool responding) =>
        responding ? "Accepting" : "Unreachable";

    /// <summary>Show server-stored RDP probe only when probed on the current local calendar day.</summary>
    public static bool ShouldShowRdpStatus(RemoteMachineService.RemoteMachineRow row) =>
        row.RdpResponding is not null
        && row.LastRdpProbeUtc is DateTimeOffset probeUtc
        && probeUtc.ToLocalTime().Date == DateTimeOffset.Now.ToLocalTime().Date;

    public static string RestartProgressLabel(RemoteMachineService.RemoteMachineRow row) =>
        RemoteMachineService.FormatRestartProgressLabel(row);

    public static string RestartProgressBadgeClass(RemoteMachineService.RemoteMachineRow row) =>
        RemoteMachineService.RestartProgressBadgeClass(row);

    public static bool ShowRestartProgress(RemoteMachineService.RemoteMachineRow row) =>
        RemoteMachineService.ShowRestartProgress(row);

    public static bool IsRdpTesting(RemoteMachineService.RemoteMachineRow row) =>
        row.RestartProgress?.Phase is RestartRdsPhases.Verifying or RestartRdsPhases.Acknowledged;
}
