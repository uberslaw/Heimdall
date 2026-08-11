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

        var machineHosts = await db.Machines.AsNoTracking()
            .ToDictionaryAsync(m => m.Id, m => m.Hostname);

        var fromUtc = DateTimeOffset.UtcNow.AddDays(-days);
        var toUtc = DateTimeOffset.UtcNow;
        // SQLite EF DateTimeOffset filters are unreliable; filter in memory for POC.
        // Only columns needed for metrics — exclude unused navigation/payload fields from materialization.
        var runs = (await db.ProcessRuns.AsNoTracking()
                .Select(r => new ProcessRun
                {
                    ProcessName = r.ProcessName,
                    MachineId = r.MachineId,
                    Username = r.Username,
                    StartedAtUtc = r.StartedAtUtc,
                    EndedAtUtc = r.EndedAtUtc,
                    LastSeenAtUtc = r.LastSeenAtUtc,
                    ExternalRunId = r.ExternalRunId
                })
                .ToListAsync())
            .Where(r => r.StartedAtUtc < toUtc
                        && (r.EndedAtUtc ?? r.LastSeenAtUtc) >= fromUtc)
            .ToList();

        var displayNames = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var g in (await db.ProcessCatalogEntries.AsNoTracking()
                     .Where(c => c.DisplayName != null && c.DisplayName != "")
                     .Select(c => new { c.ProcessName, c.DisplayName, c.LastSeenUtc })
                     .ToListAsync())
                     .GroupBy(c => c.ProcessName, StringComparer.OrdinalIgnoreCase))
        {
            var pick = g.OrderByDescending(x => x.LastSeenUtc).First();
            if (!string.IsNullOrWhiteSpace(pick.DisplayName))
                displayNames[g.Key] = pick.DisplayName!;
        }

        foreach (var e in await db.AppListEntries.AsNoTracking()
                     .Where(e => e.DisplayName != null && e.DisplayName != "")
                     .Select(e => new { e.ProcessName, e.DisplayName })
                     .ToListAsync())
        {
            if (!string.IsNullOrWhiteSpace(e.DisplayName) && !displayNames.ContainsKey(e.ProcessName))
                displayNames[e.ProcessName] = e.DisplayName!;
        }

        Apps = runs
            .GroupBy(r => r.ProcessName, StringComparer.OrdinalIgnoreCase)
            .Select(g =>
            {
                var groupRuns = g.ToList();
                var seconds = ProcessRunMetrics.UnionDurationSeconds(groupRuns, fromUtc, toUtc);
                var processName = g.Key;
                var machines = groupRuns
                    .Select(x => machineHosts.GetValueOrDefault(x.MachineId))
                    .Where(h => !string.IsNullOrWhiteSpace(h))
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .Count();
                return new AppRow(
                    processName,
                    displayNames.GetValueOrDefault(processName, processName),
                    groupRuns.Select(x => x.Username).Distinct(StringComparer.OrdinalIgnoreCase).Count(),
                    machines,
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
        int UniqueMachines,
        double AvgConcurrentProcesses,
        int RunCount,
        TimeSpan TotalDuration,
        DateTimeOffset LastSeenUtc);
}
