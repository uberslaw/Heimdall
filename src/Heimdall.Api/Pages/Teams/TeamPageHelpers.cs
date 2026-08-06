using System.Text;
using Heimdall.Api.Data;
using Microsoft.EntityFrameworkCore;

namespace Heimdall.Api.Pages.Teams;

public static class TeamPageHelpers
{
    public static string? NullIfEmpty(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    public static (string Username, string? Domain) SplitUser(string username, string? domain)
    {
        var u = username.Trim();
        if (u.Contains('\\'))
        {
            var parts = u.Split('\\', 2);
            return (parts[1].Trim(), string.IsNullOrWhiteSpace(parts[0]) ? NullIfEmpty(domain) : parts[0].Trim());
        }

        return (u, NullIfEmpty(domain));
    }

    public static IEnumerable<string> MatchKeys(string username, string? domain)
    {
        var (user, dom) = SplitUser(username, domain);
        if (string.IsNullOrWhiteSpace(user)) yield break;
        yield return user;
        if (!string.IsNullOrWhiteSpace(dom))
            yield return $"{dom}\\{user}";
    }

    /// <summary>UI display form — bare username (strips Global\ / DOMAIN\).</summary>
    public static string FormatUser(string username, string? domain) =>
        Heimdall.Shared.UsernameDisplay.Format(username, domain);

    public static string NormalizeKey(string username, string? domain)
    {
        var (user, dom) = SplitUser(username, domain);
        if (string.IsNullOrWhiteSpace(user)) return "";
        return string.IsNullOrWhiteSpace(dom) ? user : $"{dom}\\{user}";
    }

    public static async Task<bool> WouldCreateCycleAsync(HeimdallDbContext db, int teamId, int proposedParentId)
    {
        var current = proposedParentId;
        var guard = 0;
        while (guard++ < 100)
        {
            if (current == teamId) return true;
            var parent = await db.Teams.AsNoTracking()
                .Where(t => t.Id == current)
                .Select(t => t.ParentTeamId)
                .FirstOrDefaultAsync();
            if (parent is null) return false;
            current = parent.Value;
        }

        return true;
    }

    public static async Task<IReadOnlyList<TeamOption>> LoadTeamOptionsAsync(HeimdallDbContext db)
    {
        var teams = await db.Teams.AsNoTracking().OrderBy(t => t.Name).ToListAsync();
        var byParent = teams.ToLookup(t => t.ParentTeamId);
        var list = new List<TeamOption>();
        void Walk(int? parentId, int depth)
        {
            foreach (var t in byParent[parentId].OrderBy(x => x.Name, StringComparer.OrdinalIgnoreCase))
            {
                var prefix = depth == 0 ? "" : new string('\u2014', depth) + " ";
                list.Add(new TeamOption(t.Id, prefix + t.Name));
                Walk(t.Id, depth + 1);
            }
        }

        Walk(null, 0);
        return list;
    }

    public static List<string> ParseCsvLine(string line)
    {
        var result = new List<string>();
        var sb = new StringBuilder();
        var inQuotes = false;
        for (var i = 0; i < line.Length; i++)
        {
            var c = line[i];
            if (inQuotes)
            {
                if (c == '"')
                {
                    if (i + 1 < line.Length && line[i + 1] == '"')
                    {
                        sb.Append('"');
                        i++;
                    }
                    else
                    {
                        inQuotes = false;
                    }
                }
                else
                {
                    sb.Append(c);
                }
            }
            else if (c == '"')
            {
                inQuotes = true;
            }
            else if (c == ',')
            {
                result.Add(sb.ToString());
                sb.Clear();
            }
            else
            {
                sb.Append(c);
            }
        }

        result.Add(sb.ToString());
        return result;
    }

    public sealed record TeamOption(int Id, string Label);
}
