using System.Text;
using System.Text.RegularExpressions;
using Heimdall.Api.Services;
using Heimdall.Shared.Contracts;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace Heimdall.Api.Pages;

public class RemoteMachinesModel(RemoteMachineService remote) : PageModel
{
    private static readonly Regex SafeRdpTarget = new(
        @"^(?:[A-Za-z0-9](?:[A-Za-z0-9\-]{0,61}[A-Za-z0-9])?(?:\.[A-Za-z0-9](?:[A-Za-z0-9\-]{0,61}[A-Za-z0-9])?)*|\d{1,3}(?:\.\d{1,3}){3})$",
        RegexOptions.CultureInvariant | RegexOptions.Compiled);

    public IReadOnlyList<RemoteMachineService.RemoteMachineRow> Rows { get; private set; } = [];
    public int OnlineCount { get; private set; }
    public int OfflineCount { get; private set; }
    public int RdpAcceptingCount { get; private set; }
    public bool HasActiveRestartOperations { get; private set; }

    public async Task OnGetAsync(CancellationToken ct)
    {
        Rows = await remote.ListAsync(ct);
        OnlineCount = Rows.Count(r => r.IsOnline);
        OfflineCount = Rows.Count - OnlineCount;
        RdpAcceptingCount = Rows.Count(r => r.RdpResponding == true);
        HasActiveRestartOperations = Rows.Any(r =>
            r.RestartProgress is not null
            && RemoteMachineService.IsActiveRestartPhase(r.RestartProgress.Phase));
    }

    /// <summary>
    /// Serves a tiny .rdp file for the row hostname so Windows opens mstsc
    /// (browsers cannot invoke <c>mstsc /v:</c> directly).
    /// </summary>
    public IActionResult OnGetConnectRdp(string hostname)
    {
        if (string.IsNullOrWhiteSpace(hostname))
            return BadRequest();

        var host = hostname.Trim();
        if (host.Length > 253 || !SafeRdpTarget.IsMatch(host))
            return BadRequest();

        // Minimal RDP file — equivalent to: mstsc /v:HOSTNAME
        var content =
            "full address:s:" + host + "\r\n" +
            "prompt for credentials:i:1\r\n" +
            "authentication level:i:2\r\n";

        var fileName = SanitizeRdpFileName(host) + ".rdp";
        var bytes = Encoding.ASCII.GetBytes(content);
        return File(bytes, "application/x-rdp", fileName);
    }

    public async Task<IActionResult> OnPostPingAsync(string hostname, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(hostname))
            return RedirectToPage();

        var result = await remote.PingAsync(hostname.Trim(), ct);
        TempData["Message"] = result.Reachable
            ? $"Ping {result.Target}: reachable ({result.Detail})"
            : $"Ping {result.Target ?? hostname}: unreachable ({result.Detail})";
        return RedirectToPage();
    }

    public async Task<IActionResult> OnPostProbeRdpAsync(string hostname, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(hostname))
            return RedirectToPage();

        var result = await remote.ProbeRdpAsync(hostname.Trim(), ct);
        if (result is null)
        {
            TempData["Error"] = $"Machine '{hostname}' not found.";
            return RedirectToPage();
        }

        TempData["Message"] = result.RdpResponding
            ? $"RDP probe {result.ComputerName}: accepting connections ({result.ElapsedMs} ms)"
            : $"RDP probe {result.ComputerName}: not responding — {result.Error ?? "unknown"}";
        return RedirectToPage();
    }

    public async Task<IActionResult> OnPostRestartRdsAsync(string hostname, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(hostname))
            return RedirectToPage();

        var ok = await remote.QueueRestartTermServiceAsync(hostname.Trim(), ct);
        if (!ok)
        {
            TempData["Error"] = $"Machine '{hostname}' not found.";
            return RedirectToPage();
        }

        TempData["Message"] =
            $"Restart TermService queued for {hostname}. A countdown shows expected agent pickup; RDP will be tested automatically when the agent acknowledges.";
        return RedirectToPage();
    }

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

    private static string SanitizeRdpFileName(string host)
    {
        var sb = new StringBuilder(host.Length);
        foreach (var c in host)
        {
            if (char.IsAsciiLetterOrDigit(c) || c is '.' or '-')
                sb.Append(c);
            else
                sb.Append('_');
        }

        return sb.Length == 0 ? "remote" : sb.ToString();
    }
}
