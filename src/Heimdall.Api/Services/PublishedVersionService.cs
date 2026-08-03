using System.Data;
using Heimdall.Api.Data;
using Microsoft.EntityFrameworkCore;

namespace Heimdall.Api.Services;

/// <summary>
/// Tracks the "current published" client pack version so the Clients admin page can flag hosts running
/// an older or unknown agent build. Backed by the existing SystemFlags key/value table (see
/// IngestService.EnsureSchemaPatchesAsync) — additive, no new table needed.
/// Set either by Launch Control (best-effort HTTP call after "Create client pack" — see
/// Publish-ClientVersionToApi in Heimdall-LaunchControl.ps1) or manually from the Clients page.
/// </summary>
public class PublishedVersionService(HeimdallDbContext db)
{
    private const string VersionFlagKey = "PublishedClientVersion";
    private const string SetAtFlagKey = "PublishedClientVersionSetUtc";
    private const string SetByFlagKey = "PublishedClientVersionSetBy";

    public async Task<PublishedVersionInfo> GetAsync(CancellationToken ct = default)
    {
        var version = await ReadFlagAsync(VersionFlagKey, ct);
        var setAtRaw = await ReadFlagAsync(SetAtFlagKey, ct);
        var setBy = await ReadFlagAsync(SetByFlagKey, ct);

        DateTimeOffset? setAtUtc = DateTimeOffset.TryParse(setAtRaw, out var parsed) ? parsed : null;
        return new PublishedVersionInfo(
            string.IsNullOrWhiteSpace(version) ? null : version,
            setAtUtc,
            string.IsNullOrWhiteSpace(setBy) ? null : setBy);
    }

    public async Task SetAsync(string version, string? setBy, CancellationToken ct = default)
    {
        var trimmed = version.Trim();
        await db.Database.ExecuteSqlRawAsync(
            "INSERT OR REPLACE INTO SystemFlags (Key, Value) VALUES ({0}, {1});",
            [VersionFlagKey, trimmed], ct);
        await db.Database.ExecuteSqlRawAsync(
            "INSERT OR REPLACE INTO SystemFlags (Key, Value) VALUES ({0}, {1});",
            [SetAtFlagKey, DateTimeOffset.UtcNow.ToString("o")], ct);
        await db.Database.ExecuteSqlRawAsync(
            "INSERT OR REPLACE INTO SystemFlags (Key, Value) VALUES ({0}, {1});",
            [SetByFlagKey, setBy ?? ""], ct);
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

public sealed record PublishedVersionInfo(string? Version, DateTimeOffset? SetUtc, string? SetBy);
