using System.Data;
using Heimdall.Api.Data;
using Microsoft.EntityFrameworkCore;

namespace Heimdall.Api.Services;

/// <summary>
/// Org-level TUFLOW scratch/archive defaults stored in <c>SystemFlags</c>.
/// </summary>
public sealed class TuflowScratchSettingsService(HeimdallDbContext db)
{
    public const string ArchiveTemplateFlagKey = "Tuflow.DefaultArchiveShareTemplate";
    public const string DefaultArchiveShareTemplate = @"\\global\australasia\bne\analysis\{hostname}";

    public async Task<string> GetArchiveShareTemplateAsync(CancellationToken ct = default)
    {
        var raw = await ReadFlagAsync(ArchiveTemplateFlagKey, ct);
        return string.IsNullOrWhiteSpace(raw) ? DefaultArchiveShareTemplate : raw.Trim();
    }

    public async Task SaveArchiveShareTemplateAsync(string template, CancellationToken ct = default)
    {
        var value = string.IsNullOrWhiteSpace(template) ? DefaultArchiveShareTemplate : template.Trim();
        await db.Database.ExecuteSqlRawAsync(
            "INSERT OR REPLACE INTO SystemFlags (Key, Value) VALUES ({0}, {1});",
            [ArchiveTemplateFlagKey, value],
            ct);
    }

    /// <summary>Resolve <c>{hostname}</c> and trim trailing slashes.</summary>
    public static string ResolveArchiveRoot(string? templateOrOverride, string hostname)
    {
        var t = string.IsNullOrWhiteSpace(templateOrOverride)
            ? DefaultArchiveShareTemplate
            : templateOrOverride.Trim();
        var host = string.IsNullOrWhiteSpace(hostname) ? "unknown-host" : hostname.Trim();
        return t.Replace("{hostname}", host, StringComparison.OrdinalIgnoreCase)
            .Replace("{Hostname}", host, StringComparison.OrdinalIgnoreCase)
            .TrimEnd('\\', '/');
    }

    public static string CombineArchiveRunFolder(string archiveRoot, string runFolderName)
    {
        var safe = SanitizeFolderName(runFolderName);
        return $"{archiveRoot.TrimEnd('\\')}\\{safe}";
    }

    public static string SanitizeFolderName(string name)
    {
        var s = string.IsNullOrWhiteSpace(name) ? "run" : name.Trim();
        foreach (var c in Path.GetInvalidFileNameChars())
            s = s.Replace(c, '_');
        return s.Length == 0 ? "run" : s;
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
