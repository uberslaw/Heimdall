using Heimdall.Api.Data;
using Heimdall.Shared.Contracts;
using Microsoft.EntityFrameworkCore;

namespace Heimdall.Api.Services;

public sealed class MachineBookingService(HeimdallDbContext db)
{
    public static readonly TimeSpan MaxDuration = TimeSpan.FromHours(24);

    public sealed record BookingResult(bool Ok, string Message, MachineBooking? Booking = null, bool ActiveSessionWarning = false);

    public enum ConnectBlockReason
    {
        None = 0,
        ActiveSession = 1,
        BookedNow = 2
    }

    public sealed record PoolBookingRow(
        int Id,
        string BookedByEmail,
        string? BookedByName,
        DateTimeOffset StartUtc,
        DateTimeOffset EndUtc,
        string? Notes,
        bool IsMine,
        bool CoversNow);

    public sealed record PoolMachineRow(
        int MachineId,
        string Hostname,
        string DisplayName,
        string? FriendlyName,
        string? TeamName,
        string? LastIp,
        bool IsOnline,
        string? SessionUser,
        SessionState? SessionState,
        bool HasActiveSession,
        long DisconnectedSeconds,
        DateTimeOffset? SessionStartedAtUtc,
        DateTimeOffset? SessionLastObservedUtc,
        MachineBooking? CurrentBooking,
        bool BookingIsMine,
        IReadOnlyList<PoolBookingRow> TodayBookings,
        ConnectBlockReason ConnectBlocked,
        string ConnectTarget);

