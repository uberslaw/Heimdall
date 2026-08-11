using Heimdall.Api.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace Heimdall.Api.Pages;

public class UserModel(StatsQueryService stats) : PageModel
{
    [BindProperty(SupportsGet = true)]
    public string? Username { get; set; }

    [BindProperty(SupportsGet = true)]
    public string Range { get; set; } = "7d";

    public UserDetailSnapshot? Detail { get; private set; }
    public bool MissingUsername { get; private set; }
    public string RangeLabel { get; private set; } = "7 day";
    public int RangeDays { get; private set; } = 7;

    public async Task OnGetAsync(CancellationToken ct)
    {
        var (key, label, days) = IndexModel.ResolveRange(Range);
        Range = key;
        RangeLabel = label;
        RangeDays = days;

        if (string.IsNullOrWhiteSpace(Username))
        {
            MissingUsername = true;
            return;
        }

        var fromUtc = DateTimeOffset.UtcNow.AddDays(-days);
        var toUtc = DateTimeOffset.UtcNow;
        Detail = await stats.QueryUserDetailAsync(Username.Trim(), fromUtc, toUtc, ct);
    }

    public static string FormatHourBar(double seconds, double maxSeconds)
    {
        if (maxSeconds <= 0 || seconds <= 0) return "0%";
        var pct = Math.Clamp(seconds / maxSeconds * 100.0, 0, 100);
        return pct.ToString("0.#") + "%";
    }

    public static string DisplayOrDash(string? value) =>
        string.IsNullOrWhiteSpace(value) ? "—" : value.Trim();
}
