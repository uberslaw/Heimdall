using System.Text.Json;
using Heimdall.Api.Data;
using Heimdall.Api.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;

namespace Heimdall.Api.Pages;

public class HistoricalDashboardMachineModel(FleetDashboardService fleet, HeimdallDbContext db, FloodAccessGuard flood) : PageModel
{
    [BindProperty(SupportsGet = true)]
    public int Id { get; set; }

    [BindProperty(SupportsGet = true)]
    public string Range { get; set; } = "7d";

    public string Hostname { get; private set; } = "";
    public string? LastIp { get; private set; }
    public bool NotFoundMachine { get; private set; }
    public bool IsEnrolled { get; private set; }
    public FleetDashboardService.FleetMetrics Summary { get; private set; } = FleetDashboardService.FleetMetrics.Empty;
    public IReadOnlyList<FleetDashboardService.TimeSeriesPoint> Series { get; private set; } = [];
    public string ChartJson { get; private set; } = "{}";
    public string RangeLabel { get; private set; } = "7 days";

    public async Task<IActionResult> OnGetAsync(CancellationToken ct)
    {
        if (flood.ForbidIfDenied(HttpContext) is { } denied)
            return denied;

        var machine = await db.Machines.AsNoTracking().FirstOrDefaultAsync(m => m.Id == Id, ct);
        if (machine is null)
        {
            NotFoundMachine = true;
            return Page();
        }

        Hostname = machine.Hostname;
        LastIp = machine.LastIp;
        IsEnrolled = await db.FleetDashboardMachines.AsNoTracking().AnyAsync(e => e.MachineId == Id, ct);

        var rangeKey = NormalizeRange(Range);
        Range = rangeKey;
        RangeLabel = LabelForRange(rangeKey);
        var (from, to) = FleetDashboardService.ResolvePeriod(rangeKey);
        Summary = await fleet.AggregateMachineAsync(Id, from, to, ct);

        var bucket = FleetDashboardService.ResolveChartBucket(rangeKey);
        Series = await fleet.GetTimeSeriesAsync(Id, from, to, bucket, ct);

        ChartJson = JsonSerializer.Serialize(new
        {
            labels = Series.Select(p => p.BucketUtc.ToLocalTime().ToString("g")).ToList(),
            cpu = Series.Select(p => p.CpuPercent).ToList(),
            gpu = Series.Select(p => p.GpuPercent).ToList(),
            ramMb = Series.Select(p => p.RamUsedMb).ToList(),
            diskRead = Series.Select(p => p.DiskReadMBps).ToList(),
            diskWrite = Series.Select(p => p.DiskWriteMBps).ToList(),
            netIn = Series.Select(p => p.NetworkInMBps).ToList(),
            netOut = Series.Select(p => p.NetworkOutMBps).ToList(),
            runtimeSamples = Series.Select(p => p.TuflowRunningCount).ToList(),
            activeSamples = Series.Select(p => p.ActiveCount).ToList()
        });

        return Page();
    }

    private static string NormalizeRange(string? range) =>
        (range ?? "7d").Trim().ToLowerInvariant() switch
        {
            "24h" or "1d" => "24h",
            "7d" => "7d",
            "30d" => "30d",
            "90d" or "3m" => "90d",
            "365d" or "year" or "1y" => "365d",
            _ => "7d"
        };

    private static string LabelForRange(string range) => range switch
    {
        "24h" => "Last 24 hours",
        "7d" => "Last 7 days",
        "30d" => "Last 30 days",
        "90d" => "Last 90 days",
        "365d" => "Last 365 days",
        _ => range
    };

    public static string FormatHours(double hours) =>
        hours < 0.01 ? "0" : hours.ToString("0.##");
}