    /// <summary>
    /// Lean pool query: public-team machines (+ optional RAG hostname intersect), open sessions, today's bookings.
    /// No RDP probes.
    /// </summary>
    public async Task<IReadOnlyList<PoolMachineRow>> ListPoolAsync(
        string? staffEmail,
        IReadOnlyCollection<string>? ragHostnameFilter,
        bool adminFullPool,
        CancellationToken ct)
    {
        var now = DateTimeOffset.UtcNow;
        var onlineCutoff = now.AddMinutes(-RemoteMachineService.OnlineWindow.TotalMinutes);
        var localNow = DateTimeOffset.Now;
        var todayStartLocal = new DateTimeOffset(localNow.Year, localNow.Month, localNow.Day, 0, 0, 0, localNow.Offset);
        var todayEndLocal = todayStartLocal.AddDays(1);
        var todayStartUtc = todayStartLocal.ToUniversalTime();
        var todayEndUtc = todayEndLocal.ToUniversalTime();

        var machinesQuery = db.Machines.AsNoTracking()
            .Where(m => m.TeamId != null && m.Team != null && m.Team.IsPublicFacing);

        if (!adminFullPool && ragHostnameFilter is { Count: > 0 })
        {
            var hosts = ragHostnameFilter
                .Where(h => !string.IsNullOrWhiteSpace(h))
                .Select(h => h.Trim())
                .ToList();
            machinesQuery = machinesQuery.Where(m => hosts.Contains(m.Hostname));
        }

        var machines = await machinesQuery
            .Select(m => new
            {
                m.Id,
                m.Hostname,
                m.FriendlyName,
                m.LastSeenUtc,
                m.LastIp,
                TeamName = m.Team!.Name
            })
            .OrderBy(m => m.Hostname)
            .ToListAsync(ct);

        if (machines.Count == 0)
            return [];

        var ids = machines.Select(m => m.Id).ToList();

        var openSessions = await db.Sessions.AsNoTracking()
            .Where(s => ids.Contains(s.MachineId) && s.State != SessionState.Ended)
            .Select(s => new
            {
                s.MachineId,
                s.Username,
                s.State,
                s.LastObservedUtc,
                s.ActiveSeconds,
                s.DisconnectedSeconds,
                s.StartedAtUtc
            })
            .ToListAsync(ct);

        var sessionByMachine = openSessions
            .GroupBy(s => s.MachineId)
            .ToDictionary(
                g => g.Key,
                g => g
                    .OrderByDescending(s => s.State == SessionState.Active)
                    .ThenByDescending(s => s.LastObservedUtc)
                    .ThenByDescending(s => s.ActiveSeconds)
                    .First());

        // SQLite cannot filter/ORDER BY DateTimeOffset reliably — load then filter/sort in memory.
        var allBookings = await db.MachineBookings.AsNoTracking()
            .Where(b => ids.Contains(b.MachineId))
            .ToListAsync(ct);

        var todayBookings = allBookings
            .Where(b => b.StartUtc < todayEndUtc && b.EndUtc > todayStartUtc)
            .OrderBy(b => b.StartUtc)
            .ToList();

        var todayByMachine = todayBookings
            .GroupBy(b => b.MachineId)
            .ToDictionary(g => g.Key, g => g.OrderBy(b => b.StartUtc).ToList());

        var emailNorm = string.IsNullOrWhiteSpace(staffEmail)
            ? null
            : WindowsStaffIdentityService.NormalizeEmail(staffEmail);

        return machines.Select(m =>
        {
            sessionByMachine.TryGetValue(m.Id, out var sess);
            todayByMachine.TryGetValue(m.Id, out var dayList);
            dayList ??= [];

            var coveringNow = dayList.FirstOrDefault(b => b.StartUtc <= now && b.EndUtc > now)
                ?? allBookings.FirstOrDefault(b =>
                    b.MachineId == m.Id && b.StartUtc <= now && b.EndUtc > now);

            var display = string.IsNullOrWhiteSpace(m.FriendlyName) ? m.Hostname : m.FriendlyName.Trim();
            var mine = coveringNow is not null
                && emailNorm is not null
                && string.Equals(
                    WindowsStaffIdentityService.NormalizeEmail(coveringNow.BookedByEmail),
                    emailNorm,
                    StringComparison.OrdinalIgnoreCase);

            var hasActive = sess is { State: SessionState.Active };
            var bookedNow = coveringNow is not null;
            var block = hasActive
                ? ConnectBlockReason.ActiveSession
                : bookedNow
                    ? ConnectBlockReason.BookedNow
                    : ConnectBlockReason.None;

            var connectTarget = !string.IsNullOrWhiteSpace(m.LastIp)
                ? m.LastIp.Trim()
                : m.Hostname;

            var poolBookings = dayList.Select(b =>
            {
                var isMine = emailNorm is not null
                    && string.Equals(
                        WindowsStaffIdentityService.NormalizeEmail(b.BookedByEmail),
                        emailNorm,
                        StringComparison.OrdinalIgnoreCase);
                return new PoolBookingRow(
                    b.Id,
                    b.BookedByEmail,
                    b.BookedByName,
                    b.StartUtc,
                    b.EndUtc,
                    b.Notes,
                    isMine,
                    b.StartUtc <= now && b.EndUtc > now);
            }).ToList();

            return new PoolMachineRow(
                m.Id,
                m.Hostname,
                display,
                m.FriendlyName,
                m.TeamName,
                m.LastIp,
                m.LastSeenUtc >= onlineCutoff,
                sess?.Username,
                sess?.State,
                hasActive,
                sess?.DisconnectedSeconds ?? 0,
                sess?.StartedAtUtc,
                sess?.LastObservedUtc,
                coveringNow,
                mine,
                poolBookings,
                block,
                connectTarget);
        }).ToList();
    }

    /// <summary>
    /// Returns connect target (LastIp or Hostname) only when the machine is in the public-facing pool.
    /// </summary>
    public async Task<(bool Ok, string? Target, string? Error)> TryResolvePublicConnectTargetAsync(
        string? hostnameOrIp,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(hostnameOrIp))
            return (false, null, "Missing host.");

        var key = hostnameOrIp.Trim();
        var machine = await db.Machines.AsNoTracking()
            .Include(m => m.Team)
            .FirstOrDefaultAsync(m =>
                m.Hostname == key
                || (m.LastIp != null && m.LastIp == key), ct);

        if (machine is null)
            return (false, null, "Machine not in catalogue.");
        if (machine.Team is null || !machine.Team.IsPublicFacing)
            return (false, null, "Machine is not in the public remote workstation pool.");

