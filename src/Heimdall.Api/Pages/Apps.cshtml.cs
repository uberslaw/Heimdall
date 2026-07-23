using Heimdall.Api.Data;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;

namespace Heimdall.Api.Pages;

public class AppsModel(HeimdallDbContext db) : PageModel
{
    public IReadOnlyList<AppRow> Apps { get; private set; } = [];

    public async Task OnGetAsync()
    {
        var since = DateTimeOffset.UtcNow.AddDays(-30);
        // SQLite EF DateTimeOffset filters are unreliable; filter in memory for POC.
        var runs = (await db.ProcessRuns.AsNoTracking().ToListAsync())
            .Where(r => r.StartedAtUtc >= since)
            .ToList();

        Apps = runs
            .GroupBy(r => r.ProcessName, StringComparer.OrdinalIgnoreCase)
            .Select(g =>
            {
                var seconds = g.Sum(r =>
                {
                    var end = r.EndedAtUtc ?? r.LastSeenAtUtc;
                    return Math.Max(0, (end - r.StartedAtUtc).TotalSeconds);
                });
                return new AppRow(
                    g.Key,
                    g.Select(x => x.Username).Distinct(StringComparer.OrdinalIgnoreCase).Count(),
                    g.Count(),
                    TimeSpan.FromSeconds(seconds),
                    g.Max(x => x.LastSeenAtUtc)
                );
            })
            .OrderByDescending(a => a.TotalDuration)
            .ToList();
    }

    public record AppRow(string ProcessName, int UniqueUsers, int RunCount, TimeSpan TotalDuration, DateTimeOffset LastSeenUtc);
}
