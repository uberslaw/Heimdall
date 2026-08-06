using Heimdall.Api.Data;
using Microsoft.EntityFrameworkCore;

namespace Heimdall.Api.Services;

/// <summary>Syncs Entra group members into <see cref="PersonTeam"/> for a Heimdall team.</summary>
public sealed class EntraTeamMembershipSyncService(
    HeimdallDbContext db,
    EntraGraphService graph,
    ILogger<EntraTeamMembershipSyncService> log)
{
    public async Task<EntraSyncResult> SyncTeamAsync(int teamId, CancellationToken ct)
    {
        if (!graph.IsConfigured)
            return EntraSyncResult.Fail(graph.SetupHint);

        var team = await db.Teams.FirstOrDefaultAsync(t => t.Id == teamId, ct);
        if (team is null)
            return EntraSyncResult.Fail("Team not found.");

        var groupId = EntraGraphService.NormalizeGuid(team.EntraGroupId);
        if (groupId is null)
            return EntraSyncResult.Fail("Link an Entra group Object ID on Edit team before syncing.");

        try
        {
            var group = await graph.GetGroupAsync(groupId, ct);
            if (group is null)
            {
                team.EntraLastSyncError = "Entra group not found (check Object ID and app permissions).";
                await db.SaveChangesAsync(ct);
                return EntraSyncResult.Fail(team.EntraLastSyncError);
            }

            team.EntraGroupId = group.Id;
            team.EntraGroupName = group.DisplayName;

            var members = await graph.ListGroupUserMembersAsync(group.Id, ct);
            var existing = await db.PersonTeams.Where(p => p.TeamId == teamId).ToListAsync(ct);
            var byKey = existing.ToDictionary(
                p => PersonKey(p.Username, p.Domain),
                StringComparer.OrdinalIgnoreCase);

            var added = 0;
            var updated = 0;
            var keepKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (var m in members)
            {
                var key = PersonKey(m.Username, m.Domain);
                if (key.Length == 0) continue;
                keepKeys.Add(key);

                if (byKey.TryGetValue(key, out var row))
                {
                    var changed = false;
                    if (!string.Equals(row.DisplayName, m.DisplayName, StringComparison.Ordinal))
                    {
                        row.DisplayName = m.DisplayName;
                        changed = true;
                    }
                    if (!string.Equals(row.Email, m.Email, StringComparison.OrdinalIgnoreCase))
                    {
                        row.Email = m.Email;
                        changed = true;
                    }
                    if (!string.Equals(row.Domain, m.Domain, StringComparison.OrdinalIgnoreCase))
                    {
                        row.Domain = m.Domain;
                        changed = true;
                    }
                    if (changed) updated++;
                }
                else
                {
                    db.PersonTeams.Add(new PersonTeam
                    {
                        TeamId = teamId,
                        Username = m.Username,
                        Domain = m.Domain,
                        DisplayName = m.DisplayName,
                        Email = m.Email
                    });
                    added++;
                }
            }

            var removed = 0;
            foreach (var row in existing)
            {
                var key = PersonKey(row.Username, row.Domain);
                if (keepKeys.Contains(key)) continue;
                db.PersonTeams.Remove(row);
                removed++;
            }

            team.EntraMembersSyncedUtc = DateTimeOffset.UtcNow;
            team.EntraLastSyncError = null;
            await db.SaveChangesAsync(ct);

            log.LogInformation(
                "Entra sync team {TeamId} group {GroupId}: +{Added} ~{Updated} -{Removed} (graph {GraphCount})",
                teamId, group.Id, added, updated, removed, members.Count);

            return new EntraSyncResult(true, null, group.DisplayName, members.Count, added, updated, removed);
        }
        catch (Exception ex)
        {
            team.EntraLastSyncError = Truncate(ex.Message, 500);
            await db.SaveChangesAsync(ct);
            log.LogWarning(ex, "Entra sync failed for team {TeamId}", teamId);
            return EntraSyncResult.Fail(ex.Message);
        }
    }

    /// <summary>Resolve group display name for a GUID without syncing members.</summary>
    public async Task<(string? GroupId, string? DisplayName, string? Error)> ResolveGroupAsync(
        string? groupIdRaw, CancellationToken ct)
    {
        if (!graph.IsConfigured)
            return (null, null, graph.SetupHint);

        var groupId = EntraGraphService.NormalizeGuid(groupIdRaw);
        if (groupId is null)
            return (null, null, "Entra group id must be a GUID (Object ID).");

        try
        {
            var group = await graph.GetGroupAsync(groupId, ct);
            if (group is null)
                return (groupId, null, "Group not found.");
            return (group.Id, group.DisplayName, null);
        }
        catch (Exception ex)
        {
            return (groupId, null, ex.Message);
        }
    }

    private static string Truncate(string value, int max) =>
        value.Length <= max ? value : value[..max] + "…";

    /// <summary>Same shape as Teams session matching: DOMAIN\user or bare username.</summary>
    private static string PersonKey(string username, string? domain)
    {
        var u = username.Trim();
        string? dom = string.IsNullOrWhiteSpace(domain) ? null : domain.Trim();
        if (u.Contains('\\'))
        {
            var parts = u.Split('\\', 2);
            u = parts[1].Trim();
            if (!string.IsNullOrWhiteSpace(parts[0]))
                dom = parts[0].Trim();
        }

        if (u.Length == 0) return "";
        return string.IsNullOrWhiteSpace(dom) ? u : $"{dom}\\{u}";
    }
}

public sealed record EntraSyncResult(
    bool Ok,
    string? Error,
    string? GroupName,
    int MemberCount,
    int Added,
    int Updated,
    int Removed)
{
    public static EntraSyncResult Fail(string error) =>
        new(false, error, null, 0, 0, 0, 0);

    public string Summary =>
        Ok
            ? $"Synced {(GroupName is null ? "group" : $"“{GroupName}”")}: {MemberCount} member(s) — +{Added} added, {Updated} updated, {Removed} removed."
            : Error ?? "Sync failed.";
}
