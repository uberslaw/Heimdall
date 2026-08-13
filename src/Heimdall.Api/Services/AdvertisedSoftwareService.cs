using System.Data;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Heimdall.Api.Data;
using Microsoft.EntityFrameworkCore;

namespace Heimdall.Api.Services;

/// <summary>
/// Catalog apps flagged AllowAdvertiseRdp, expanded by SeenHostnamesJson onto public-pool hosts.
/// </summary>
public sealed class AdvertisedSoftwareService(HeimdallDbContext db)
{
    public const string DisplayedTitlesFlagKey = "RemotePool.DisplayedTitles";

    public sealed record AdvertisedApp(string DisplayName, string? CompanyName, string ProcessName);

    public async Task<IReadOnlyDictionary<int, IReadOnlyList<AdvertisedApp>>> ListByMachineAsync(
        IReadOnlyList<(int MachineId, string Hostname)> hosts,
        CancellationToken ct = default)
    {
        var empty = new Dictionary<int, IReadOnlyList<AdvertisedApp>>();
        if (hosts.Count == 0)
            return empty;

        var hostIndex = new Dictionary<string, List<int>>(StringComparer.OrdinalIgnoreCase);
        foreach (var (machineId, hostname) in hosts)
        {
            empty[machineId] = [];
            if (string.IsNullOrWhiteSpace(hostname)) continue;
            var key = hostname.Trim();
            if (!hostIndex.TryGetValue(key, out var ids))
            {
                ids = [];
                hostIndex[key] = ids;
            }
            ids.Add(machineId);
        }

        var advertised = await db.ProcessCatalogEntries.AsNoTracking()
            .Where(e => e.AllowAdvertiseRdp && !e.Ignored)
            .ToListAsync(ct);
        if (advertised.Count == 0)
            return empty;

        var buckets = new Dictionary<int, Dictionary<string, AdvertisedApp>>();
        foreach (var entry in advertised)
        {
            var display = string.IsNullOrWhiteSpace(entry.DisplayName) ? entry.ProcessName : entry.DisplayName!;
            if (string.IsNullOrWhiteSpace(display)) continue;
            var app = new AdvertisedApp(display, NullIfWhiteSpace(entry.CompanyName), entry.ProcessName);
            foreach (var host in ProcessCatalogService.GetSeenHostnames(entry))
            {
                if (!hostIndex.TryGetValue(host, out var machineIds)) continue;
                foreach (var machineId in machineIds)
                {
                    if (!buckets.TryGetValue(machineId, out var byName))
                    {
                        byName = new Dictionary<string, AdvertisedApp>(StringComparer.OrdinalIgnoreCase);
                        buckets[machineId] = byName;
                    }
                    byName.TryAdd(app.DisplayName, app);
                }
            }
        }

        foreach (var (machineId, byName) in buckets)
        {
            empty[machineId] = byName.Values
                .OrderBy(a => a.DisplayName, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }

        return empty;
    }

    /// <summary>
    /// Public display titles. <c>null</c> means no filter (show every advertised display name).
    /// An empty set means show none until an admin ticks titles.
    /// </summary>
    public async Task<HashSet<string>?> GetDisplayedTitlesAsync(CancellationToken ct = default)
    {
        var raw = await ReadFlagAsync(DisplayedTitlesFlagKey, ct);
        if (string.IsNullOrWhiteSpace(raw))
            return null;
        try
        {
            var parsed = JsonSerializer.Deserialize<string[]>(raw);
            if (parsed is null)
                return null;
            return parsed
                .Where(s => !string.IsNullOrWhiteSpace(s))
                .Select(s => s.Trim())
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    public async Task SaveDisplayedTitlesAsync(IEnumerable<string>? titles, CancellationToken ct = default)
    {
        var list = (titles ?? [])
            .Where(s => !string.IsNullOrWhiteSpace(s))
            .Select(s => s.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(s => s, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var json = JsonSerializer.Serialize(list);
        await db.Database.ExecuteSqlRawAsync(
            "INSERT OR REPLACE INTO SystemFlags (Key, Value) VALUES ({0}, {1});",
            [DisplayedTitlesFlagKey, json], ct);
    }

    public Task ClearDisplayedTitlesAsync(CancellationToken ct = default) =>
        db.Database.ExecuteSqlRawAsync(
            "DELETE FROM SystemFlags WHERE Key = {0};",
            [DisplayedTitlesFlagKey], ct);

    public static IReadOnlyList<AdvertisedApp> ProjectDisplayed(
        IReadOnlyList<AdvertisedApp> apps,
        IReadOnlySet<string>? displayedTitles)
    {
        if (displayedTitles is null)
            return apps;
        if (apps.Count == 0 || displayedTitles.Count == 0)
            return [];

        var result = new List<AdvertisedApp>();
        foreach (var title in displayedTitles.OrderBy(t => t, StringComparer.OrdinalIgnoreCase))
        {
            var match = apps.FirstOrDefault(a =>
                string.Equals(a.DisplayName, title, StringComparison.OrdinalIgnoreCase)
                || MatchesQuery(a, title));
            if (match is not null)
                result.Add(match with { DisplayName = title });
        }
        return result;
    }

    public static bool MatchesQuery(AdvertisedApp app, string? query)
    {
        if (string.IsNullOrWhiteSpace(query)) return true;
        var tokens = query.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        return tokens.All(token =>
            FieldMatches(app.DisplayName, token)
            || FieldMatches(app.ProcessName, token)
            || FieldMatches(app.CompanyName, token));
    }

    public static string FormatCell(IReadOnlyList<AdvertisedApp> apps) =>
        string.Join(", ", apps.Select(a => a.DisplayName));

    public static string FormatTooltip(IReadOnlyList<AdvertisedApp> apps) =>
        string.Join("\n", apps.Select(a =>
            string.IsNullOrWhiteSpace(a.CompanyName) ? a.DisplayName : $"{a.DisplayName} — {a.CompanyName}"));

    private static bool FieldMatches(string? field, string token)
    {
        if (string.IsNullOrEmpty(field)) return false;
        if (token.IndexOfAny(['*', '?']) < 0)
            return field.Contains(token, StringComparison.OrdinalIgnoreCase);
        return Regex.IsMatch(field, GlobToUnanchoredPattern(token), RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    }

    private static string GlobToUnanchoredPattern(string token)
    {
        var sb = new StringBuilder(token.Length * 2);
        foreach (var c in token)
        {
            sb.Append(c switch
            {
                '*' => ".*",
                '?' => ".",
                _ => Regex.Escape(c.ToString())
            });
        }
        return sb.ToString();
    }

    private static string? NullIfWhiteSpace(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private async Task<string?> ReadFlagAsync(string key, CancellationToken ct)
    {
        var conn = db.Database.GetDbConnection();
        var openedHere = conn.State != ConnectionState.Open;
        if (openedHere)
            await conn.OpenAsync(ct);
        try
        {
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT Value FROM SystemFlags WHERE Key = $k LIMIT 1;";
            var p = cmd.CreateParameter();
            p.ParameterName = "$k";
            p.Value = key;
            cmd.Parameters.Add(p);
            var result = await cmd.ExecuteScalarAsync(ct);
            return result as string;
        }
        finally
        {
            if (openedHere)
                await conn.CloseAsync();
        }
    }
}
