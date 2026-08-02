using Heimdall.Api.Data;
using Microsoft.EntityFrameworkCore;

namespace Heimdall.Api.Services;

/// <summary>
/// CRUD + membership resolution for Staff Access / Remote Access Groups. Access control is enforced
/// here: a machine is only ever visible to a staff email through an explicit RemoteAccessGroupMachine
/// row on a group that email belongs to (see GetStaffGroupPageAsync / IsEmailInGroupAsync).
/// </summary>
public class RemoteAccessGroupService(HeimdallDbContext db)
{
    public async Task<List<RemoteAccessGroup>> ListGroupsAsync(CancellationToken ct) =>
        await db.RemoteAccessGroups
            .Include(g => g.Staff)
            .Include(g => g.Machines)
            .Include(g => g.FavoriteProcesses)
            .AsNoTracking()
            .OrderBy(g => g.Name)
            .ToListAsync(ct);

    public async Task<RemoteAccessGroup?> GetGroupAsync(int id, CancellationToken ct) =>
        await db.RemoteAccessGroups
            .Include(g => g.Staff)
            .Include(g => g.Machines)
            .Include(g => g.FavoriteProcesses)
            .AsNoTracking()
            .FirstOrDefaultAsync(g => g.Id == id, ct);

    public async Task<RemoteAccessGroup> CreateGroupAsync(string name, CancellationToken ct)
    {
        var now = DateTimeOffset.UtcNow;
        var group = new RemoteAccessGroup { Name = name.Trim(), CreatedUtc = now, UpdatedUtc = now };
        db.RemoteAccessGroups.Add(group);
        await db.SaveChangesAsync(ct);
        return group;
    }

    public async Task<bool> RenameGroupAsync(int id, string name, CancellationToken ct)
    {
        var group = await db.RemoteAccessGroups.FirstOrDefaultAsync(g => g.Id == id, ct);
        if (group is null) return false;
        group.Name = name.Trim();
        group.UpdatedUtc = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(ct);
        return true;
    }

    public async Task<bool> DeleteGroupAsync(int id, CancellationToken ct)
    {
        var group = await db.RemoteAccessGroups.FirstOrDefaultAsync(g => g.Id == id, ct);
        if (group is null) return false;
        db.RemoteAccessGroups.Remove(group);
        await db.SaveChangesAsync(ct);
        return true;
    }

    public async Task<int> AddStaffEmailsAsync(int groupId, IEnumerable<string> emails, CancellationToken ct)
    {
        var group = await db.RemoteAccessGroups.Include(g => g.Staff).FirstOrDefaultAsync(g => g.Id == groupId, ct);
        if (group is null) return 0;

        var added = 0;
        foreach (var raw in emails)
        {
            var email = NormalizeEmail(raw);
            if (email.Length == 0) continue;
            if (group.Staff.Any(s => string.Equals(s.Email, email, StringComparison.OrdinalIgnoreCase)))
                continue;

            group.Staff.Add(new RemoteAccessGroupStaff { GroupId = groupId, Email = email });
            added++;
        }

        if (added > 0)
        {
            group.UpdatedUtc = DateTimeOffset.UtcNow;
            await db.SaveChangesAsync(ct);
        }

        return added;
    }

    public async Task<bool> RemoveStaffAsync(int staffId, CancellationToken ct)
    {
        var staff = await db.RemoteAccessGroupStaff.FirstOrDefaultAsync(s => s.Id == staffId, ct);
        if (staff is null) return false;
        db.RemoteAccessGroupStaff.Remove(staff);
        await db.SaveChangesAsync(ct);
        return true;
    }

    public async Task<int> AddMachinesAsync(int groupId, IEnumerable<string> hostnames, CancellationToken ct)
    {
        var group = await db.RemoteAccessGroups.Include(g => g.Machines).FirstOrDefaultAsync(g => g.Id == groupId, ct);
        if (group is null) return 0;

        var added = 0;
        foreach (var raw in hostnames)
        {
            var hostname = raw?.Trim() ?? "";
            if (hostname.Length == 0) continue;
            if (group.Machines.Any(m => string.Equals(m.Hostname, hostname, StringComparison.OrdinalIgnoreCase)))
                continue;

            group.Machines.Add(new RemoteAccessGroupMachine { GroupId = groupId, Hostname = hostname });
            added++;
        }

        if (added > 0)
        {
            group.UpdatedUtc = DateTimeOffset.UtcNow;
            await db.SaveChangesAsync(ct);
        }

        return added;
    }

