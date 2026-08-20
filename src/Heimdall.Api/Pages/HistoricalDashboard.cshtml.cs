using Heimdall.Api.Services;
using Heimdall.Shared.Contracts;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace Heimdall.Api.Pages;

public class HistoricalDashboardModel(
    FleetDashboardService fleet,
    FloodAccessGuard flood,
    CodeMeterLicenseHub codeMeterHub,
    Microsoft.Extensions.Options.IOptions<CodeMeterOptions> codeMeterOptions,
    Heimdall.Api.Data.HeimdallDbContext db) : PageModel
{
    [BindProperty(SupportsGet = true)]
    public string Tab { get; set; } = "live";

    [BindProperty(SupportsGet = true)]
    public string? Q { get; set; }

    [BindProperty(SupportsGet = true)]
    public string StatusFilter { get; set; } = "all";

    [BindProperty(SupportsGet = true)]
    public string HistMode { get; set; } = "totals";

    [BindProperty(SupportsGet = true)]
    public string Period { get; set; } = "today";

    [BindProperty]
    public int EnrollMachineId { get; set; }

    [BindProperty]
    public string? EnrollNotes { get; set; }

    [BindProperty]
    public int UnenrollId { get; set; }

    public IReadOnlyList<FleetDashboardService.EnrolledMachineRow> Enrolled { get; private set; } = [];
    public IReadOnlyList<FleetDashboardService.MachineSearchHit> SearchHits { get; private set; } = [];
    public IReadOnlyList<FleetDashboardService.MachineSearchHit> PickerMachines { get; private set; } = [];
    public IReadOnlyList<FleetDashboardService.LiveFleetRow> LiveRows { get; private set; } = [];
    public IReadOnlyList<FleetDashboardService.LiveFleetRow> FilteredLiveRows { get; private set; } = [];
    public FleetDashboardService.FleetMetrics Summary { get; private set; } = FleetDashboardService.FleetMetrics.Empty;
    public IReadOnlyList<(int MachineId, string Hostname, FleetDashboardService.FleetMetrics Metrics)> PerMachine { get; private set; } = [];
    public IReadOnlyList<(int MachineId, string Hostname, FleetDashboardService.FleetMetrics Metrics)> TopGpu { get; private set; } = [];
    public IReadOnlyList<(int MachineId, string Hostname, FleetDashboardService.FleetMetrics Metrics)> TopCpu { get; private set; } = [];
    public IReadOnlyList<(int MachineId, string Hostname, FleetDashboardService.FleetMetrics Metrics)> TopRam { get; private set; } = [];
    public IReadOnlyList<(int MachineId, string Hostname, FleetDashboardService.FleetMetrics Metrics)> TopDisk { get; private set; } = [];
    public IReadOnlyList<(int MachineId, string Hostname, FleetDashboardService.FleetMetrics Metrics)> TopActive { get; private set; } = [];
    public string PeriodLabel { get; private set; } = "Today";
    public bool IsAveragesMode { get; private set; }
    public int MachinesActiveToday { get; private set; }
    public double AvgActiveRuntimePerMachine { get; private set; }

    public bool LiveOnly { get; private set; }

    public FloodLiveLicenseDto LicenseStrip { get; private set; } =
        new(false, false, false, null, 32, null, null, 32, null, 0, 0, 0, 0, null, null);

    public (int Hpc, int Classic) LicenseSeatsFor(FleetDashboardService.LiveFleetRow row)
    {
        var snap = codeMeterHub.Latest;
        if (!codeMeterOptions.Value.Enabled || !snap.Available)
            return (0, 0);
        return snap.SeatsForIp(row.LastIp);
    }

    public string LicenseSeatDetailFor(FleetDashboardService.LiveFleetRow row, bool hpc)
    {
        var snap = codeMeterHub.Latest;
        if (!codeMeterOptions.Value.Enabled || !snap.Available)
            return "";
        return snap.SeatDetailForIp(row.LastIp, hpc);
    }

    /// <summary>Agent claim without double-counting HPC+Classic (max of the two when both reported).</summary>
    public static int? EffectiveClaimedSeats(int? claimedHpc, int? claimedClassic)
    {
        if (claimedHpc is null && claimedClassic is null) return null;
        return Math.Max(claimedHpc ?? 0, claimedClassic ?? 0);
    }

    /// <summary>CodeMeter count is authoritative (LastIp). Claim is agent estimate; mismatch is advisory only.</summary>
    public static string FormatLicenseCellHtml(int codeMeterSeats, int? claimedSeats, bool mismatch)
    {
        if (claimedSeats is null)
            return System.Net.WebUtility.HtmlEncode(codeMeterSeats.ToString());
        var cm = System.Net.WebUtility.HtmlEncode(codeMeterSeats.ToString());
        var claim = System.Net.WebUtility.HtmlEncode(claimedSeats.Value.ToString());
        var warn = mismatch ? " hd-lic-mismatch" : "";
        return $"<span class=\"hd-lic-cm\">{cm}</span><span class=\"hd-lic-claim{warn}\" title=\"Agent claim (estimate)\">/{claim}</span>";
    }

    public static string FormatLicenseCellTitle(
        int hpcSeats,
        int classicSeats,
        string? hpcDetail,
        string? classicDetail,
        int? claimedHpc,
        int? claimedClassic,
        string? claimDetail)
    {
        var effective = Math.Max(hpcSeats, classicSeats);
        var parts = new List<string>
        {
            $"Seats in use: {effective} = max(HPC {hpcSeats}, Classic {classicSeats}). TUFLOW GPU/HPC typically holds both products — do not add them."
        };
        if (!string.IsNullOrWhiteSpace(hpcDetail))
            parts.Add($"HPC: {hpcDetail}");
        else if (hpcSeats == 0)
            parts.Add("No HPC checkout at this machine LastIp");
        if (!string.IsNullOrWhiteSpace(classicDetail))
            parts.Add($"Classic: {classicDetail}");
        else if (classicSeats == 0)
            parts.Add("No Classic checkout at this machine LastIp");

        var claimed = EffectiveClaimedSeats(claimedHpc, claimedClassic);
        if (claimed is not null)
        {
            var ev = string.IsNullOrWhiteSpace(claimDetail) ? "" : $" — {claimDetail}";
            parts.Add($"Agent claim: {claimed} (max HPC {claimedHpc?.ToString() ?? "—"} / Classic {claimedClassic?.ToString() ?? "—"}){ev}");
            if (claimed.Value != effective)
                parts.Add("Mismatch: CodeMeter is source of truth; claim is from local process args (-nt/GPU).");
        }

        return string.Join(" · ", parts);
    }

    public async Task<IActionResult> OnGetAsync(CancellationToken ct)
    {
        Tab = NormalizeTab(Tab);
        if (Tab == "live")
        {
            if (await flood.ForbidIfLiveDeniedAsync(HttpContext) is { } liveDenied)
                return liveDenied;
        }
        else if (await flood.ForbidIfDeniedAsync(HttpContext) is { } denied)
        {
            return denied;
        }

        LiveOnly = flood.IsLiveOnly(HttpContext);

        if (!OpsPartial.IsPartial(Request))
            return OpsPartial.RedirectToFloodTab(Request, Tab);

        await LoadAsync(ct);
        return Page();
    }

    public async Task<IActionResult> OnPostEnrollAsync(CancellationToken ct)
    {
        if (await flood.ForbidIfDeniedAsync(HttpContext) is { } denied)
            return denied;

        var (ok, message) = await fleet.EnrollAsync(EnrollMachineId, EnrollNotes, ct);
        TempData[ok ? "Message" : "Error"] = message;
        var q = string.IsNullOrWhiteSpace(Q) ? "" : "&q=" + Uri.EscapeDataString(Q);
        return Redirect("/Flood?tab=enroll" + q);
    }

    public async Task<IActionResult> OnPostUnenrollAsync(CancellationToken ct)
    {
        if (await flood.ForbidIfDeniedAsync(HttpContext) is { } denied)
            return denied;

        var (ok, message) = await fleet.UnenrollAsync(UnenrollId, ct);
        TempData[ok ? "Message" : "Error"] = message;
        return Redirect("/Flood?tab=enroll");
    }

    private async Task LoadAsync(CancellationToken ct)
    {
        // Tab already normalized in OnGet when partial.
        Tab = NormalizeTab(Tab);
        Enrolled = await fleet.ListEnrolledAsync(ct);

        // Enroll/Unenroll used to rebuild live fleet + historical aggregates on every POST redirect.
        if (Tab == "enroll")
        {
            PickerMachines = await fleet.ListMachinesForEnrollmentPickerAsync(ct);
            if (!string.IsNullOrWhiteSpace(Q))
                SearchHits = await fleet.SearchMachinesAsync(Q, 25, ct);
            return;
        }

        LiveRows = await fleet.GetLiveFleetAsync(ct);
        if (!string.Equals(StatusFilter, "all", StringComparison.OrdinalIgnoreCase))
        {
            LiveRows = LiveRows.Where(r => StatusMatches(r.Status, StatusFilter)).ToList();
        }

        FilteredLiveRows = LiveRows;
        if (!string.IsNullOrWhiteSpace(Q))
        {
            var q = Q.Trim();
            FilteredLiveRows = LiveRows.Where(r =>
                r.Hostname.Contains(q, StringComparison.OrdinalIgnoreCase)
                || r.DisplayName.Contains(q, StringComparison.OrdinalIgnoreCase)
                || (r.FriendlyName?.Contains(q, StringComparison.OrdinalIgnoreCase) ?? false)
                || (r.Username?.Contains(q, StringComparison.OrdinalIgnoreCase) ?? false)
                || (r.LastIp?.Contains(q, StringComparison.OrdinalIgnoreCase) ?? false)).ToList();
        }

        MachinesActiveToday = LiveRows.Count(r => r.TodayActiveHours > 0.01 || r.Status == FleetDashboardService.FleetStatus.Active);

        // Live tab does not need period aggregates.
        if (Tab == "live")
        {
            AvgActiveRuntimePerMachine = 0;
            LicenseStrip = FloodLiveBroadcastService.BuildLicenseDto(
                codeMeterOptions.Value.Enabled,
                codeMeterHub.Latest,
                LiveRows.Select(r => r.LastIp),
                await FloodLiveBroadcastService.LoadCodeMeterIpHintsAsync(db, ct),
                codeMeterOptions.Value.PollSeconds);
            return;
        }

        IsAveragesMode = string.Equals(HistMode, "averages", StringComparison.OrdinalIgnoreCase);
        var (from, to) = ResolveUiPeriod(Period, IsAveragesMode);
        PeriodLabel = LabelForPeriod(Period, IsAveragesMode);
        Summary = await fleet.AggregateFleetAsync(from, to, ct);
        PerMachine = await fleet.AggregatePerMachineAsync(from, to, ct);

        if (IsAveragesMode)
        {
            var divisor = AverageDivisor(Period);
            Summary = Summary.DivideBy(divisor);
            PerMachine = PerMachine
                .Select(p => (p.MachineId, p.Hostname, p.Metrics.DivideBy(divisor)))
                .ToList();
        }

        var ranked = PerMachine.Where(p => p.Metrics.SampleCount > 0).ToList();
        TopGpu = ranked.OrderByDescending(p => p.Metrics.GpuHours).Take(5).ToList();
        TopCpu = ranked.OrderByDescending(p => p.Metrics.CpuHours).Take(5).ToList();
        TopRam = ranked.OrderByDescending(p => p.Metrics.RamGbHours).Take(5).ToList();
        TopDisk = ranked.OrderByDescending(p => p.Metrics.DiskReadGb + p.Metrics.DiskWriteGb).Take(5).ToList();
        TopActive = ranked.OrderByDescending(p => p.Metrics.ActiveRuntimeHours).Take(5).ToList();

        AvgActiveRuntimePerMachine = Enrolled.Count == 0
            ? 0
            : Summary.ActiveRuntimeHours / Enrolled.Count;
    }

    private static string NormalizeTab(string? tab) =>
        (tab ?? "live").Trim().ToLowerInvariant() switch
        {
            "historical" or "history" or "analytics" => "historical",
            "enroll" or "enrollment" => "enroll",
            _ => "live"
        };

    private static bool StatusMatches(FleetDashboardService.FleetStatus status, string filter) =>
        filter.Trim().ToLowerInvariant() switch
        {
            "active" => status == FleetDashboardService.FleetStatus.Active,
            "idle" => status == FleetDashboardService.FleetStatus.Idle,
            "notrunning" or "not-running" or "not_running" => status == FleetDashboardService.FleetStatus.NotRunning,
            _ => true
        };

    private static (DateTimeOffset From, DateTimeOffset? To) ResolveUiPeriod(string period, bool averages)
    {
        var now = DateTimeOffset.UtcNow;
        var today = new DateTimeOffset(now.Date, TimeSpan.Zero);
        var key = (period ?? "today").Trim().ToLowerInvariant();

        if (averages)
        {
            return key switch
            {
                "daily" => (today.AddDays(-1), today),
                "7d" or "7-day" => (today.AddDays(-7), today),
                "monthly" => (today.AddDays(-30), today),
                "all" or "all-time" => (DateTimeOffset.MinValue.AddYears(1), null),
                _ => (today.AddDays(-1), today)
            };
        }

        return FleetDashboardService.ResolvePeriod(key);
    }

    private static double AverageDivisor(string period) =>
        (period ?? "").Trim().ToLowerInvariant() switch
        {
            "daily" => 1,
            "7d" or "7-day" => 7,
            "monthly" => 30,
            "all" or "all-time" => 1,
            _ => 1
        };

    private static string LabelForPeriod(string period, bool averages)
    {
        var key = (period ?? "").Trim().ToLowerInvariant();
        if (averages)
        {
            return key switch
            {
                "daily" => "Daily average (yesterday)",
                "7d" or "7-day" => "7-day average",
                "monthly" => "Monthly average (30d)",
                "all" or "all-time" => "All-time average",
                _ => "Average"
            };
        }

        return key switch
        {
            "today" => "Today",
            "week" or "7d" => "This week (7d)",
            "month" => "This month",
            "30d" => "Last 30 days",
            "3m" or "90d" => "Last 3 months",
            "6m" => "Last 6 months",
            "year" or "365d" => "Last year",
            "all" or "all-time" => "All time",
            _ => period ?? "Today"
        };
    }

    public static string StatusLabel(FleetDashboardService.FleetStatus s) => s switch
    {
        FleetDashboardService.FleetStatus.Active => "Active",
        FleetDashboardService.FleetStatus.Idle => "Idle",
        FleetDashboardService.FleetStatus.NotRunning => "Not Running",
        _ => "—"
    };

    public static string StatusBadgeClass(FleetDashboardService.FleetStatus s) => s switch
    {
        FleetDashboardService.FleetStatus.Active => "badge-active",
        FleetDashboardService.FleetStatus.Idle => "badge-rdp",
        FleetDashboardService.FleetStatus.NotRunning => "badge-ended",
        _ => "badge-ended"
    };

    /// <summary>Fleet machines Status colours: Active green / Idle amber / Off red.</summary>
    public static string FleetStatusTextClass(FleetDashboardService.FleetStatus s) => s switch
    {
        FleetDashboardService.FleetStatus.Active => "hd-status-active",
        FleetDashboardService.FleetStatus.Idle => "hd-status-idle",
        FleetDashboardService.FleetStatus.NotRunning => "hd-status-off",
        _ => "hd-status-off"
    };

    public static string ActiveIdleLabel(FleetDashboardService.FleetStatus s) => s switch
    {
        FleetDashboardService.FleetStatus.Active => "Active",
        FleetDashboardService.FleetStatus.Idle => "Idle",
        FleetDashboardService.FleetStatus.NotRunning => "N/A",
        _ => "—"
    };

    /// <summary>Active column display fallback / legacy text key (Active/Idle/N/A or HH:mm dd/MM).</summary>
    public static string ActiveColumnLabel(FleetDashboardService.LiveFleetRow r)
    {
        if (TryGetActiveStamp(r, out var stamp))
            return $"{stamp.Time} {stamp.Date}";
        return ActiveIdleLabel(r.Status);
    }

    /// <summary>
    /// Numeric Active-column sort key for <c>data-sort="num"</c>.
    /// Desc (first click): start stamps → stop stamps → Active/Idle/N/A (N/A last within plain).
    /// Within a stamp tier, newer timestamps sort higher.
    /// </summary>
    public static long ActiveColumnSortValue(FleetDashboardService.LiveFleetRow r)
    {
        // 1e10 > any unix seconds (~1.7e9); keeps tiers from overlapping.
        const long tier = 10_000_000_000L;

        if ((string.Equals(r.DetectedRunState, TuflowBehaviourStates.Active, StringComparison.OrdinalIgnoreCase)
                || string.Equals(r.DetectedRunState, TuflowBehaviourStates.Watching, StringComparison.OrdinalIgnoreCase))
            && r.DetectedRunStartedUtc is { } start)
            return 2 * tier + start.ToUnixTimeSeconds();

        if (string.Equals(r.DetectedRunState, TuflowBehaviourStates.Ended, StringComparison.OrdinalIgnoreCase)
            && r.DetectedRunEndedUtc is { } end)
            return 1 * tier + end.ToUnixTimeSeconds();

        return r.Status switch
        {
            FleetDashboardService.FleetStatus.Active => 2,
            FleetDashboardService.FleetStatus.Idle => 1,
            FleetDashboardService.FleetStatus.NotRunning => 0,
            _ => -1
        };
    }

    /// <summary>
    /// When a detected run is Active/Watching or Ended, returns stacked time (HH:mm) + date (dd/MM).
    /// Start stamps use green (hd-status-active); stop stamps use red (hd-status-off).
    /// Stop stamps persist until the next Watching/Active run starts.
    /// </summary>
    public static bool TryGetActiveStamp(FleetDashboardService.LiveFleetRow r, out ActiveStamp stamp)
    {
        // Open run (Active or Watching): green start stamp from DetectedRunStartedUtc.
        if ((string.Equals(r.DetectedRunState, TuflowBehaviourStates.Active, StringComparison.OrdinalIgnoreCase)
                || string.Equals(r.DetectedRunState, TuflowBehaviourStates.Watching, StringComparison.OrdinalIgnoreCase))
            && r.DetectedRunStartedUtc is { } start)
        {
            stamp = ToActiveStamp(start, isStop: false);
            return true;
        }

        if (string.Equals(r.DetectedRunState, TuflowBehaviourStates.Ended, StringComparison.OrdinalIgnoreCase)
            && r.DetectedRunEndedUtc is { } end)
        {
            stamp = ToActiveStamp(end, isStop: true);
            return true;
        }

        stamp = default;
        return false;
    }

    public static ActiveStamp ToActiveStamp(DateTimeOffset utc, bool isStop)
    {
        var local = utc.ToLocalTime();
        return new ActiveStamp(
            local.ToString("HH:mm"),
            local.ToString("dd/MM"),
            isStop ? "hd-status-off" : "hd-status-active",
            isStop);
    }

    public readonly record struct ActiveStamp(string Time, string Date, string CssClass, bool IsStop);

    public static string ActiveColumnTitle(FleetDashboardService.LiveFleetRow r)
    {
        if (string.Equals(r.DetectedRunState, TuflowBehaviourStates.Active, StringComparison.OrdinalIgnoreCase)
            && r.DetectedRunStartedUtc is { } start)
        {
            return $"Run detected active since {start.ToLocalTime():dd/MM/yyyy HH:mm:ss} local "
                + $"(tuflow.exe CPU > {TuflowBehaviourDefaults.CpuPercentThreshold:0}% or GPU > {TuflowBehaviourDefaults.GpuPercentThreshold:0}% for "
                + $"{TuflowBehaviourDefaults.ConfirmIntervals} consecutive samples). Busy thresholds still apply to Today active hours.";
        }

        if (string.Equals(r.DetectedRunState, TuflowBehaviourStates.Watching, StringComparison.OrdinalIgnoreCase)
            && r.DetectedRunStartedUtc is { } seen)
        {
            return $"TUFLOW process first seen {seen.ToLocalTime():dd/MM/yyyy HH:mm:ss} local "
                + $"(awaiting CPU > {TuflowBehaviourDefaults.CpuPercentThreshold:0}% or GPU > {TuflowBehaviourDefaults.GpuPercentThreshold:0}% for "
                + $"{TuflowBehaviourDefaults.ConfirmIntervals} samples to confirm launch). Busy thresholds still apply to Today active hours.";
        }

        if (string.Equals(r.DetectedRunState, TuflowBehaviourStates.Ended, StringComparison.OrdinalIgnoreCase)
            && r.DetectedRunEndedUtc is { } end)
        {
            return $"Run stop detected at {end.ToLocalTime():dd/MM/yyyy HH:mm:ss} local "
                + $"(CPU ≤ {TuflowBehaviourDefaults.CpuPercentThreshold:0}% and GPU ≤ {TuflowBehaviourDefaults.GpuPercentThreshold:0}% "
                + $"or process gone for {TuflowBehaviourDefaults.ConfirmIntervals} consecutive samples).";
        }

        return r.Status switch
        {
            FleetDashboardService.FleetStatus.Active =>
                "TUFLOW running and busy (GPU > 5%, CPU > 10%, or disk read/write > 5 MB/s). Launch time appears once the process is tracked (or after CPU/GPU elevated for 2 samples).",
            FleetDashboardService.FleetStatus.Idle =>
                "TUFLOW running but below busy thresholds. Launch time appears once the process is tracked (or after CPU/GPU elevated for 2 samples).",
            FleetDashboardService.FleetStatus.NotRunning => "TUFLOW not detected.",
            _ => "No recent sample."
        };
    }

    public static string ActiveColumnCssClass(FleetDashboardService.LiveFleetRow r)
    {
        if (TryGetActiveStamp(r, out var stamp))
            return stamp.CssClass;
        return FleetStatusTextClass(r.Status);
    }

    public static string FormatLocalShort(DateTimeOffset utc) =>
        utc.ToLocalTime().ToString("HH:mm dd/MM");


    public static string FormatHours(double hours) =>
        hours < 0.05 && hours > -0.05 ? "0.0" : hours.ToString("0.0");

    public static string FormatGauge(double? value, string suffix = "%") =>
        value is null ? "—" : $"{value.Value:0.#}{suffix}";

    public static string FormatMb(double? mb) => FormatDataFromMb(mb);

    public static string FormatRamGb(double? mb) => FormatDataFromMb(mb);

    public static string FormatMBps(double? mbps) => FormatDataRateFromMBps(mbps);

    /// <summary>Auto-scale MB quantities so the coefficient stays under 1000 (KB/MB/GB/TB).</summary>
    public static string FormatDataFromMb(double? mb)
    {
        if (mb is null) return "—";
        if (Math.Abs(mb.Value) < 0.0005) return "0\u00A0MB";
        return FormatDataSize(mb.Value * 1024.0 * 1024.0);
    }

    /// <summary>Auto-scale MB/s rates so the coefficient stays under 1000 (KB/s … TB/s).</summary>
    public static string FormatDataRateFromMBps(double? mbps)
    {
        if (mbps is null) return "—";
        if (Math.Abs(mbps.Value) < 0.0005) return "0";
        return FormatDataSize(mbps.Value * 1024.0 * 1024.0) + "/s";
    }

    /// <summary>Format a byte magnitude with at most 3 digits before the unit steps up (1000-based).</summary>
    public static string FormatDataSize(double bytes)
    {
        var v = Math.Abs(bytes);
        string[] units = ["B", "KB", "MB", "GB", "TB", "PB"];
        var u = 0;
        while (v >= 1000.0 && u < units.Length - 1)
        {
            v /= 1000.0;
            u++;
        }

        var text = v >= 100 ? v.ToString("0")
            : v.ToString("0.0");
        // Non-breaking space keeps value + unit on one line in narrow Live columns.
        return $"{text}\u00A0{units[u]}";
    }

    public static string FormatContact(DateTimeOffset? utc)
    {
        if (utc is null) return "—";
        return RemoteMachineService.FormatAgentContact(utc.Value);
    }
}
