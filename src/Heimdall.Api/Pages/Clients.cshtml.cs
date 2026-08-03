using Heimdall.Api.Data;
using Heimdall.Api.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;

namespace Heimdall.Api.Pages;

/// <summary>
/// Admin console view: agent version reported per host (via heartbeat — see IngestService.UpsertMachineAsync)
/// compared against the "current published" client pack version (see PublishedVersionService). Green = core
/// version matches (build metadata after '+' ignored, same rule as Heimdall-VersionCompare.ps1); red = out of
/// date, missing, or unknown.
/// </summary>
public class ClientsModel(HeimdallDbContext db, PublishedVersionService publishedVersion) : PageModel
{
    public string? PublishedVersion { get; private set; }
    public string PublishedCoreVersion { get; private set; } = "";
    public DateTimeOffset? PublishedSetUtc { get; private set; }
    public string? PublishedSetBy { get; private set; }

    public List<ClientRow> Rows { get; private set; } = [];
    public int UpToDateCount { get; private set; }
    public int OutOfDateCount { get; private set; }
    public int UnknownCount { get; private set; }

    [BindProperty]
    public string? NewPublishedVersion { get; set; }

    public async Task OnGetAsync(CancellationToken ct)
    {
        await LoadAsync(ct);
    }

    public async Task<IActionResult> OnPostSetPublishedVersionAsync(CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(NewPublishedVersion))
        {
            TempData["Error"] = "Enter a version (e.g. 0.1.0) before saving.";
            return RedirectToPage();
        }

        var setBy = string.IsNullOrWhiteSpace(User.Identity?.Name) ? "Clients page" : User.Identity!.Name;
        await publishedVersion.SetAsync(NewPublishedVersion.Trim(), setBy, ct);
        TempData["Message"] = $"Published client version set to “{NewPublishedVersion.Trim()}”. Clients now compare against this.";
        return RedirectToPage();
    }

    private async Task LoadAsync(CancellationToken ct)
    {
        var info = await publishedVersion.GetAsync(ct);
        PublishedVersion = info.Version;
        PublishedCoreVersion = VersionCompare.GetCoreVersion(info.Version);
        PublishedSetUtc = info.SetUtc;
        PublishedSetBy = info.SetBy;

        var now = DateTimeOffset.UtcNow;
        var onlineCutoff = now.AddMinutes(-5);

        var machines = await db.Machines.AsNoTracking()
            .OrderBy(m => m.Hostname)
            .ToListAsync(ct);

        Rows = machines.Select(m =>
        {
            var core = VersionCompare.GetCoreVersion(m.AgentVersion);
            ClientVersionStatus status;
            if (string.IsNullOrWhiteSpace(PublishedVersion))
                status = ClientVersionStatus.NoBaseline;
            else if (string.IsNullOrWhiteSpace(m.AgentVersion))
                status = ClientVersionStatus.Missing;
            else
                status = VersionCompare.CoreVersionsMatch(m.AgentVersion, PublishedVersion)
                    ? ClientVersionStatus.UpToDate
                    : ClientVersionStatus.OutOfDate;

            return new ClientRow(
                m.Hostname,
                m.MachineGroup,
                m.Region,
                m.Office,
                m.AgentVersion,
                core,
                m.LastSeenUtc,
                m.LastSeenUtc >= onlineCutoff,
                status);
        }).ToList();

        UpToDateCount = Rows.Count(r => r.Status == ClientVersionStatus.UpToDate);
        OutOfDateCount = Rows.Count(r => r.Status is ClientVersionStatus.OutOfDate or ClientVersionStatus.Missing);
        UnknownCount = Rows.Count(r => r.Status == ClientVersionStatus.NoBaseline);
    }

    public static string StatusBadgeClass(ClientVersionStatus status) => status switch
    {
        ClientVersionStatus.UpToDate => "badge-active",
        ClientVersionStatus.OutOfDate => "badge-expired",
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

    public static string FormatContact(DateTimeOffset utc) => RemoteMachineService.FormatAgentContact(utc);

    public enum ClientVersionStatus
    {
        UpToDate,
        OutOfDate,
        Missing,
        NoBaseline
    }

    public sealed record ClientRow(
        string Hostname,
        string? MachineGroup,
        string? Region,
        string? Office,
        string? AgentVersion,
        string CoreVersion,
        DateTimeOffset LastSeenUtc,
        bool IsOnline,
        ClientVersionStatus Status);
}
