using Heimdall.Api.Data;
using Heimdall.Api.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Heimdall.Api.Pages;

public class UsageModel(
    HeimdallDbContext db,
    StaffAccessGuard guard,
    IOptions<UsageAnalyticsOptions> options) : PageModel
{
    public const int PageSize = 100;

    public bool Allowed { get; private set; }
    public bool AnalyticsEnabled { get; private set; }
    public int RetentionDays { get; private set; }

    [BindProperty(SupportsGet = true)]
    public string? From { get; set; }

    [BindProperty(SupportsGet = true)]
    public string? To { get; set; }

    [BindProperty(SupportsGet = true)]
    public string? UserFilter { get; set; }

    [BindProperty(SupportsGet = true)]
    public string? PathFilter { get; set; }

    [BindProperty(SupportsGet = true)]
    public string? EventType { get; set; }

    [BindProperty(SupportsGet = true)]
    public int PageNumber { get; set; } = 1;

    public DateTimeOffset FromUtc { get; private set; }
    public DateTimeOffset ToUtc { get; private set; }
    public int TotalCount { get; private set; }
    public int TotalPages { get; private set; }
    public IReadOnlyList<SiteUsageEvent> Events { get; private set; } = [];
    public IReadOnlyList<NameCount> TopPages { get; private set; } = [];
    public IReadOnlyList<NameCount> TopUsers { get; private set; } = [];
    public int PageViewCount7d { get; private set; }
    public int ClickCount7d { get; private set; }

    public record NameCount(string Name, int Count);

    public async Task<IActionResult> OnGetAsync(CancellationToken ct)
    {
        if (!EnsureAdmin())
            return Page();

        AnalyticsEnabled = options.Value.Enabled;
        RetentionDays = Math.Max(1, options.Value.RetentionDays);
        ResolveRange();

        if (PageNumber < 1)
            PageNumber = 1;

        var q = db.SiteUsageEvents.AsNoTracking().AsQueryable();
        q = q.Where(e => e.OccurredUtc >= FromUtc && e.OccurredUtc <= ToUtc);

        if (!string.IsNullOrWhiteSpace(UserFilter))
        {
            var u = UserFilter.Trim();
            q = q.Where(e => e.UserName != null && e.UserName.Contains(u));
        }

        if (!string.IsNullOrWhiteSpace(PathFilter))
        {
            var p = PathFilter.Trim();
            q = q.Where(e => e.Path.Contains(p));
        }

        if (!string.IsNullOrWhiteSpace(EventType)
            && !string.Equals(EventType, "all", StringComparison.OrdinalIgnoreCase))
        {
            var t = EventType.Trim().ToLowerInvariant();
            q = q.Where(e => e.EventType == t);
        }

        TotalCount = await q.CountAsync(ct);
        TotalPages = Math.Max(1, (int)Math.Ceiling(TotalCount / (double)PageSize));
        if (PageNumber > TotalPages)
            PageNumber = TotalPages;

        Events = await q
            .OrderByDescending(e => e.OccurredUtc)
            .ThenByDescending(e => e.Id)
            .Skip((PageNumber - 1) * PageSize)
            .Take(PageSize)
            .ToListAsync(ct);

        var weekAgo = DateTimeOffset.UtcNow.AddDays(-7);
        var week = db.SiteUsageEvents.AsNoTracking().Where(e => e.OccurredUtc >= weekAgo);

        PageViewCount7d = await week.CountAsync(e => e.EventType == "pageview", ct);
        ClickCount7d = await week.CountAsync(e => e.EventType == "click", ct);

        TopPages = await week
            .Where(e => e.EventType == "pageview")
            .GroupBy(e => e.Path)
            .Select(g => new NameCount(g.Key, g.Count()))
            .OrderByDescending(x => x.Count)
            .Take(10)
            .ToListAsync(ct);

        TopUsers = await week
            .Where(e => e.EventType == "pageview" && e.UserName != null)
            .GroupBy(e => e.UserName!)
            .Select(g => new NameCount(g.Key, g.Count()))
            .OrderByDescending(x => x.Count)
            .Take(10)
            .ToListAsync(ct);

        return Page();
    }

    public async Task<IActionResult> OnGetExportAsync(CancellationToken ct)
    {
        if (!EnsureAdmin())
            return Page();

        ResolveRange();
        var q = db.SiteUsageEvents.AsNoTracking()
            .Where(e => e.OccurredUtc >= FromUtc && e.OccurredUtc <= ToUtc);

        if (!string.IsNullOrWhiteSpace(UserFilter))
        {
            var u = UserFilter.Trim();
            q = q.Where(e => e.UserName != null && e.UserName.Contains(u));
        }

        if (!string.IsNullOrWhiteSpace(PathFilter))
        {
            var p = PathFilter.Trim();
            q = q.Where(e => e.Path.Contains(p));
        }

        if (!string.IsNullOrWhiteSpace(EventType)
            && !string.Equals(EventType, "all", StringComparison.OrdinalIgnoreCase))
        {
            var t = EventType.Trim().ToLowerInvariant();
            q = q.Where(e => e.EventType == t);
        }

        var rows = await q
            .OrderByDescending(e => e.OccurredUtc)
            .Take(10_000)
            .ToListAsync(ct);

        var sb = new System.Text.StringBuilder();
        sb.AppendLine("OccurredUtc,EventType,UserName,Path,Query,DurationSeconds,LinkHref,LinkText,SessionId,PageViewId,IpAddress");
        foreach (var r in rows)
        {
            sb.Append(Csv(r.OccurredUtc.ToString("u"))).Append(',');
            sb.Append(Csv(r.EventType)).Append(',');
            sb.Append(Csv(r.UserName)).Append(',');
            sb.Append(Csv(r.Path)).Append(',');
            sb.Append(Csv(r.Query)).Append(',');
            sb.Append(r.DurationSeconds?.ToString() ?? "").Append(',');
            sb.Append(Csv(r.LinkHref)).Append(',');
            sb.Append(Csv(r.LinkText)).Append(',');
            sb.Append(Csv(r.SessionId)).Append(',');
            sb.Append(Csv(r.PageViewId)).Append(',');
            sb.Append(Csv(r.IpAddress));
            sb.AppendLine();
        }

        var bytes = System.Text.Encoding.UTF8.GetBytes(sb.ToString());
        var fileName = $"heimdall-usage-{DateTime.UtcNow:yyyyMMdd-HHmmss}.csv";
        return File(bytes, "text/csv", fileName);
    }

    private bool EnsureAdmin()
    {
        Allowed = guard.IsConfiguredAdmin(HttpContext);
        if (!Allowed && guard.IsDevBypassActive)
            Allowed = true;
        if (!Allowed)
            TempData["Error"] = "Admin only (Heimdall:StaffAccess:AdminEmails).";
        return Allowed;
    }

    private void ResolveRange()
    {
        var now = DateTimeOffset.UtcNow;
        if (!DateTimeOffset.TryParse(From, out var from))
            from = now.AddDays(-7);
        if (!DateTimeOffset.TryParse(To, out var to))
            to = now;

        // Date-only inputs: treat To as end of day UTC.
        if (To is { Length: <= 10 } && DateOnly.TryParse(To, out var toDate))
            to = new DateTimeOffset(toDate.ToDateTime(TimeOnly.MaxValue), TimeSpan.Zero);
        if (From is { Length: <= 10 } && DateOnly.TryParse(From, out var fromDate))
            from = new DateTimeOffset(fromDate.ToDateTime(TimeOnly.MinValue), TimeSpan.Zero);

        if (from > to)
            (from, to) = (to, from);

        FromUtc = from;
        ToUtc = to;
        From = from.UtcDateTime.ToString("yyyy-MM-dd");
        To = to.UtcDateTime.ToString("yyyy-MM-dd");
    }

    private static string Csv(string? value)
    {
        if (string.IsNullOrEmpty(value))
            return "";
        var needsQuote = value.Contains(',') || value.Contains('"') || value.Contains('\n') || value.Contains('\r');
        if (!needsQuote)
            return value;
        return "\"" + value.Replace("\"", "\"\"") + "\"";
    }
}
