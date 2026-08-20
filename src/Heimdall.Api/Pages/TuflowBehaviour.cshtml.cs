using Heimdall.Api.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace Heimdall.Api.Pages;

/// <summary>
/// Flood hub tab: TUFLOW run-behaviour analytics from TuflowBehaviourRuns
/// (DetectedStart/End, username, machine). Read-only — does not touch Live Active stamps.
/// </summary>
public class TuflowBehaviourModel(TuflowBehaviourService behaviour, FloodAccessGuard flood) : PageModel
{
    public static readonly int[] DayChoices = [7, 14, 30, 90, 180];

    [BindProperty(SupportsGet = true)]
    public int Days { get; set; } = 30;

    public TuflowBehaviourService.TuflowBehaviourAnalytics Analytics { get; private set; } =
        new(30, DateTimeOffset.UtcNow.AddDays(-30), 0, 0, 0, null, null, null, [], [], [], [], 0, false);

    public async Task<IActionResult> OnGetAsync(CancellationToken ct)
    {
        if (await flood.ForbidIfDeniedAsync(HttpContext) is { } denied)
            return denied;

        if (!OpsPartial.IsPartial(Request))
            return OpsPartial.RedirectToFloodTab(Request, "behaviour");

        Days = DayChoices.Contains(Days) ? Days : 30;
        Analytics = await behaviour.GetBehaviourAnalyticsAsync(Days, ct);
        return Page();
    }

    public static string FormatDuration(TimeSpan? t) =>
        t is not { } span
            ? "—"
            : span.TotalHours >= 1
                ? $"{(int)span.TotalHours}h {span.Minutes:D2}m"
                : $"{(int)span.TotalMinutes}m {span.Seconds:D2}s";

    public static string FormatPeakHour(int? hour) =>
        hour is int h ? $"{h:00}:00" : "—";

    public static string FormatHourBar(int count, int max) =>
        max <= 0 || count <= 0
            ? "0%"
            : $"{Math.Clamp(100.0 * count / max, 0, 100).ToString("0.##", System.Globalization.CultureInfo.InvariantCulture)}%";

    public static string FormatGpuPercent(double? pct) =>
        pct is { } v ? $"{v:0.0}%" : "—";

    /// <summary>Hover text for Avg GPU on a behaviour run row.</summary>
    public const string AvgGpuTitle =
        "Average of TUFLOW process GPU% samples during this run (SumGpu ÷ sample count). " +
        "Same source as Peak. Can exceed 100% when multiple GPU engines/instances are summed.";

    /// <summary>Hover text for Peak GPU — explains values over 100% (e.g. 624%).</summary>
    public const string PeakGpuTitle =
        "Highest TUFLOW process GPU% sample during this run (max of ProcessGpuPercent samples). " +
        "Windows “GPU Engine” counters are summed across engines/adapters for matching TUFLOW processes, " +
        "so values can exceed 100% on multi-engine or multi-GPU hosts (sanity cap 1000%). " +
        "The chart below prefers the same process GPU series but draws clamped to 0–100%, so Peak can look higher than the chart.";

    /// <summary>Hover text for High GPU time.</summary>
    public const string HighGpuTitle =
        "Seconds during this run where process GPU% was in histogram buckets ≥ 50% " +
        "(including the 100+ bucket when engines sum above 100%).";

    public static string FormatHighGpu(double seconds) =>
        seconds <= 0
            ? "—"
            : seconds >= 3600
                ? $"{seconds / 3600.0:0.0}h ≥50%"
                : seconds >= 60
                    ? $"{seconds / 60.0:0}m ≥50%"
                    : $"{seconds:0}s ≥50%";

    public static string FormatLocalStamp(DateTimeOffset utc) =>
        utc.ToLocalTime().ToString("dd/MM HH:mm");

    /// <summary>JSON array of multi-metric points for the zoomable run chart (HTML-attribute safe).</summary>
    public static string MetricSeriesJson(TuflowBehaviourService.BehaviourRunRow row)
    {
        if (row.GpuSeries.Count == 0)
            return "[]";

        var inv = System.Globalization.CultureInfo.InvariantCulture;
        var sb = new System.Text.StringBuilder(row.GpuSeries.Count * 48);
        sb.Append('[');
        for (var i = 0; i < row.GpuSeries.Count; i++)
        {
            var p = row.GpuSeries[i];
            if (i > 0) sb.Append(',');
            sb.Append("{\"t\":");
            sb.Append(p.SampledAtUtc.ToUnixTimeSeconds().ToString(inv));
            AppendNum(sb, "gpu", p.GpuPercent);
            AppendNum(sb, "cpu", p.CpuPercent);
            AppendNum(sb, "ram", p.RamPercent);
            AppendNum(sb, "diskW", p.DiskWriteMBps);
            AppendNum(sb, "netTx", p.NetworkOutMBps);
            sb.Append('}');
        }
        sb.Append(']');
        return sb.ToString().Replace("\"", "&quot;", StringComparison.Ordinal);
    }

    /// <summary>Backward-compatible alias.</summary>
    public static string GpuSeriesJson(TuflowBehaviourService.BehaviourRunRow row) =>
        MetricSeriesJson(row);

    private static void AppendNum(System.Text.StringBuilder sb, string key, double? value)
    {
        if (value is not { } v) return;
        sb.Append(",\"");
        sb.Append(key);
        sb.Append("\":");
        sb.Append(v.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture));
    }
}
