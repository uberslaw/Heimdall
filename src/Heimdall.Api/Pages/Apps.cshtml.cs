using Heimdall.Api.Data;
using Heimdall.Api.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;

namespace Heimdall.Api.Pages;

public class AppsModel(HeimdallDbContext db) : PageModel
{
    public IReadOnlyList<AppRow> Apps { get; private set; } = [];
    public IReadOnlyList<string> Hostnames { get; private set; } = [];
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

        Hostnames = await db.Machines.AsNoTracking()
            .OrderBy(m => m.Hostname)
            .Select(m => m.Hostname)
            .ToListAsync();

        var fromUtc = DateTimeOffset.UtcNow.AddDays(-days);
        var toUtc = DateTimeOffset.UtcNow;
        // SQLite EF DateTimeOffset filters are unreliable; filter in memory for POC.
        var runs = (await db.ProcessRuns.AsNoTracking().ToListAsync())
            .Where(r => r.StartedAtUtc < toUtc
                        && (r.EndedAtUtc ?? r.LastSeenAtUtc) >= fromUtc)
            .ToList();

        var known = await db.KnownApps.AsNoTracking().ToListAsync();
        var displayNames = known.ToDictionary(a => a.ProcessName, a => a.DisplayName, StringComparer.OrdinalIgnoreCase);

        Apps = runs
            .GroupBy(r => r.ProcessName, StringComparer.OrdinalIgnoreCase)
            .Select(g =>
            {
                var groupRuns = g.ToList();
                var seconds = ProcessRunMetrics.UnionDurationSeconds(groupRuns, fromUtc, toUtc);
                var processName = g.Key;
                return new AppRow(
                    processName,
                    displayNames.GetValueOrDefault(processName, processName),
                    groupRuns.Select(x => x.Username).Distinct(StringComparer.OrdinalIgnoreCase).Count(),
                    ProcessRunMetrics.AvgConcurrentProcesses(groupRuns, fromUtc, toUtc),
                    groupRuns.Count,
                    TimeSpan.FromSeconds(seconds),
                    groupRuns.Max(x => x.LastSeenAtUtc)
                );
            })
            .OrderByDescending(a => a.TotalDuration)
            .ToList();
    }

    public record AppRow(
        string ProcessName,
        string DisplayName,
        int UniqueUsers,
        double AvgConcurrentProcesses,
        int RunCount,
        TimeSpan TotalDuration,
        DateTimeOffset LastSeenUtc);
}
