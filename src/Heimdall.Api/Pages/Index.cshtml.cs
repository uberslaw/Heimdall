using Heimdall.Api.Data;
using Heimdall.Shared.Contracts;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;

namespace Heimdall.Api.Pages;

public class IndexModel(HeimdallDbContext db) : PageModel
{
    public IReadOnlyList<MachineRow> Machines { get; private set; } = [];
    public int OnlineCount { get; private set; }
    public int InUseCount { get; private set; }
    public double AvgUtilisationPct { get; private set; }
    public string RangeLabel { get; private set; } = "7 day";
    public int RangeDays { get; private set; } = 7;

    /// <summary>Utilisation window query key, e.g. 1d, 7d, 2w, 4w, quarter, 6m, year.</summary>
    [BindProperty(SupportsGet = true)]
    public string Range { get; set; } = "7d";

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

    public async Task OnGetAsync()
    {
        var (key, label, days) = ResolveRange(Range);
        Range = key;
        RangeLabel = label;
        RangeDays = days;

        var since = DateTimeOffset.UtcNow.AddDays(-days);
        var windowSeconds = days * 24 * 3600.0;
        var now = DateTimeOffset.UtcNow;
        var onlineCutoff = now.AddMinutes(-5);

        var machines = await db.Machines.AsNoTracking().OrderBy(m => m.Hostname).ToListAsync();
        // SQLite EF cannot translate nullable DateTimeOffset comparisons; filter in memory for POC.
        var sessions = (await db.Sessions.AsNoTracking().ToListAsync())
            .Where(s => s.StartedAtUtc >= since || s.EndedAtUtc is null || s.EndedAtUtc >= since)
            .ToList();

        var rows = new List<MachineRow>();
        foreach (var m in machines)
        {
            var machineSessions = sessions.Where(s => s.MachineId == m.Id).ToList();
            var occupied = machineSessions.Sum(s =>
            {
                var start = s.StartedAtUtc < since ? since : s.StartedAtUtc;
                var end = s.EndedAtUtc ?? now;
                if (end < since) return 0;
                return Math.Max(0, (end - start).TotalSeconds);
            });

            var util = Math.Clamp(occupied / windowSeconds * 100.0, 0, 100);
            var lastUser = machineSessions.OrderByDescending(s => s.LastObservedUtc).FirstOrDefault();

            rows.Add(new MachineRow(
                m.Hostname,
                m.MachineGroup,
                m.IsInUse,
                m.LastSeenUtc >= onlineCutoff,
                m.LastSeenUtc,
                util,
                lastUser?.Username,
                lastUser?.SessionType,
                machineSessions.Count(s => s.State != SessionState.Ended)
            ));
        }

        Machines = rows;
        OnlineCount = rows.Count(r => r.IsOnline);
        InUseCount = rows.Count(r => r.IsInUse);
        AvgUtilisationPct = rows.Count == 0 ? 0 : rows.Average(r => r.UtilisationPct);
    }

    public static (string Key, string Label, int Days) ResolveRange(string? range)
    {
        var key = string.IsNullOrWhiteSpace(range) ? "7d" : range.Trim().ToLowerInvariant();
        var match = RangeOptions.FirstOrDefault(o => o.Key == key);
        return match.Key is null ? RangeOptions[1] : match;
    }

    public record MachineRow(
        string Hostname,
        string? Group,
        bool IsInUse,
        bool IsOnline,
        DateTimeOffset LastSeenUtc,
        double UtilisationPct,
        string? LastUser,
        SessionType? LastSessionType,
        int OpenSessions);
}
