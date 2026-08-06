using Heimdall.Api.Data;
using Heimdall.Shared.Contracts;
using Microsoft.EntityFrameworkCore;

namespace Heimdall.Api.Services;

public sealed class MachineBookingService(HeimdallDbContext db)
{
    public static readonly TimeSpan MaxDuration = TimeSpan.FromHours(24);

    public sealed record BookingResult(bool Ok, string Message, MachineBooking? Booking = null, bool ActiveSessionWarning = false);

    public sealed record PoolMachineRow(
        int MachineId,
        string Hostname,
        string DisplayName,
        string? FriendlyName,
        string? TeamName,
        bool IsOnline,
        string? ActiveUser,
        SessionState? ActiveUserState,
        bool HasActiveSession,
        MachineBooking? CurrentBooking,
        bool BookingIsMine);

    /// <summary>
    /// Lean pool query: public-team machines (+ optional RAG hostname intersect), open sessions, active bookings.
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
                TeamName = m.Team!.Name
            })
            .OrderBy(m => m.Hostname)
            .ToListAsync(ct);

        if (machines.Count == 0)
            return [];

        var ids = machines.Select(m => m.Id).ToList();

        var openSessions = await db.Sessions.AsNoTracking()
            .Where(s => ids.Contains(s.MachineId) && s.State != SessionState.Ended)
            .Select(s => new { s.MachineId, s.Username, s.State, s.LastObservedUtc, s.ActiveSeconds })
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

        var bookings = await db.MachineBookings.AsNoTracking()
            .Where(b => ids.Contains(b.MachineId) && b.StartUtc < now.AddDays(1) && b.EndUtc > now)
            .OrderBy(b => b.StartUtc)
            .ToListAsync(ct);

        var bookingByMachine = bookings
            .GroupBy(b => b.MachineId)
            .ToDictionary(
                g => g.Key,
                g => g.OrderBy(b => b.StartUtc).First());

        var emailNorm = string.IsNullOrWhiteSpace(staffEmail)
            ? null
            : WindowsStaffIdentityService.NormalizeEmail(staffEmail);

        return machines.Select(m =>
        {
            sessionByMachine.TryGetValue(m.Id, out var sess);
            bookingByMachine.TryGetValue(m.Id, out var booking);
            var display = string.IsNullOrWhiteSpace(m.FriendlyName) ? m.Hostname : m.FriendlyName.Trim();
            var mine = booking is not null
                && emailNorm is not null
                && string.Equals(
                    WindowsStaffIdentityService.NormalizeEmail(booking.BookedByEmail),
                    emailNorm,
                    StringComparison.OrdinalIgnoreCase);

            return new PoolMachineRow(
                m.Id,
                m.Hostname,
                display,
                m.FriendlyName,
                m.TeamName,
                m.LastSeenUtc >= onlineCutoff,
                sess?.Username,
                sess?.State,
                sess is { State: SessionState.Active },
                booking,
                mine);
        }).ToList();
    }

    public async Task<BookingResult> TryCreateAsync(
        int machineId,
        string bookedByEmail,
        DateTimeOffset startUtc,
        DateTimeOffset endUtc,
        string? notes,
        CancellationToken ct)
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

        var machine = await db.Machines.AsNoTracking()
            .Include(m => m.Team)
            .FirstOrDefaultAsync(m => m.Id == machineId, ct);
        if (machine is null)
            return new BookingResult(false, "Machine not found.");
        if (machine.Team is null || !machine.Team.IsPublicFacing)
            return new BookingResult(false, "That machine is not in the Staff RDP pool (team is not public-facing).");

        var overlap = await db.MachineBookings.AsNoTracking()
            .AnyAsync(b =>
                b.MachineId == machineId
                && b.StartUtc < endUtc
                && b.EndUtc > startUtc, ct);
        if (overlap)
            return new BookingResult(false, "Another booking overlaps that window. Cancel it or pick a different time.");

        // Soft rule: one active booking per user per machine — replace own future/current booking.
        var mine = await db.MachineBookings
            .Where(b =>
                b.MachineId == machineId
                && b.BookedByEmail == email
                && b.EndUtc > DateTimeOffset.UtcNow)
            .ToListAsync(ct);
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
            StartUtc = startUtc,
            EndUtc = endUtc,
            CreatedUtc = DateTimeOffset.UtcNow,
            Notes = string.IsNullOrWhiteSpace(notes) ? null : notes.Trim()
        };
        db.MachineBookings.Add(booking);
        await db.SaveChangesAsync(ct);

        var msg = hasActive
            ? $"Booked {machine.Hostname} until {endUtc.ToLocalTime():g}. Warning: an Active session user is present — Connect is still allowed."
            : $"Booked {machine.Hostname} until {endUtc.ToLocalTime():g}.";

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