    public async Task<bool> RemoveMachineAsync(int groupMachineId, CancellationToken ct)
    {
        var gm = await db.RemoteAccessGroupMachines.FirstOrDefaultAsync(m => m.Id == groupMachineId, ct);
        if (gm is null) return false;
        db.RemoteAccessGroupMachines.Remove(gm);
        await db.SaveChangesAsync(ct);
        return true;
    }

    public async Task<bool> SetFavoritesOnlyAsync(int groupId, bool favoritesOnly, CancellationToken ct)
    {
        var group = await db.RemoteAccessGroups.FirstOrDefaultAsync(g => g.Id == groupId, ct);
        if (group is null) return false;
        group.FavoritesOnly = favoritesOnly;
        group.UpdatedUtc = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(ct);
        return true;
    }

    public async Task<bool> AddFavoriteAsync(int groupId, string processName, CancellationToken ct)
    {
        var name = NormalizeProcessName(processName);
        if (name.Length == 0) return false;

        var exists = await db.RemoteAccessFavoriteProcesses
            .AnyAsync(f => f.GroupId == groupId && f.ProcessName.ToLower() == name.ToLower(), ct);
        if (exists) return true;

        db.RemoteAccessFavoriteProcesses.Add(new RemoteAccessFavoriteProcess { GroupId = groupId, ProcessName = name });
        await db.SaveChangesAsync(ct);
        return true;
    }

    public async Task<bool> RemoveFavoriteAsync(int favoriteId, CancellationToken ct)
    {
        var fav = await db.RemoteAccessFavoriteProcesses.FirstOrDefaultAsync(f => f.Id == favoriteId, ct);
        if (fav is null) return false;
        db.RemoteAccessFavoriteProcesses.Remove(fav);
        await db.SaveChangesAsync(ct);
        return true;
    }

    /// <summary>Groups (case-insensitive) matching a staff email — used at Staff Access sign-in.</summary>
    public async Task<List<RemoteAccessGroup>> FindGroupsForEmailAsync(string email, CancellationToken ct)
    {
        var normalized = NormalizeEmail(email);
        if (normalized.Length == 0) return [];

        var groupIds = await db.RemoteAccessGroupStaff
            .Where(s => s.Email.ToLower() == normalized.ToLower())
            .Select(s => s.GroupId)
            .Distinct()
            .ToListAsync(ct);

        if (groupIds.Count == 0) return [];

        return await db.RemoteAccessGroups
            .Where(g => groupIds.Contains(g.Id))
            .OrderBy(g => g.Name)
            .AsNoTracking()
            .ToListAsync(ct);
    }

    public async Task<bool> IsEmailInGroupAsync(string email, int groupId, CancellationToken ct)
    {
        var normalized = NormalizeEmail(email);
        if (normalized.Length == 0) return false;
        return await db.RemoteAccessGroupStaff
            .AnyAsync(s => s.GroupId == groupId && s.Email.ToLower() == normalized.ToLower(), ct);
    }

    /// <summary>All hostnames assigned across every Remote Access Group (used to find groups a machine belongs to).</summary>
    public async Task<List<string>> GroupHostnamesAsync(int groupId, CancellationToken ct) =>
        await db.RemoteAccessGroupMachines
            .Where(m => m.GroupId == groupId)
            .Select(m => m.Hostname)
            .ToListAsync(ct);

    public static string NormalizeEmail(string value) => (value ?? "").Trim().ToLowerInvariant();

    public static string NormalizeProcessName(string value)
    {
        var s = (value ?? "").Trim();
        if (s.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
            s = s[..^4];
        return s;
    }

    public static IEnumerable<string> SplitMultiValue(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) yield break;
        var parts = raw.Split([',', ';', '\n', '\r', '\t'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        foreach (var p in parts)
            if (p.Length > 0)
                yield return p;
    }
}
