using Heimdall.Api.Data;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;

namespace Heimdall.Api.Pages;

public class AppsModel(HeimdallDbContext db) : PageModel
{
    public IReadOnlyList<AppRow> Apps { get; private set; } = [];
    public string RangeLabel { get; private set; } = "7 day";
    public int RangeDays { get; private set; } = 7;

    [BindProperty(SupportsGet = true)]
    public string Range { get; set; } = "7d";

    public async Task OnGetAsync()
    {
        var (key, label, days) = IndexModel.ResolveRange(Range);
        Range = key;
        RangeLabel = label;
        RangeDays = days;

        var since = DateTimeOffset.UtcNow.AddDays(-days);
        // SQLite EF DateTimeOffset filters are unreliable; filter in memory for POC.
        var runs = (await db.ProcessRuns.AsNoTracking().ToListAsync())
            .Where(r => r.StartedAtUtc >= since
                        || (r.EndedAtUtc ?? r.LastSeenAtUtc) >= since)
            .ToList();

        var known = await db.KnownApps.AsNoTracking().ToListAsync();
        var displayNames = known.ToDictionary(a => a.ProcessName, a => a.DisplayName, StringComparer.OrdinalIgnoreCase);

        Apps = runs
            .GroupBy(r => r.ProcessName, StringComparer.OrdinalIgnoreCase)
            .Select(g =>
            {
                var seconds = g.Sum(r =>
                {
                    var end = r.EndedAtUtc ?? r.LastSeenAtUtc;
                    return Math.Max(0, (end - r.StartedAtUtc).TotalSeconds);
                });
                var processName = g.Key;
                return new AppRow(
                    processName,
                    displayNames.GetValueOrDefault(processName, processName),
                    g.Select(x => x.Username).Distinct(StringComparer.OrdinalIgnoreCase).Count(),
                    g.Count(),
                    TimeSpan.FromSeconds(seconds),
                    g.Max(x => x.LastSeenAtUtc)
                );
            })
            .OrderByDescending(a => a.TotalDuration)
            .ToList();
    }

    public record AppRow(
        string ProcessName,
        string DisplayName,
        int UniqueUsers,
        int RunCount,
        TimeSpan TotalDuration,
        DateTimeOffset LastSeenUtc);
}
