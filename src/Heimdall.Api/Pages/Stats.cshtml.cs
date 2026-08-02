using Heimdall.Api.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace Heimdall.Api.Pages;

public class StatsModel(StatsQueryService stats) : PageModel
{
    public StatsScopeOptions ScopeOptions { get; private set; } = new([], [], [], [], [], []);
    public StatsSnapshot? Snapshot { get; private set; }

    [BindProperty(SupportsGet = true)]
    public string ScopeKind { get; set; } = nameof(StatsScopeKind.All);

    [BindProperty(SupportsGet = true)]
    public string? ScopeValue { get; set; }

    [BindProperty(SupportsGet = true)]
    public int Days { get; set; } = 7;

    [BindProperty(SupportsGet = true)]
    public string? From { get; set; }

    [BindProperty(SupportsGet = true)]
    public string? To { get; set; }

    [BindProperty(SupportsGet = true)]
    public string View { get; set; } = "logon-count";

    [BindProperty(SupportsGet = true)]
    public string? PatternApp { get; set; }

    [BindProperty(SupportsGet = true)]
    public double? MinRuntimeMin { get; set; }

    [BindProperty(SupportsGet = true)]
    public double? MaxRuntimeMin { get; set; }

    public DateTimeOffset FromUtc { get; private set; }
    public DateTimeOffset ToUtc { get; private set; }

    public async Task OnGetAsync(CancellationToken ct)
    {
        ScopeOptions = await stats.GetScopeOptionsAsync(ct);
        ResolveDateRange();

        if (!Enum.TryParse<StatsScopeKind>(ScopeKind, ignoreCase: true, out var kind))
            kind = StatsScopeKind.All;

        if (kind != StatsScopeKind.All && string.IsNullOrWhiteSpace(ScopeValue))
        {
            Snapshot = null;
            return;
        }

        Snapshot = await stats.QueryAsync(
            kind,
            kind == StatsScopeKind.All ? null : ScopeValue,
            FromUtc,
            ToUtc,
            PatternApp,
            MinRuntimeMin,
            MaxRuntimeMin,
            ct);
    }

    private void ResolveDateRange()
    {
        ToUtc = DateTimeOffset.UtcNow;

        // Days > 0 = quick range (default). Days == 0 = use From/To custom dates.
        // Form always posts From/To for display; they must not override a selected quick range.
        if (Days > 0)
        {
            if (Days is not (7 or 30 or 90))
                Days = 7;
            FromUtc = ToUtc.AddDays(-Days);
            From = FromUtc.ToString("yyyy-MM-dd");
            To = ToUtc.ToString("yyyy-MM-dd");
            return;
        }

        if (!string.IsNullOrWhiteSpace(From) && DateTimeOffset.TryParse(From, out var fromParsed)
            && !string.IsNullOrWhiteSpace(To) && DateTimeOffset.TryParse(To, out var toParsed))
        {
            FromUtc = fromParsed.ToUniversalTime().Date;
            ToUtc = toParsed.ToUniversalTime().Date.AddDays(1).AddTicks(-1);
            if (ToUtc < FromUtc)
                (FromUtc, ToUtc) = (ToUtc, FromUtc);
            return;
        }

        Days = 7;
        FromUtc = ToUtc.AddDays(-Days);
        From = FromUtc.ToString("yyyy-MM-dd");
        To = ToUtc.ToString("yyyy-MM-dd");
    }

    public static string FormatBytes(long? bytes)
    {
        if (bytes is null) return "—";
        if (bytes < 1024) return $"{bytes} B";
        if (bytes < 1024 * 1024) return $"{bytes / 1024.0:0.#} KB";
        if (bytes < 1024L * 1024 * 1024) return $"{bytes / (1024.0 * 1024):0.#} MB";
        return $"{bytes / (1024.0 * 1024 * 1024):0.##} GB";
    }

    public static string FormatPct(double? pct) => pct is null ? "—" : $"{pct:0.#}%";
}
