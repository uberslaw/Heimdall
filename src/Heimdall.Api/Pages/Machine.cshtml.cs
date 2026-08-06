using Heimdall.Api.Services;
using Heimdall.Shared.Contracts;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace Heimdall.Api.Pages;

public class MachineModel(
    StatsQueryService stats,
    AppListService appLists,
    ConfigService config,
    TuflowRunService tuflowRuns) : PageModel
{
    [BindProperty(SupportsGet = true)]
    public string? Hostname { get; set; }

    [BindProperty(SupportsGet = true)]
    public string Range { get; set; } = "7d";

    [BindProperty(SupportsGet = true)]
    public List<string> Apps { get; set; } = [];

    [BindProperty]
    public int ApplyAppListId { get; set; }

    [BindProperty]
    public int RemoveAssignmentId { get; set; }

    [BindProperty]
    public List<string> TrackedProcesses { get; set; } = [];

    public MachineDetailSnapshot? Detail { get; private set; }
    public bool HostNotFound { get; private set; }
    public string RangeLabel { get; private set; } = "7 day";
    public int RangeDays { get; private set; } = 7;

    public AppListService.MachineAppListsView? AppListsView { get; private set; }
    public IReadOnlyList<AppListService.AppListPickerRow> AppListPicker { get; private set; } = [];
    public IReadOnlyList<string> MachineExcludedProcesses { get; private set; } = [];

    /// <summary>Null-if-not-Flood-enrolled — the .cshtml hides the whole TUFLOW panel when this is null
    /// or FloodEnrolled is false. See TuflowRunService.GetMachineViewAsync.</summary>
    public TuflowMachineView? Tuflow { get; private set; }

    public static string FormatLocalTimestamp(DateTimeOffset utc) =>
        RemoteMachineService.FormatAgentContact(utc);

    public static string TuflowStateBadgeClass(string? state) => state switch
    {
        TuflowRunStates.Running => "badge-active",
        TuflowRunStates.Starting or TuflowRunStates.StopRequested => "badge-ended",
        TuflowRunStates.Completed or TuflowRunStates.Stopped => "badge-local",
        TuflowRunStates.Failed => "badge-expired",
        _ => "badge-ended"
    };

    public static string FormatDuration(DateTimeOffset? startedUtc, DateTimeOffset? endedUtc)
    {
        if (startedUtc is not DateTimeOffset start)
            return "—";
        var end = endedUtc ?? DateTimeOffset.UtcNow;
        var span = end - start;
        return span.TotalHours >= 1
            ? $"{(int)span.TotalHours}h {span.Minutes}m"
            : $"{(int)span.TotalMinutes}m {span.Seconds}s";
    }

    public async Task<IActionResult> OnGetAsync(CancellationToken ct)
    {
        await LoadAsync(ct);
        return Page();
    }

    public async Task<IActionResult> OnPostApplyAppListAsync(CancellationToken ct)
    {
        var host = Hostname?.Trim();
        if (string.IsNullOrWhiteSpace(host) || ApplyAppListId <= 0)
        {
            TempData["Error"] = "Pick an app list to apply.";
            return RedirectToMachine(host);
        }

        await appLists.AssignAsync(
            ApplyAppListId,
            [(ConfigScope.Machine, host)],
            ct);
        TempData["Message"] = "App list applied to this machine.";
        return RedirectToMachine(host);
    }

    public async Task<IActionResult> OnPostRemoveAppListAsync(CancellationToken ct)
    {
        var host = Hostname?.Trim();
        if (string.IsNullOrWhiteSpace(host) || RemoveAssignmentId <= 0)
        {
            TempData["Error"] = "Could not remove that assignment.";
            return RedirectToMachine(host);
        }

        var view = await appLists.GetEffectiveForHostAsync(host, ct);
        var assignment = view.ActiveLists.FirstOrDefault(a => a.AssignmentId == RemoveAssignmentId);
        if (assignment is null || !assignment.CanUnassign)
        {
            TempData["Error"] = "Only machine-scoped assignments can be removed here. Inherited lists are managed on App lists.";
            return RedirectToMachine(host);
        }

        await appLists.UnassignAsync(RemoveAssignmentId, ct);
        TempData["Message"] = $"Removed “{assignment.Name}” from this machine.";
        return RedirectToMachine(host);
    }

    public async Task<IActionResult> OnPostSaveTrackingOverridesAsync(CancellationToken ct)
    {
        var host = Hostname?.Trim();
        if (string.IsNullOrWhiteSpace(host))
        {
            TempData["Error"] = "Missing hostname.";
            return RedirectToPage("/Index");
        }

        var view = await appLists.GetEffectiveForHostAsync(host, ct);
        var tracked = TrackedProcesses.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var excludes = view.MergedProcesses
            .Where(p => !tracked.Contains(p))
            .ToList();

        await config.SetMachineExcludeProcessesAsync(host, excludes, ct);
        TempData["Message"] = excludes.Count == 0
            ? "Machine tracking overrides cleared — all merged apps are tracked."
            : $"Tracking disabled for {excludes.Count} app(s) on this machine only.";
        return RedirectToMachine(host);
    }

    private async Task LoadAsync(CancellationToken ct)
    {
        var (key, label, days) = IndexModel.ResolveRange(Range);
        Range = key;
        RangeLabel = label;
        RangeDays = days;

        if (string.IsNullOrWhiteSpace(Hostname))
            return;

        var host = Hostname.Trim();
        var fromUtc = DateTimeOffset.UtcNow.AddDays(-days);
        var toUtc = DateTimeOffset.UtcNow;
        var selectedApps = Apps.Count > 0 ? Apps : null;

        Detail = await stats.QueryMachineDetailAsync(host, fromUtc, toUtc, selectedApps, ct);
        HostNotFound = Detail is null;
        if (HostNotFound)
            return;

        AppListsView = await appLists.GetEffectiveForHostAsync(host, ct);
        AppListPicker = await appLists.ListForPickerAsync(ct);
        MachineExcludedProcesses = await config.GetMachineExcludeProcessesAsync(host, ct);
        Tuflow = await tuflowRuns.GetMachineViewAsync(host, ct);
    }

    private IActionResult RedirectToMachine(string? host) =>
        RedirectToPage(new { hostname = host, range = Range });
}
