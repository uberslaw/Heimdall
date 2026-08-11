using System.Text.Json;
using Heimdall.Api.Data;
using Heimdall.Api.Services;
using Heimdall.Shared.Contracts;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;

namespace Heimdall.Api.Pages;

public class MachineModel(
    HeimdallDbContext db,
    StatsQueryService stats,
    AppListService appLists,
    ConfigService config,
    TuflowRunService tuflowRuns,
    FloodAccessGuard flood) : PageModel
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

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

    [BindProperty]
    public string? DiskScanRoot { get; set; }

    [BindProperty]
    public int DiskScanMinFileMb { get; set; } = 100;

    [BindProperty(SupportsGet = true)]
    public bool EditFriendly { get; set; }

    [BindProperty]
    public string? FriendlyNameInput { get; set; }

    public MachineDetailSnapshot? Detail { get; private set; }
    public bool HostNotFound { get; private set; }
    public string RangeLabel { get; private set; } = "7 day";
    public int RangeDays { get; private set; } = 7;

    public string? FriendlyName { get; private set; }
    public string? LastIp { get; private set; }
    public int? MachineTeamId { get; private set; }
    public IReadOnlyList<Teams.TeamPageHelpers.TeamOption> TeamOptions { get; private set; } = [];
    public MachineResourceGlance? ResourceGlance { get; private set; }

    [BindProperty]
    public int SelectedTeamId { get; set; }

    public AppListService.MachineAppListsView? AppListsView { get; private set; }
    public IReadOnlyList<AppListService.AppListPickerRow> AppListPicker { get; private set; } = [];
    public IReadOnlyList<string> MachineExcludedProcesses { get; private set; } = [];

    public bool PendingInventory { get; private set; }
    public DateTimeOffset? InventoryCollectedUtc { get; private set; }
    public IReadOnlyList<DiskVolumeDto> DiskVolumes { get; private set; } = [];
    public DateTimeOffset? DiskVolumesUtc { get; private set; }

    public bool PendingDiskUsageScan { get; private set; }
    public string? PendingDiskScanRoot { get; private set; }
    public DateTimeOffset? PendingDiskScanRequestedUtc { get; private set; }
    public string? PendingDiskScanId { get; private set; }
    public int PendingDiskScanMaxSeconds { get; private set; } = 180;
    public DiskUsageScanProgressDto? DiskUsageProgress { get; private set; }
    public DiskUsageScanResultDto? DiskUsageScan { get; private set; }
    public DateTimeOffset? DiskUsageScanUtc { get; private set; }

    /// <summary>Null-if-not-Flood-enrolled — the .cshtml hides the whole TUFLOW panel when this is null
    /// or FloodEnrolled is false. See TuflowRunService.GetMachineViewAsync.</summary>
    public TuflowMachineView? Tuflow { get; private set; }

    /// <summary>TUFLOW panel visible only to FloodTeamEmails ∪ AdminEmails.</summary>
    public bool CanAccessFlood { get; private set; }

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

    public async Task<IActionResult> OnPostSaveFriendlyNameAsync(CancellationToken ct)
    {
        var host = Hostname?.Trim();
        if (string.IsNullOrWhiteSpace(host))
        {
            TempData["Error"] = "Missing hostname.";
            return RedirectToPage("/Index");
        }

        var machine = await db.Machines.FirstOrDefaultAsync(m => m.Hostname == host, ct);
        if (machine is null)
        {
            TempData["Error"] = "Machine not found.";
            return RedirectToMachine(host);
        }

        var trimmed = string.IsNullOrWhiteSpace(FriendlyNameInput) ? null : FriendlyNameInput.Trim();
        if (trimmed is { Length: > 80 })
            trimmed = trimmed[..80];

        machine.FriendlyName = trimmed;
        await db.SaveChangesAsync(ct);
        TempData["Message"] = trimmed is null
            ? "Friendly name cleared."
            : $"Friendly name set to “{trimmed}”.";
        return RedirectToMachine(host);
    }

    public async Task<IActionResult> OnPostSaveTeamAsync(CancellationToken ct)
    {
        var host = Hostname?.Trim();
        if (string.IsNullOrWhiteSpace(host))
        {
            TempData["Error"] = "Missing hostname.";
            return RedirectToPage("/Index");
        }

        var machine = await db.Machines.FirstOrDefaultAsync(m => m.Hostname == host, ct);
        if (machine is null)
        {
            TempData["Error"] = "Machine not found.";
            return RedirectToMachine(host);
        }

        if (SelectedTeamId <= 0)
        {
            machine.TeamId = null;
            await db.SaveChangesAsync(ct);
            TempData["Message"] = "Team affiliation cleared.";
            return RedirectToMachine(host);
        }

        var team = await db.Teams.AsNoTracking().FirstOrDefaultAsync(t => t.Id == SelectedTeamId, ct);
        if (team is null)
        {
            TempData["Error"] = "Team not found.";
            return RedirectToMachine(host);
        }

        machine.TeamId = team.Id;
        await db.SaveChangesAsync(ct);
        TempData["Message"] = $"Team set to “{team.Name}”.";
        return RedirectToMachine(host);
    }

    public async Task<IActionResult> OnPostRequestInventoryAsync(CancellationToken ct)
    {
        var host = Hostname?.Trim();
        if (string.IsNullOrWhiteSpace(host))
        {
            TempData["Error"] = "Missing hostname.";
            return RedirectToPage("/Index");
        }

        try
        {
            await appLists.RequestAgentInventoryAsync(host, ct);
            TempData["Message"] =
                $"Full process inventory requested for {host}. Agent picks this up on next config refresh (~5 min), then uploads on next heartbeat (~1 min).";
        }
        catch (Exception ex)
        {
            TempData["Error"] = ex.Message;
        }

        return RedirectToMachine(host);
    }

    public async Task<IActionResult> OnPostRequestDiskUsageScanAsync(CancellationToken ct)
    {
        var host = Hostname?.Trim();
        if (string.IsNullOrWhiteSpace(host))
        {
            TempData["Error"] = "Missing hostname.";
            return RedirectToPage("/Index");
        }

        var root = (DiskScanRoot ?? "C:\\").Trim();
        if (root.Length == 2 && root[1] == ':')
            root += "\\";
        if (!System.Text.RegularExpressions.Regex.IsMatch(root, @"^[A-Za-z]:\\"))
        {
            TempData["Error"] = "Root must be a local drive path like C:\\";
            return RedirectToMachine(host);
        }

        var minMb = DiskScanMinFileMb <= 0 ? 100 : Math.Clamp(DiskScanMinFileMb, 10, 10240);
        var machine = await db.Machines.FirstOrDefaultAsync(m => m.Hostname == host, ct);
        if (machine is null)
        {
            TempData["Error"] = "Machine not found.";
            return RedirectToMachine(host);
        }

        var requestedUtc = DateTimeOffset.UtcNow;
        var request = new DiskUsageScanRequestDto
        {
            ScanId = Guid.NewGuid().ToString("N"),
            RootPath = root,
            RequestedUtc = requestedUtc,
            MinFileMb = minMb,
            TopFolderCount = 25,
            MaxLargeFiles = 100,
            MaxSeconds = 180
        };
        machine.PendingDiskUsageScanJson = JsonSerializer.Serialize(request);
        machine.DiskUsageScanProgressJson = JsonSerializer.Serialize(new DiskUsageScanProgressDto
        {
            ScanId = request.ScanId,
            RootPath = root,
            Status = DiskUsageScanStatuses.Queued,
            UpdatedUtc = requestedUtc,
            Message = "Waiting for agent (usually within ~20s)"
        });
        await db.SaveChangesAsync(ct);
        TempData["Message"] =
            $"Disk usage scan queued for {host} ({root}, files ≥ {minMb} MB) at {FormatLocalTimestamp(requestedUtc)}. Agent usually picks this up within ~20s; scan itself can take up to ~3 min.";
        return RedirectToMachine(host);
    }

    public async Task<IActionResult> OnGetDiskUsageStatusAsync(CancellationToken ct)
    {
        var host = Hostname?.Trim();
        if (string.IsNullOrWhiteSpace(host))
            return new JsonResult(new { ok = false, error = "Missing hostname" });

        var machine = await db.Machines.AsNoTracking()
            .FirstOrDefaultAsync(m => m.Hostname == host, ct);
        if (machine is null)
            return new JsonResult(new { ok = false, error = "Machine not found" });

        DiskUsageScanRequestDto? pending = null;
        if (!string.IsNullOrWhiteSpace(machine.PendingDiskUsageScanJson))
        {
            try
            {
                pending = JsonSerializer.Deserialize<DiskUsageScanRequestDto>(machine.PendingDiskUsageScanJson, JsonOptions);
            }
            catch { /* ignore */ }
        }

        DiskUsageScanProgressDto? progress = null;
        if (!string.IsNullOrWhiteSpace(machine.DiskUsageScanProgressJson))
        {
            try
            {
                progress = JsonSerializer.Deserialize<DiskUsageScanProgressDto>(machine.DiskUsageScanProgressJson, JsonOptions);
            }
            catch { /* ignore */ }
        }

        var result = DeserializeDiskScan(machine.DiskUsageScanJson);
        var pendingActive = pending is not null;

        return new JsonResult(new
        {
            ok = true,
            pending = pendingActive,
            complete = !pendingActive,
            requestedUtc = pending?.RequestedUtc,
            scanId = pending?.ScanId ?? progress?.ScanId ?? result?.ScanId,
            rootPath = pending?.RootPath ?? progress?.RootPath ?? result?.RootPath,
            maxSeconds = pending?.MaxSeconds ?? 180,
            progress,
            resultUtc = machine.DiskUsageScanUtc,
            resultScanId = result?.ScanId
        });
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

        TeamOptions = await Teams.TeamPageHelpers.LoadTeamOptionsAsync(db);
        AppListsView = await appLists.GetEffectiveForHostAsync(host, ct);
        AppListPicker = await appLists.ListForPickerAsync(ct);
        MachineExcludedProcesses = await config.GetMachineExcludeProcessesAsync(host, ct);
        CanAccessFlood = flood.CanAccessFlood(HttpContext);
        if (CanAccessFlood)
            Tuflow = await tuflowRuns.GetMachineViewAsync(host, ct);

        var machine = await db.Machines.AsNoTracking()
            .FirstOrDefaultAsync(m => m.Hostname == host, ct);
        if (machine is not null)
        {
            FriendlyName = string.IsNullOrWhiteSpace(machine.FriendlyName) ? null : machine.FriendlyName.Trim();
            LastIp = string.IsNullOrWhiteSpace(machine.LastIp) ? null : machine.LastIp.Trim();
            MachineTeamId = machine.TeamId;
            SelectedTeamId = machine.TeamId ?? 0;
            if (EditFriendly)
                FriendlyNameInput = FriendlyName;
            ResourceGlance = await LoadResourceGlanceAsync(machine.Id, ct);

            PendingInventory = machine.PendingAppAnalysis;
            InventoryCollectedUtc = machine.InventoryCollectedUtc;
            DiskVolumesUtc = machine.DiskVolumesUtc;
            DiskVolumes = DeserializeVolumes(machine.DiskVolumesJson);
            DiskUsageScanUtc = machine.DiskUsageScanUtc;
            DiskUsageScan = DeserializeDiskScan(machine.DiskUsageScanJson);
            if (!string.IsNullOrWhiteSpace(machine.DiskUsageScanProgressJson))
            {
                try
                {
                    DiskUsageProgress = JsonSerializer.Deserialize<DiskUsageScanProgressDto>(
                        machine.DiskUsageScanProgressJson, JsonOptions);
                }
                catch { /* ignore */ }
            }
            if (!string.IsNullOrWhiteSpace(machine.PendingDiskUsageScanJson))
            {
                PendingDiskUsageScan = true;
                try
                {
                    var pending = JsonSerializer.Deserialize<DiskUsageScanRequestDto>(machine.PendingDiskUsageScanJson, JsonOptions);
                    PendingDiskScanRoot = pending?.RootPath;
                    PendingDiskScanId = pending?.ScanId;
                    PendingDiskScanRequestedUtc = pending is { RequestedUtc: { } req } && req > DateTimeOffset.UnixEpoch
                        ? req
                        : null;
                    if (pending is not null)
                    {
                        DiskScanMinFileMb = pending.MinFileMb;
                        PendingDiskScanMaxSeconds = pending.MaxSeconds > 0 ? pending.MaxSeconds : 180;
                    }
                }
                catch { /* ignore */ }
            }
        }
    }

    public static string FormatBytes(long bytes)
    {
        if (bytes < 1024) return $"{bytes} B";
        double v = bytes;
        string[] units = ["KB", "MB", "GB", "TB"];
        var i = -1;
        do
        {
            v /= 1024;
            i++;
        } while (v >= 1024 && i < units.Length - 1);
        return $"{v:0.##} {units[i]}";
    }

    public static string FormatGlancePct(double? v) => v is double n ? $"{n:0.#}%" : "—";

    private async Task<MachineResourceGlance?> LoadResourceGlanceAsync(int machineId, CancellationToken ct)
    {
        // SQLite cannot ORDER BY DateTimeOffset — use Id (fleet is append-only) / single live row per machine.
        var fleet = await db.FleetMetricSnapshots.AsNoTracking()
            .Where(s => s.MachineId == machineId)
            .OrderByDescending(s => s.Id)
            .Select(s => new
            {
                s.SampledAtUtc,
                s.CpuPercent,
                s.GpuPercent,
                s.DiskReadMBps,
                s.DiskWriteMBps,
                s.NetworkInMBps,
                s.NetworkOutMBps
            })
            .FirstOrDefaultAsync(ct);

        // Live metrics are upserted (one row per machine).
        var live = await db.MachineResourceMetrics.AsNoTracking()
            .Where(m => m.MachineId == machineId)
            .Select(m => new
            {
                m.SampledAtUtc,
                m.CpuPercent,
                m.GpuPercent,
                m.DiskReadBytesPerSec,
                m.DiskWriteBytesPerSec,
                m.DiskReadLevel,
                m.DiskWriteLevel
            })
            .FirstOrDefaultAsync(ct);

        if (fleet is null && live is null)
            return null;

        // Prefer the newer sample. Fleet includes network; live Staff samples do not.
        if (fleet is not null && (live is null || fleet.SampledAtUtc >= live.SampledAtUtc))
        {
            var disk = DiskActivityLevel.ClassifyCombinedMBps(fleet.DiskReadMBps, fleet.DiskWriteMBps);
            var net = NetworkActivityLevel.ClassifyCombinedMBps(fleet.NetworkInMBps, fleet.NetworkOutMBps);
            return new MachineResourceGlance(
                fleet.SampledAtUtc,
                fleet.CpuPercent,
                fleet.GpuPercent,
                disk,
                net,
                "fleet");
        }

        // Live path: take the higher of read/write levels (Med > Low, High > Med).
        var diskLive = RankLevel(live!.DiskReadLevel) >= RankLevel(live.DiskWriteLevel)
            ? NormalizeLevel(live.DiskReadLevel)
            : NormalizeLevel(live.DiskWriteLevel);
        if (diskLive == DiskActivityLevel.Low
            && ((live.DiskReadBytesPerSec ?? 0) > 0 || (live.DiskWriteBytesPerSec ?? 0) > 0))
        {
            diskLive = DiskActivityLevel.Classify(
                Math.Max(live.DiskReadBytesPerSec ?? 0, live.DiskWriteBytesPerSec ?? 0));
        }

        return new MachineResourceGlance(
            live.SampledAtUtc,
            live.CpuPercent,
            live.GpuPercent,
            diskLive,
            "—",
            "live");
    }

    private static int RankLevel(string? level) => NormalizeLevel(level) switch
    {
        DiskActivityLevel.High => 2,
        DiskActivityLevel.Med => 1,
        _ => 0
    };

    private static string NormalizeLevel(string? level) => level?.Trim() switch
    {
        DiskActivityLevel.High => DiskActivityLevel.High,
        DiskActivityLevel.Med or "Medium" => DiskActivityLevel.Med,
        _ => DiskActivityLevel.Low
    };

    private static DiskUsageScanResultDto? DeserializeDiskScan(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return null;
        try
        {
            return JsonSerializer.Deserialize<DiskUsageScanResultDto>(json, JsonOptions);
        }
        catch
        {
            return null;
        }
    }

    private static IReadOnlyList<DiskVolumeDto> DeserializeVolumes(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
            return [];
        try
        {
            return JsonSerializer.Deserialize<List<DiskVolumeDto>>(json, JsonOptions) ?? [];
        }
        catch
        {
            return [];
        }
    }

    private IActionResult RedirectToMachine(string? host) =>
        RedirectToPage(new { hostname = host, range = Range });
}

public sealed record MachineResourceGlance(
    DateTimeOffset SampledAtUtc,
    double? CpuPercent,
    double? GpuPercent,
    string DiskLevel,
    string NetworkLevel,
    string Source);
