using System.Data;
using System.Text.Json;
using Heimdall.Api.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Heimdall.Api.Services;

/// <summary>
/// Resolves named email allowlists: DB override in <c>SystemFlags</c> when saved from Admin,
/// otherwise appsettings seed. Scoped; caches lists for the request so sync guards stay cheap.
/// </summary>
public sealed class AccessAllowlistService(
    HeimdallDbContext db,
    IConfiguration config,
    IOptions<StaffAccessOptions> staffOptions,
    WindowsStaffIdentityService identity)
{
    private static readonly JsonSerializerOptions JsonOpts = new() { PropertyNamingPolicy = null };

    private Dictionary<string, IReadOnlyList<string>>? _cache;
    private HashSet<string>? _dbOverrideIds;

    /// <summary>Effective emails for a catalog id (normalized, distinct). Admin list is always config-only.</summary>
    public IReadOnlyList<string> GetEmails(string id)
    {
        EnsureLoaded();
        return _cache!.TryGetValue(id, out var list) ? list : [];
    }

    /// <summary>True when this list has a SystemFlags override (Admin has saved it at least once).</summary>
    public bool HasDbOverride(string id)
    {
        EnsureLoaded();
        return _dbOverrideIds!.Contains(id);
    }

    /// <summary>Framework helper for future page gates: candidate emails vs named list.</summary>
    public bool IsAllowed(HttpContext ctx, string listId) =>
        ResolveCandidateEmails(ctx).Any(e => IsEmailAllowed(listId, e));

    public bool IsEmailAllowed(string listId, string? email)
    {
        if (string.IsNullOrWhiteSpace(email))
            return false;

        // Full Flood effective membership includes site admins.
        if (string.Equals(listId, AccessAllowlistCatalog.FloodFull, StringComparison.OrdinalIgnoreCase))
            return EmailInList(email, GetFloodFullEffective());

        return EmailInList(email, GetEmails(listId));
    }

    /// <summary>AdminEmails ∪ Full Flood team list (config or DB).</summary>
    public IReadOnlyList<string> GetFloodFullEffective()
    {
        var admins = staffOptions.Value.AdminEmails ?? [];
        var team = GetEmails(AccessAllowlistCatalog.FloodFull);
        return NormalizeList(admins.Concat(team));
    }

    public IReadOnlyList<string> GetFloodLiveOnly() =>
        GetEmails(AccessAllowlistCatalog.FloodLive);

    public async Task SaveEmailsAsync(string id, IEnumerable<string> emails, CancellationToken ct = default)
    {
        var def = AccessAllowlistCatalog.TryGet(id)
            ?? throw new ArgumentException($"Unknown access list '{id}'.", nameof(id));
        if (!def.Editable)
            throw new InvalidOperationException($"Access list '{id}' is not editable from the UI.");

        var normalized = NormalizeList(emails);
        var json = JsonSerializer.Serialize(normalized.ToList(), JsonOpts);
        await db.Database.ExecuteSqlRawAsync(
            "INSERT OR REPLACE INTO SystemFlags (Key, Value) VALUES ({0}, {1});",
            [AccessAllowlistCatalog.FlagKey(id), json],
            ct);

        // Invalidate request cache so subsequent reads see the save.
        _cache = null;
        _dbOverrideIds = null;
    }

    /// <summary>Parse textarea / multi-line input into distinct normalized emails.</summary>
    public static IReadOnlyList<string> ParseEmailLines(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
            return [];

        return NormalizeList(
            raw.Split(['\r', '\n', ',', ';'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
    }

    private void EnsureLoaded()
    {
        if (_cache is not null)
            return;

        var cache = new Dictionary<string, IReadOnlyList<string>>(StringComparer.OrdinalIgnoreCase);
        var overrides = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var def in AccessAllowlistCatalog.All)
        {
            if (def.Id == AccessAllowlistCatalog.Admin)
            {
                cache[def.Id] = NormalizeList(staffOptions.Value.AdminEmails ?? []);
                continue;
            }

            var flag = ReadFlag(AccessAllowlistCatalog.FlagKey(def.Id));
            if (flag is not null)
            {
                overrides.Add(def.Id);
                cache[def.Id] = ParseStoredJson(flag);
            }
            else
            {
                var seed = config.GetSection(def.ConfigPath).Get<string[]>() ?? [];
                cache[def.Id] = NormalizeList(seed);
            }
        }

        _cache = cache;
        _dbOverrideIds = overrides;
    }

    private static IReadOnlyList<string> ParseStoredJson(string raw)
    {
        try
        {
            var arr = JsonSerializer.Deserialize<string[]>(raw, JsonOpts);
            return NormalizeList(arr ?? []);
        }
        catch (JsonException)
        {
            return [];
        }
    }

    private static IReadOnlyList<string> NormalizeList(IEnumerable<string> emails) =>
        emails
            .Where(e => !string.IsNullOrWhiteSpace(e))
            .Select(WindowsStaffIdentityService.NormalizeEmail)
            .Where(e => e.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(e => e, StringComparer.OrdinalIgnoreCase)
            .ToList();

    private static bool EmailInList(string? email, IReadOnlyList<string> allowed)
    {
        if (string.IsNullOrWhiteSpace(email))
            return false;

        var normalized = WindowsStaffIdentityService.NormalizeEmail(email);
        if (normalized.Length == 0)
            return false;

        return allowed.Any(a =>
            string.Equals(
                WindowsStaffIdentityService.NormalizeEmail(a),
                normalized,
                StringComparison.OrdinalIgnoreCase));
    }

    private IEnumerable<string> ResolveCandidateEmails(HttpContext ctx)
    {
        var cookie = StaffAuthService.TryGetEmail(ctx);
        if (cookie is not null)
            yield return cookie;

        if (identity.GetWindowsPrincipalName(ctx) is not null)
        {
            foreach (var c in identity.GetCandidateEmails(ctx))
                yield return c;
        }
    }

    private string? ReadFlag(string key)
    {
        var conn = db.Database.GetDbConnection();
        var openedHere = conn.State != ConnectionState.Open;
        if (openedHere)
            conn.Open();
        try
        {
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT Value FROM SystemFlags WHERE Key = $k LIMIT 1;";
            var p = cmd.CreateParameter();
            p.ParameterName = "$k";
            p.Value = key;
            cmd.Parameters.Add(p);
            var result = cmd.ExecuteScalar();
            return result as string;
        }
        finally
        {
            if (openedHere)
                conn.Close();
        }
    }
}
