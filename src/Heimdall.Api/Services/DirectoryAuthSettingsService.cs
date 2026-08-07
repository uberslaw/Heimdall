using System.Data;
using Heimdall.Api.Data;
using Microsoft.EntityFrameworkCore;

namespace Heimdall.Api.Services;

/// <summary>
/// Admin toggles for team-membership auth sources. Stored in <c>SystemFlags</c>.
/// Manual/CSV stays available as the backup while Entra Graph permissions are pending.
/// </summary>
public sealed class DirectoryAuthSettingsService(HeimdallDbContext db)
{
    public const string ManualCsvFlagKey = "Auth.ManualCsvMembershipEnabled";
    public const string EntraGraphFlagKey = "Auth.EntraGraphMembershipEnabled";

    /// <summary>Defaults: Manual/CSV on, Entra Graph off (enable after Graph admin consent).</summary>
    public async Task<DirectoryAuthSettings> GetAsync(CancellationToken ct = default)
    {
        var manual = await ReadBoolAsync(ManualCsvFlagKey, defaultValue: true, ct);
        var entra = await ReadBoolAsync(EntraGraphFlagKey, defaultValue: false, ct);
        return new DirectoryAuthSettings(manual, entra);
    }

    public async Task SaveAsync(DirectoryAuthSettings settings, CancellationToken ct = default)
    {
        // At least one membership path must stay on.
        var manual = settings.ManualCsvMembershipEnabled;
        var entra = settings.EntraGraphMembershipEnabled;
        if (!manual && !entra)
            manual = true;

        await WriteFlagAsync(ManualCsvFlagKey, manual ? "1" : "0", ct);
        await WriteFlagAsync(EntraGraphFlagKey, entra ? "1" : "0", ct);
    }

    public async Task<bool> IsEntraGraphMembershipEnabledAsync(CancellationToken ct = default) =>
        (await GetAsync(ct)).EntraGraphMembershipEnabled;

    public async Task<bool> IsManualCsvMembershipEnabledAsync(CancellationToken ct = default) =>
        (await GetAsync(ct)).ManualCsvMembershipEnabled;

    private async Task<bool> ReadBoolAsync(string key, bool defaultValue, CancellationToken ct)
    {
        var raw = await ReadFlagAsync(key, ct);
        if (string.IsNullOrWhiteSpace(raw))
            return defaultValue;
        return raw is "1" or "true" or "True" or "yes" or "YES";
    }

    private async Task WriteFlagAsync(string key, string value, CancellationToken ct)
    {
        await db.Database.ExecuteSqlRawAsync(
            "INSERT OR REPLACE INTO SystemFlags (Key, Value) VALUES ({0}, {1});",
            [key, value], ct);
    }

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

public sealed record DirectoryAuthSettings(
    bool ManualCsvMembershipEnabled,
    bool EntraGraphMembershipEnabled);
