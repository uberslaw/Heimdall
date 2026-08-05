using Heimdall.Api.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace Heimdall.Api.Pages;

public class HistoricalDashboardModel(FleetDashboardService fleet) : PageModel
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

    public async Task OnGetAsync(CancellationToken ct)
    {
        await LoadAsync(ct);
    }

    public async Task<IActionResult> OnPostEnrollAsync(CancellationToken ct)
    {
        var (ok, message) = await fleet.EnrollAsync(EnrollMachineId, EnrollNotes, ct);
        TempData[ok ? "Message" : "Error"] = message;
        return RedirectToPage(new { tab = "enroll", q = Q });
    }

    public async Task<IActionResult> OnPostUnenrollAsync(CancellationToken ct)
    {
        var (ok, message) = await fleet.UnenrollAsync(UnenrollId, ct);
        TempData[ok ? "Message" : "Error"] = message;
        return RedirectToPage(new { tab = "enroll" });
    }

    private async Task LoadAsync(CancellationToken ct)
    {
        Tab = NormalizeTab(Tab);
        Enrolled = await fleet.ListEnrolledAsync(ct);

        // Enroll/Unenroll used to rebuild live fleet + historical aggregates on every POST redirect.
        if (Tab == "enroll")
        {
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
                || (r.Username?.Contains(q, StringComparison.OrdinalIgnoreCase) ?? false)
                || (r.LastIp?.Contains(q, StringComparison.OrdinalIgnoreCase) ?? false)).ToList();
        }

        MachinesActiveToday = LiveRows.Count(r => r.TodayActiveHours > 0.01 || r.Status == FleetDashboardService.FleetStatus.Active);

        // Live tab does not need period aggregates.
        if (Tab == "live")
        {
            AvgActiveRuntimePerMachine = 0;
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

    public static string ActiveIdleLabel(FleetDashboardService.FleetStatus s) => s switch
    {
        FleetDashboardService.FleetStatus.Active => "Active",
        FleetDashboardService.FleetStatus.Idle => "Idle",
        _ => "—"
    };

    public static string FormatHours(double hours) =>
        hours < 0.01 ? "0" : hours.ToString("0.##");

    public static string FormatGauge(double? value, string suffix = "%") =>
        value is null ? "—" : $"{value.Value:0.#}{suffix}";

    public static string FormatMb(double? mb) =>
        mb is null ? "—" : $"{mb.Value:0.#} MB";

    public static string FormatRamGb(double? mb) =>
        mb is null ? "—" : $"{mb.Value / 1024.0:0.##} GB";

    public static string FormatMBps(double? mbps) =>
        mbps is null ? "—" : $"{mbps.Value:0.##}";

    public static string FormatContact(DateTimeOffset? utc)
    {
        if (utc is null) return "—";
        return RemoteMachineService.FormatAgentContact(utc.Value);
    }
}