        var target = !string.IsNullOrWhiteSpace(machine.LastIp)
            ? machine.LastIp.Trim()
            : machine.Hostname;
        return (true, target, null);
    }

    public async Task<BookingResult> TryCreateAsync(
        int machineId,
        string bookedByEmail,
        DateTimeOffset startUtc,
        DateTimeOffset endUtc,
        string? notes,
        CancellationToken ct,
        string? bookedByName = null)
    {
        var email = WindowsStaffIdentityService.NormalizeEmail(bookedByEmail);
        if (email.Length == 0 || !email.Contains('@'))
            return new BookingResult(false, "A valid email is required to book.");

        if (endUtc <= startUtc)
            return new BookingResult(false, "End time must be after start time.");

        if (endUtc - startUtc > MaxDuration)
            return new BookingResult(false, "Bookings cannot exceed 24 hours.");

        if (endUtc <= DateTimeOffset.UtcNow)
            return new BookingResult(false, "Booking end must be in the future.");

        // Allow starts slightly in the past (clock skew) but not more than 5 minutes.
        if (startUtc < DateTimeOffset.UtcNow.AddMinutes(-5))
            return new BookingResult(false, "Booking start cannot be in the past.");

        var machine = await db.Machines.AsNoTracking()
            .Include(m => m.Team)
            .FirstOrDefaultAsync(m => m.Id == machineId, ct);
        if (machine is null)
            return new BookingResult(false, "Machine not found.");
        if (machine.Team is null || !machine.Team.IsPublicFacing)
            return new BookingResult(false, "That machine is not in the public remote workstation pool.");

        var machineBookings = await db.MachineBookings.AsNoTracking()
            .Where(b => b.MachineId == machineId)
            .ToListAsync(ct);
        var overlap = machineBookings.Any(b => b.StartUtc < endUtc && b.EndUtc > startUtc);
        if (overlap)
            return new BookingResult(false, "Another booking overlaps that window. Cancel it or pick a different time.");

        // Soft rule: one active booking per user per machine — replace own future/current booking.
        var nowUtc = DateTimeOffset.UtcNow;
        var mine = (await db.MachineBookings
                .Where(b => b.MachineId == machineId && b.BookedByEmail == email)
                .ToListAsync(ct))
            .Where(b => b.EndUtc > nowUtc)
            .ToList();
        if (mine.Count > 0)
            db.MachineBookings.RemoveRange(mine);

        var hasActive = await db.Sessions.AsNoTracking()
            .AnyAsync(s =>
                s.MachineId == machineId
                && s.State == SessionState.Active, ct);

        var booking = new MachineBooking
        {
            MachineId = machineId,
            BookedByEmail = email,
            BookedByName = string.IsNullOrWhiteSpace(bookedByName) ? null : bookedByName.Trim(),
            StartUtc = startUtc,
            EndUtc = endUtc,
            CreatedUtc = DateTimeOffset.UtcNow,
            Notes = string.IsNullOrWhiteSpace(notes) ? null : notes.Trim()
        };
        db.MachineBookings.Add(booking);
        await db.SaveChangesAsync(ct);

        var msg = hasActive
            ? $"Booked {machine.Hostname} {startUtc.ToLocalTime():g}–{endUtc.ToLocalTime():g}. Warning: an Active session user is present."
            : $"Booked {machine.Hostname} {startUtc.ToLocalTime():g}–{endUtc.ToLocalTime():g}.";

        return new BookingResult(true, msg, booking, hasActive);
    }

    public async Task<BookingResult> TryCancelAsync(int bookingId, string requesterEmail, bool isAdmin, CancellationToken ct)
    {
        var email = WindowsStaffIdentityService.NormalizeEmail(requesterEmail);
        var booking = await db.MachineBookings
            .Include(b => b.Machine)
            .FirstOrDefaultAsync(b => b.Id == bookingId, ct);
        if (booking is null)
            return new BookingResult(false, "Booking not found.");

        var owner = WindowsStaffIdentityService.NormalizeEmail(booking.BookedByEmail);
        if (!isAdmin && !string.Equals(owner, email, StringComparison.OrdinalIgnoreCase))
            return new BookingResult(false, "You can only cancel your own bookings.");

        db.MachineBookings.Remove(booking);
        await db.SaveChangesAsync(ct);
        return new BookingResult(true, $"Cancelled booking for {booking.Machine.Hostname}.");
    }
}
