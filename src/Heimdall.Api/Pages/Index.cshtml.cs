using Heimdall.Api.Data;
using Heimdall.Api.Services;
using Heimdall.Shared.Contracts;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;

namespace Heimdall.Api.Pages;

public class IndexModel(HeimdallDbContext db, MachineUtilisationService util) : PageModel
{
    public IReadOnlyList<TeamSection> Sections { get; private set; } = [];
    public IReadOnlyList<MachineHierarchy.CountryNode> LocationTree { get; private set; } = [];
    public int MachineCount { get; private set; }
    public int ActiveCount { get; private set; }
    public int IdleCount { get; private set; }
    public int OffCount { get; private set; }
    public string PeriodLabel { get; private set; } = "7 day";

    [BindProperty(SupportsGet = true)]
    public string Period { get; set; } = "7d";

    [BindProperty(SupportsGet = true)]
    public List<string> SelectedCountries { get; set; } = [];

    [BindProperty(SupportsGet = true)]
    public List<string> SelectedCities { get; set; } = [];

    public async Task<IActionResult> OnGetAsync(CancellationToken ct)
    {
        if (!OpsPartial.IsPartial(Request))
            return OpsPartial.RedirectToOpsTab(Request, "machines");

        Period = MachineUtilisationService.NormalizePeriod(Period);
        PeriodLabel = MachineUtilisationService.PeriodOptions.First(p => p.Key == Period).Label;

        var now = DateTimeOffset.UtcNow;
        var onlineCutoff = now.AddMinutes(-5);

        var machines = await db.Machines.AsNoTracking()
            .Include(m => m.Team)
            .OrderBy(m => m.Hostname)
            .ToListAsync(ct);
        foreach (var m in machines)
            MachineHierarchy.EnsureDefaults(m);

        LocationTree = MachineHierarchy.BuildCountryTree(machines);

        var countryFilter = SelectedCountries.Where(s => !string.IsNullOrWhiteSpace(s)).Select(s => s.Trim()).ToList();
        var cityFilter = SelectedCities.Where(s => !string.IsNullOrWhiteSpace(s)).Select(s => s.Trim()).ToList();
        if (countryFilter.Count > 0 || cityFilter.Count > 0)
        {
            machines = machines
                .Where(m => MachineHierarchy.MatchesLocationFilter(m, countryFilter, cityFilter))
                .ToList();
        }

        var sessions = await db.Sessions.AsNoTracking().ToListAsync(ct);
        var openByMachine = sessions
            .Where(s => s.State != SessionState.Ended)
            .GroupBy(s => s.MachineId)
            .ToDictionary(g => g.Key, g => g.ToList());

        var utilByMachine = await util.ComputeAsync(machines.Select(m => m.Id).ToList(), Period, ct);

        var teams = await db.Teams.AsNoTracking().OrderBy(t => t.Name).ToListAsync(ct);
        var activeLinks = await db.TeamAppListLinks.AsNoTracking()
            .Include(l => l.AppList)
            .ThenInclude(a => a.Entries)
            .Where(l => !l.IsExcluded)
            .ToListAsync(ct);
        var appsByTeam = activeLinks
            .GroupBy(l => l.TeamId)
            .ToDictionary(
                g => g.Key,
                g => g.SelectMany(l => l.AppList.Entries)
                    .Select(e => string.IsNullOrWhiteSpace(e.DisplayName) ? e.ProcessName : e.DisplayName!)
                    .Where(n => !string.IsNullOrWhiteSpace(n))
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .OrderBy(n => n, StringComparer.OrdinalIgnoreCase)
                    .ToList());

        var rows = new List<MachineRow>();
        foreach (var m in machines)
        {
            openByMachine.TryGetValue(m.Id, out var open);
            open ??= [];
            var hasActiveSession = open.Any(s => s.State == SessionState.Active);
            var status = ResolveStatus(m.LastSeenUtc >= onlineCutoff, hasActiveSession);

            var lastUser = sessions.Where(s => s.MachineId == m.Id)
                .OrderByDescending(s => s.State == SessionState.Active)
                .ThenByDescending(s => s.LastObservedUtc)
                .ThenByDescending(s => s.ActiveSeconds)
                .FirstOrDefault();

            utilByMachine.TryGetValue(m.Id, out var u);
            u ??= new MachineUtilisationService.MachineUtilRow(Period, 0, null, 100, null, null, null, null, null, null, false);

            var display = string.IsNullOrWhiteSpace(m.FriendlyName) ? m.Hostname : m.FriendlyName.Trim();
            var tip = BuildTooltip(m);

            rows.Add(new MachineRow(
                m.Id,
                m.Hostname,
                display,
                tip,
                !string.IsNullOrWhiteSpace(m.FriendlyName),
                m.TeamId,
                m.Team?.Name,
                status,
                lastUser?.Username,
                m.LastIp,
                u));
        }

        MachineCount = rows.Count;
        ActiveCount = rows.Count(r => r.Status == MachineStatus.Active);
        IdleCount = rows.Count(r => r.Status == MachineStatus.Idle);
        OffCount = rows.Count(r => r.Status == MachineStatus.Off);

        var sections = new List<TeamSection>();
        foreach (var team in teams.OrderBy(t => t.Name, StringComparer.OrdinalIgnoreCase))
        {
            var teamRows = rows.Where(r => r.TeamId == team.Id)
                .OrderBy(r => r.DisplayName, StringComparer.OrdinalIgnoreCase)
                .ToList();
            if (teamRows.Count == 0)
                continue;
            appsByTeam.TryGetValue(team.Id, out var apps);
            apps ??= [];
            sections.Add(new TeamSection(team.Id, team.Name, FormatAppsHeader(apps), string.Join(" · ", apps), teamRows));
        }

        var unassigned = rows.Where(r => r.TeamId is null)
            .OrderBy(r => r.DisplayName, StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (unassigned.Count > 0)
            sections.Add(new TeamSection(null, "Unassigned", "", "", unassigned));

        Sections = sections;
        return Page();
    }

    /// <summary>Shared range keys used by Apps / Sessions / Machine detail (legacy util windows).</summary>
    public static IReadOnlyList<(string Key, string Label, int Days)> RangeOptions { get; } =
    [
        ("1d", "1 day", 1),
        ("7d", "7 day", 7),
        ("2w", "2 week", 14),
        ("4w", "4 week", 28),
        ("quarter", "Quarter (~90 days)", 90),
        ("6m", "6 month", 182),
        ("year", "Year", 365),
    ];

    public static (string Key, string Label, int Days) ResolveRange(string? range)
    {
        var key = string.IsNullOrWhiteSpace(range) ? "7d" : range.Trim().ToLowerInvariant();
        var match = RangeOptions.FirstOrDefault(o => o.Key == key);
        return match.Key is null ? RangeOptions[1] : match;
    }

    public static string FormatRelativeUtc(DateTimeOffset utc)
    {
        var delta = DateTimeOffset.UtcNow - utc;
        if (delta.TotalSeconds < 0 || delta.TotalMinutes < 1)
            return "just now";
        if (delta.TotalMinutes < 60)
            return $"{(int)delta.TotalMinutes}m ago";
        if (delta.TotalHours < 24)
            return $"{(int)delta.TotalHours}h ago";
        if (delta.TotalDays < 7)
            return $"{(int)delta.TotalDays}d ago";
        return utc.ToLocalTime().ToString("d");
    }

    public async Task<IActionResult> OnGetUtilAsync(int machineId, string? period, CancellationToken ct)
    {
        period = MachineUtilisationService.NormalizePeriod(period);
        var map = await util.ComputeAsync([machineId], period, ct);
        if (!map.TryGetValue(machineId, out var u))
            return new JsonResult(new { error = "not found" }) { StatusCode = 404 };

        return new JsonResult(new
        {
            period = u.PeriodKey,
            periodLabel = MachineUtilisationService.PeriodOptions.First(p => p.Key == u.PeriodKey).Label,
            active = MachineUtilisationService.FormatPct(u.ActivePct),
            passive = MachineUtilisationService.FormatPct(u.PassivePct),
            free = MachineUtilisationService.FormatPct(u.FreePct),
            activeSort = u.ActivePct,
            passiveSort = u.PassivePct ?? -1,
            freeSort = u.FreePct,
            gpu = MachineUtilisationService.FormatHoursCompact(u.GpuHours),
            cpu = MachineUtilisationService.FormatHoursCompact(u.CpuHours),
            gpuSort = u.GpuHours ?? -1,
            cpuSort = u.CpuHours ?? -1,
            dr = MachineUtilisationService.FormatBytesCompact(u.DiskReadBytes),
            dw = MachineUtilisationService.FormatBytesCompact(u.DiskWriteBytes),
            ntx = MachineUtilisationService.FormatBytesCompact(u.NetTxBytes),
            nrx = MachineUtilisationService.FormatBytesCompact(u.NetRxBytes),
            drSort = u.DiskReadBytes ?? -1,
            dwSort = u.DiskWriteBytes ?? -1,
            ntxSort = u.NetTxBytes ?? -1,
            nrxSort = u.NetRxBytes ?? -1
        });
    }

    private static MachineStatus ResolveStatus(bool online, bool hasActiveSession)
    {
        if (!online) return MachineStatus.Off;
        return hasActiveSession ? MachineStatus.Active : MachineStatus.Idle;
    }

    private static string BuildTooltip(Machine m)
    {
        var parts = new List<string> { m.Hostname };
        if (!string.IsNullOrWhiteSpace(m.HardwareSerialNumber))
            parts.Add("Serial " + m.HardwareSerialNumber);
        else if (!string.IsNullOrWhiteSpace(m.AssetSerial))
            parts.Add("Serial " + m.AssetSerial);
        return string.Join(" · ", parts);
    }

    private static string FormatAppsHeader(IReadOnlyList<string> apps)
    {
        if (apps.Count == 0) return "";
        const int max = 5;
        var shown = apps.Take(max).ToList();
        var text = string.Join(" – ", shown);
        if (apps.Count > max)
            text += " …";
        return text;
    }

    public enum MachineStatus { Active, Idle, Off }

    public sealed record MachineRow(
        int MachineId,
        string Hostname,
        string DisplayName,
        string Tooltip,
        bool ShowHostnameUnder,
        int? TeamId,
        string? TeamName,
        MachineStatus Status,
        string? LastUser,
        string? LastIp,
        MachineUtilisationService.MachineUtilRow Util);

    public sealed record TeamSection(
        int? TeamId,
        string TeamName,
        string AppsShort,
        string AppsFull,
        IReadOnlyList<MachineRow> Machines);
}
