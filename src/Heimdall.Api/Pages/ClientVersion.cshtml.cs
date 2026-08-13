using Heimdall.Api.Data;
using Heimdall.Api.Services;
using Heimdall.Shared.Contracts;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;

namespace Heimdall.Api.Pages;

/// <summary>
/// Fleet view: agent simple version per host vs. published baseline (PublishedVersionService).
/// Online = last seen within 5 minutes; in-use from heartbeat; Active User from open sessions.
/// Pack readiness + silent Deploy Client (agent pull).
/// </summary>
public class ClientVersionModel(
    HeimdallDbContext db,
    PublishedVersionService publishedVersion,
    ClientUpdateService clientUpdates) : PageModel
{
    public string? PublishedVersion { get; private set; }
    public int? PublishedSimpleVersion { get; private set; }
    public bool PublishedIsDefault { get; private set; }
    public DateTimeOffset? PublishedSetUtc { get; private set; }
    public string? PublishedSetBy { get; private set; }

    public List<ClientRow> Rows { get; private set; } = [];
    public int UpToDateCount { get; private set; }
    public int OutOfDateCount { get; private set; }
    public int UnknownCount { get; private set; }

    [BindProperty]
    public string? NewPublishedVersion { get; set; }

    [BindProperty]
    public List<string> SelectedHostnames { get; set; } = [];

    public async Task<IActionResult> OnGetAsync(CancellationToken ct)
    {
        if (!OpsPartial.IsPartial(Request))
            return OpsPartial.RedirectToOpsTab(Request, "clients");

        await LoadAsync(ct);
        return Page();
    }

    public async Task<IActionResult> OnPostSetPublishedVersionAsync(CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(NewPublishedVersion))
        {
            TempData["Error"] = "Enter a version number (e.g. 3) before saving.";
            return RedirectToOpsClients();
        }

        var simple = VersionCompare.TryGetSimpleVersion(NewPublishedVersion);
        if (simple is null)
        {
            TempData["Error"] = "Enter a valid version (integer, or legacy SemVer which maps to 1).";
            return RedirectToOpsClients();
        }

        var setBy = string.IsNullOrWhiteSpace(User.Identity?.Name) ? "Client Version page" : User.Identity!.Name;
        await publishedVersion.SetAsync(simple.Value.ToString(), setBy, ct);
        TempData["Message"] = $"Published client version set to {simple.Value}. Clients now compare against this.";
        return RedirectToOpsClients();
    }

    public async Task<IActionResult> OnPostDeployAsync(CancellationToken ct)
    {
        if (SelectedHostnames.Count == 0)
        {
            TempData["Error"] = "Select at least one machine to deploy.";
            return RedirectToOpsClients();
        }

        var (queued, message) = await clientUpdates.QueueUpdatesAsync(SelectedHostnames, ct);
        if (queued == 0)
            TempData["Error"] = message;
        else
            TempData["Message"] = message;
        return RedirectToOpsClients();
    }

    public async Task<IActionResult> OnPostRestartAgentAsync(CancellationToken ct)
    {
        if (SelectedHostnames.Count == 0)
        {
            TempData["Error"] = "Select at least one machine to restart.";
            return RedirectToOpsClients();
        }

        var (queued, _, message) = await clientUpdates.QueueRestartAgentAsync(SelectedHostnames, ct);
        if (queued == 0)
            TempData["Error"] = message;
        else
            TempData["Message"] = message;
        return RedirectToOpsClients();
    }

    public async Task<IActionResult> OnPostCleanupStagingAsync(CancellationToken ct)
    {
        if (SelectedHostnames.Count == 0)
        {
            TempData["Error"] = "Select at least one machine to clean staging folders.";
            return RedirectToOpsClients();
        }

        var (queued, _, message) = await clientUpdates.QueueCleanupStagingAsync(SelectedHostnames, ct);
        if (queued == 0)
            TempData["Error"] = message;
        else
            TempData["Message"] = message;
        return RedirectToOpsClients();
    }

    public async Task<IActionResult> OnPostDepositClientPackAsync(CancellationToken ct)
    {
        if (SelectedHostnames.Count == 0)
        {
            TempData["Error"] = "Select at least one machine to deposit the client pack.";
            return RedirectToOpsClients();
        }

        var result = await clientUpdates.QueueDepositClientPackAsync(SelectedHostnames, ct);
        if (result.Queued == 0)
            TempData["Error"] = result.Message;
        else
            TempData["Message"] = result.Message;
        return RedirectToOpsClients();
    }

    private static IActionResult RedirectToOpsClients() =>
        new RedirectResult("/Fleet?tab=clients");

    private async Task LoadAsync(CancellationToken ct)
    {
        var info = await publishedVersion.GetAsync(ct);
        PublishedVersion = info.Version;
        PublishedSimpleVersion = VersionCompare.TryGetSimpleVersion(info.Version);
        PublishedIsDefault = info.IsDefault;
        PublishedSetUtc = info.SetUtc;
        PublishedSetBy = info.SetBy;

        var now = DateTimeOffset.UtcNow;
        var onlineCutoff = now.AddMinutes(-5);

        var machines = await db.Machines.AsNoTracking()
            .OrderBy(m => m.Hostname)
            .ToListAsync(ct);

        var machineIds = machines.Select(m => m.Id).ToList();
        var openSessions = machineIds.Count == 0
            ? []
            : await db.Sessions.AsNoTracking()
                .Where(s => machineIds.Contains(s.MachineId) && s.State != SessionState.Ended)
                .ToListAsync(ct);

        var sessionsByMachine = openSessions
            .GroupBy(s => s.MachineId)
            .ToDictionary(g => g.Key, g => g.ToList());

        Rows = machines.Select(m =>
        {
            var simple = VersionCompare.TryGetSimpleVersion(m.AgentVersion);
            var behind = VersionCompare.GetVersionsBehind(PublishedVersion, m.AgentVersion);
            var friendly = string.IsNullOrWhiteSpace(m.FriendlyName) ? null : m.FriendlyName.Trim();

            ClientVersionStatus status;
            if (string.IsNullOrWhiteSpace(PublishedVersion))
                status = ClientVersionStatus.NoBaseline;
            else if (string.IsNullOrWhiteSpace(m.AgentVersion) || simple is null)
                status = ClientVersionStatus.Missing;
            else if (behind is > 0)
                status = ClientVersionStatus.OutOfDate;
            else if (VersionCompare.CoreVersionsMatch(m.AgentVersion, PublishedVersion))
                status = ClientVersionStatus.UpToDate;
            else
                status = ClientVersionStatus.UpToDate;

            sessionsByMachine.TryGetValue(m.Id, out var open);
            open ??= [];
            var activeUserSession = open
                .OrderByDescending(s => s.State == SessionState.Active)
                .ThenByDescending(s => s.LastObservedUtc)
                .ThenByDescending(s => s.ActiveSeconds)
                .FirstOrDefault(s => !string.IsNullOrWhiteSpace(s.Username));

            return new ClientRow(
                m.Id,
                m.Hostname,
                friendly,
                m.LastIp,
                m.MachineGroup,
                m.Region,
                m.Office,
                m.AgentVersion,
                simple,
                behind,
                m.LastSeenUtc,
                m.LastSeenUtc >= onlineCutoff,
                m.IsInUse,
                activeUserSession?.Username,
                activeUserSession?.State,
                m.ClientUpdateProgressJson,
                status);
        })
            .OrderBy(r => r.DisplayName, StringComparer.OrdinalIgnoreCase)
            .ToList();

        UpToDateCount = Rows.Count(r => r.Status == ClientVersionStatus.UpToDate);
        OutOfDateCount = Rows.Count(r => r.Status is ClientVersionStatus.OutOfDate or ClientVersionStatus.Missing);
        UnknownCount = Rows.Count(r => r.Status == ClientVersionStatus.NoBaseline);
    }

    /// <summary>
    /// Out-of-date grading: amber when exactly one simple version behind, red when two or more.
    /// Missing / unknown stays red; current and unparsed-as-behind stay non-alarm.
    /// </summary>
    public static string StatusBadgeClass(ClientVersionStatus status, int? versionsBehind = null) => status switch
    {
        ClientVersionStatus.UpToDate => "badge-active",
        ClientVersionStatus.OutOfDate => versionsBehind is 1 ? "badge-warn" : "badge-expired",
        ClientVersionStatus.Missing => "badge-expired",
        _ => "badge-warn"
    };

    public static string StatusLabel(ClientVersionStatus status) => status switch
    {
        ClientVersionStatus.UpToDate => "Up to date",
        ClientVersionStatus.OutOfDate => "Out of date",
        ClientVersionStatus.Missing => "Missing / unknown",
        _ => "No baseline set"
    };

    public static string? BehindBadgeClass(int? versionsBehind) => versionsBehind switch
    {
        1 => "badge-warn",
        > 1 => "badge-expired",
        _ => null
    };

    public static string? BehindRowClass(int? versionsBehind) => versionsBehind switch
    {
        1 => "hd-row-highlight",
        > 1 => "hd-row-behind-critical",
        _ => null
    };

    public static string? VersionCellClass(int? versionsBehind) => versionsBehind switch
    {
        1 => "hd-client-ver-behind-1",
        > 1 => "hd-client-ver-behind-2",
        _ => null
    };

    public static string FormatContact(DateTimeOffset utc) => RemoteMachineService.FormatAgentContact(utc);

    public static string? FormatUpdateProgress(string? json)
    {
        var p = ClientUpdateService.DeserializeProgress(json);
        if (p is null)
            return null;
        if (string.IsNullOrWhiteSpace(p.Detail))
            return p.Phase;
        // Agent details often already start with "Failed: …"; Phase is also "Failed" — avoid "Failed: Failed: …".
        if (p.Detail.StartsWith(p.Phase + ":", StringComparison.OrdinalIgnoreCase)
            || p.Detail.StartsWith(p.Phase + " ", StringComparison.OrdinalIgnoreCase)
            || string.Equals(p.Detail, p.Phase, StringComparison.OrdinalIgnoreCase))
            return p.Detail;
        return $"{p.Phase}: {p.Detail}";
    }

    public enum ClientVersionStatus
    {
        UpToDate,
        OutOfDate,
        Missing,
        NoBaseline
    }

    public sealed record ClientRow(
        int MachineId,
        string Hostname,
        string? FriendlyName,
        string? LastIp,
        string? MachineGroup,
        string? Region,
        string? Office,
        string? AgentVersion,
        int? SimpleVersion,
        int? VersionsBehind,
        DateTimeOffset LastSeenUtc,
        bool IsOnline,
        bool IsInUse,
        string? ActiveUser,
        SessionState? ActiveUserState,
        string? ClientUpdateProgressJson,
        ClientVersionStatus Status)
    {
        public string DisplayName =>
            string.IsNullOrWhiteSpace(FriendlyName) ? Hostname : FriendlyName!;
    }

    public static string ActiveUserBadgeClass(SessionState? state) => state switch
    {
        SessionState.Active => "badge-active",
        SessionState.Disconnected => "badge-expired",
        _ => "badge-ended"
    };

    public static string ActiveUserLabel(SessionState? state) => state switch
    {
        SessionState.Active => "Active",
        SessionState.Disconnected => "Disconnected",
        _ => ""
    };
}
