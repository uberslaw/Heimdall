using Heimdall.Api.Data;
using Heimdall.Api.Services;
using Heimdall.Shared.Contracts;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace Heimdall.Api.Pages;

/// <summary>
/// Staff Access group page — lists only the machines this group was granted, with Remote-like fields plus
/// live(ish) resource metrics. Sampling is fan-in ref-counted on the API (see LiveSamplingService); this
/// page's JS starts an explicit timed live session (default 30 min, up to 8 h): while active it heartbeats
/// a per-tab viewer id every ~20s, sends leave on stop/timer/unload, and polls /api/staff/groups/{id}/metrics
/// every ~10s to refresh metric cells without a full reload.
/// </summary>
public class StaffModel(
    RemoteAccessGroupService groups,
    RemoteMachineService remote,
    LiveSamplingService sampling,
    StaffAccessGuard guard) : PageModel
{
    [BindProperty(SupportsGet = true)]
    public int Id { get; set; }

    public RemoteAccessGroup? Group { get; private set; }
    public List<StaffMachineRow> Rows { get; private set; } = [];
    public string? SignedInEmail { get; private set; }
    public bool IsAdminPreview { get; private set; }

    [BindProperty]
    public bool FavoritesOnly { get; set; }

    [BindProperty]
    public string? FavoriteProcessInput { get; set; }

    [BindProperty]
    public int FavoriteId { get; set; }

    public async Task<IActionResult> OnGetAsync(CancellationToken ct)
    {
        if (!await guard.EnsureWindowsAuthAsync(HttpContext))
            return new EmptyResult();

        if (await guard.CanAccessGroupAsync(HttpContext, Id, groups, ct))
        {
            IsAdminPreview = guard.IsAdminPreviewActive(HttpContext) && guard.IsConfiguredAdmin(HttpContext);
            SignedInEmail = IsAdminPreview
                ? "admin preview"
                : guard.TryGetVerifiedEmail(HttpContext);
        }
        else if (guard.TryGetVerifiedEmail(HttpContext) is null && !guard.IsAdminPreviewActive(HttpContext))
        {
            return RedirectToPage("/StaffAccess");
        }
        else
        {
            TempData["Error"] = guard.IsAdminPreviewActive(HttpContext)
                ? "Admin preview expired or your Windows login is not in Heimdall:StaffAccess:AdminEmails."
                : "Your account does not have access to that Remote Access Group.";
            return RedirectToPage("/StaffAccess");
        }

        await LoadAsync(ct);
        return Page();
    }

    public async Task<IActionResult> OnPostToggleFavoritesOnlyAsync(CancellationToken ct)
    {
        if (!await RequireAccessAsync(ct)) return RedirectToPage("/StaffAccess");
        await groups.SetFavoritesOnlyAsync(Id, FavoritesOnly, ct);
        return RedirectToPage(new { id = Id });
    }

    public async Task<IActionResult> OnPostAddFavoriteAsync(CancellationToken ct)
    {
        if (!await RequireAccessAsync(ct)) return RedirectToPage("/StaffAccess");

        var names = RemoteAccessGroupService.SplitMultiValue(FavoriteProcessInput).ToList();
        if (names.Count == 0)
        {
            TempData["Error"] = "Enter a process name (e.g. Revit).";
            return RedirectToPage(new { id = Id });
        }

        foreach (var name in names)
            await groups.AddFavoriteAsync(Id, name, ct);

        return RedirectToPage(new { id = Id });
    }

    public async Task<IActionResult> OnPostRemoveFavoriteAsync(CancellationToken ct)
    {
        if (!await RequireAccessAsync(ct)) return RedirectToPage("/StaffAccess");
        await groups.RemoveFavoriteAsync(FavoriteId, ct);
        return RedirectToPage(new { id = Id });
    }

    private async Task<bool> RequireAccessAsync(CancellationToken ct)
    {
        if (!await guard.EnsureWindowsAuthAsync(HttpContext))
            return false;

        return await guard.CanAccessGroupAsync(HttpContext, Id, groups, ct);
    }

    private async Task LoadAsync(CancellationToken ct)
    {
        Group = await groups.GetGroupAsync(Id, ct);
        if (Group is null) return;

        FavoritesOnly = Group.FavoritesOnly;
        var friendlyByHost = Group.Machines.ToDictionary(
            m => m.Hostname,
            m => m.FriendlyName,
            StringComparer.OrdinalIgnoreCase);
        var hostnames = Group.Machines.Select(m => m.Hostname).OrderBy(h => h, StringComparer.OrdinalIgnoreCase).ToList();
        var remoteRows = await remote.ListForHostnamesAsync(hostnames, ct);
        var metrics = await sampling.GetLatestMetricsAsync(hostnames, ct);

        Rows = remoteRows
            .Select(r =>
            {
                friendlyByHost.TryGetValue(r.Hostname, out var friendlyName);
                return new StaffMachineRow(
                    r.Hostname,
                    RemoteAccessGroupService.GetMachineDisplayName(r.Hostname, friendlyName),
                    friendlyName,
                    r.IsOnline,
                    r.LastIp,
                    r.LastSeenUtc,
                    r.TermServiceStatus,
                    metrics.TryGetValue(r.Hostname, out var m) ? m : EmptyMetric(r.Hostname));
            })
            .ToList();
    }

    private static LiveSamplingService.MachineMetricView EmptyMetric(string hostname) => new(
        hostname, null, false, null, null, null, null, null, null, null,
        "Low", "Low", [], [], [], [], [], [], false);

    public static string FormatContact(DateTimeOffset utc) => RemoteMachineService.FormatAgentContact(utc);

    public static string TermServiceBadgeClass(string? status) => RemoteMachinesModel.TermServiceBadgeClass(status);

    public static string FormatPercent(double? value) => value is double v ? $"{v:0.#}%" : "—";

    public static string FormatRam(LiveSamplingService.MachineMetricView m)
    {
        if (m.RamPercent is not double pct) return "—";
        if (m.RamUsedGb is double used && m.RamTotalGb is double total)
            return $"{pct:0.#}% ({used:0.#}/{total:0.#} GB)";
        return $"{pct:0.#}%";
    }

    public static string DiskLevelBadgeClass(string level) => level switch
    {
        DiskActivityLevel.High => "badge-expired",
        DiskActivityLevel.Med => "badge-warn",
        _ => "badge-active"
    };

    public static string SamplingStatusLabel(LiveSamplingService.MachineMetricView m)
    {
        if (!m.IsSamplingActive) return "Not sampling";
        if (m.SampledAtUtc is null) return "Waiting for first sample…";
        var age = DateTimeOffset.UtcNow - m.SampledAtUtc.Value;
        return age.TotalSeconds < 20 ? "Live" : $"Last sample {FormatContact(m.SampledAtUtc.Value)}";
    }

    public static string SamplingStatusBadgeClass(LiveSamplingService.MachineMetricView m)
    {
        if (!m.IsSamplingActive) return "badge-ended";
        if (m.SampledAtUtc is null) return "badge-warn";
        return "badge-active";
    }
}

public sealed record StaffMachineRow(
    string Hostname,
    string DisplayName,
    string? FriendlyName,
    bool IsOnline,
    string? LastIp,
    DateTimeOffset LastSeenUtc,
    string? TermServiceStatus,
    LiveSamplingService.MachineMetricView Metric);
