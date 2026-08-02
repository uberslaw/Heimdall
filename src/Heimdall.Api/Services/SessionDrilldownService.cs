using Heimdall.Api.Data;
using Heimdall.Shared.Contracts;
using Microsoft.EntityFrameworkCore;

namespace Heimdall.Api.Services;

/// <summary>
/// Backs the Sessions page "Open" drill-down: who currently has a machine open right now, how long
/// they've been disconnected (cumulative disconnected seconds for the open session — the closest thing
/// the current session model tracks), and live resource metrics via the same ref-counted sampling as
/// Staff Access (see LiveSamplingService), keyed by hostname so it works for any machine, not just ones
/// assigned to a Remote Access Group.
/// </summary>
public class SessionDrilldownService(HeimdallDbContext db, LiveSamplingService sampling)
{
    public async Task<SessionDrilldownDto?> GetAsync(string hostname, CancellationToken ct)
    {
        var machine = await db.Machines.AsNoTracking()
            .FirstOrDefaultAsync(m => m.Hostname == hostname, ct);
        if (machine is null)
            return null;

        // SQLite can't translate ORDER BY over DateTimeOffset — sort client-side (matches StatsQueryService's approach).
        var openSessions = await db.Sessions.AsNoTracking()
            .Where(s => s.MachineId == machine.Id && s.State != SessionState.Ended)
            .ToListAsync(ct);

        var users = openSessions
            .OrderByDescending(s => s.LastObservedUtc)
            .Select(ToUserDto)
            .ToList();

        var metric = (await sampling.GetLatestMetricsAsync([hostname], ct))
            .GetValueOrDefault(hostname);

        return new SessionDrilldownDto(hostname, users, metric);
    }

    private static SessionDrilldownUserDto ToUserDto(UserSession s) => new(
        Username: s.Username,
        Domain: s.Domain,
        SessionTypeLabel: s.SessionType == SessionType.Rdp ? "Inbound RDP" : "Local",
        SessionTypeBadgeClass: s.SessionType == SessionType.Rdp ? "badge-rdp" : "badge-local",
        StateLabel: s.State == SessionState.Disconnected ? "Disconnected" : "Active",
        StateBadgeClass: s.State == SessionState.Disconnected ? "badge-disc" : "badge-active",
        StartedAtUtc: s.StartedAtUtc,
        DisconnectedSeconds: s.DisconnectedSeconds,
        ClientName: s.ClientName,
        ClientAddress: s.ClientAddress);
}

public sealed record SessionDrilldownUserDto(
    string Username,
    string? Domain,
    string SessionTypeLabel,
    string SessionTypeBadgeClass,
    string StateLabel,
    string StateBadgeClass,
    DateTimeOffset StartedAtUtc,
    long DisconnectedSeconds,
    string? ClientName,
    string? ClientAddress);

public sealed record SessionDrilldownDto(
    string Hostname,
    IReadOnlyList<SessionDrilldownUserDto> Users,
    LiveSamplingService.MachineMetricView? Metric);
