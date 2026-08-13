using Heimdall.Api.Data;
using Heimdall.Api.Services;
using Heimdall.Shared;
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

        var (fromUtc, toUtc) = IndexModel.ResolveRangeWindow(Range);
        // SQLite EF DateTimeOffset filters are unreliable; filter in memory for POC.
        // Only columns needed for metrics — exclude unused navigation/payload fields from materialization.
        var runs = (await db.ProcessRuns.AsNoTracking()
                .Select(r => new ProcessRun
                {
                    ProcessName = r.ProcessName,
                    ExecutablePath = r.ExecutablePath,
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

        // Prefer catalog paths when a run has no ExecutablePath so Program Files siblings still roll up.
        var catalogPathByName = (await db.ProcessCatalogEntries.AsNoTracking()
                .Where(c => c.ExecutablePath != null && c.ExecutablePath != "")
                .Select(c => new { c.ProcessName, c.ExecutablePath, c.LastSeenUtc })
                .ToListAsync())
            .GroupBy(c => c.ProcessName, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(
                g => g.Key,
                g => g.OrderByDescending(x => x.LastSeenUtc).First().ExecutablePath!,
                StringComparer.OrdinalIgnoreCase);

        Apps = runs
            .GroupBy(r =>
            {
                var path = !string.IsNullOrWhiteSpace(r.ExecutablePath)
                    ? r.ExecutablePath
                    : catalogPathByName.GetValueOrDefault(r.ProcessName);
                var program = ProgramInstallRoot.TryExtract(path);
                return program?.Key ?? ("proc:" + r.ProcessName.ToLowerInvariant());
            }, StringComparer.OrdinalIgnoreCase)
            .Select(g =>
            {
                var groupRuns = g.ToList();
                var seconds = ProcessRunMetrics.UnionDurationSeconds(groupRuns, fromUtc, toUtc);
                var processNames = groupRuns
                    .Select(x => x.ProcessName)
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .OrderBy(n => n, StringComparer.OrdinalIgnoreCase)
                    .ToList();
                // Detail link: process with the most runs in this program group.
                var primaryProcess = groupRuns
                    .GroupBy(x => x.ProcessName, StringComparer.OrdinalIgnoreCase)
                    .OrderByDescending(x => x.Count())
                    .ThenBy(x => x.Key, StringComparer.OrdinalIgnoreCase)
                    .First().Key;

                var samplePath = groupRuns
                    .Select(x => x.ExecutablePath)
                    .FirstOrDefault(p => !string.IsNullOrWhiteSpace(p))
                    ?? catalogPathByName.GetValueOrDefault(primaryProcess);
                var program = ProgramInstallRoot.TryExtract(samplePath);
                var isProgram = program is not null && processNames.Count >= 1 && g.Key.StartsWith("pf:", StringComparison.Ordinal);
                var displayName = isProgram
                    ? program!.DisplayName
                    : displayNames.GetValueOrDefault(primaryProcess, primaryProcess);

                var machines = groupRuns
                    .Select(x => machineHosts.GetValueOrDefault(x.MachineId))
                    .Where(h => !string.IsNullOrWhiteSpace(h))
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .Count();
                return new AppRow(
                    primaryProcess,
                    displayName,
                    processNames,
                    isProgram,
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
        IReadOnlyList<string> MemberProcessNames,
        bool IsProgramGroup,
        int UniqueUsers,
        int UniqueMachines,
        double AvgConcurrentProcesses,
        int RunCount,
        TimeSpan TotalDuration,
        DateTimeOffset LastSeenUtc);
}
