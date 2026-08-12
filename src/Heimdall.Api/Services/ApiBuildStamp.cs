using System.Globalization;
using System.Reflection;
using System.Text.Json;

namespace Heimdall.Api.Services;

/// <summary>
/// Resolves when this API binary was last built/published for the header stamp.
/// Prefers a stamped VERSION.json (packedAtUtc / builtAtUtc) beside the app; otherwise
/// uses the executing assembly's last-write time (survives publish/redeploy).
/// </summary>
public sealed class ApiBuildStamp
{
    public DateTimeOffset BuiltAtUtc { get; }
    public string DisplayLocal { get; }

    public ApiBuildStamp(IHostEnvironment env)
    {
        BuiltAtUtc = Resolve(env);
        // Match site date style (RemoteMachineService.FormatAgentContact).
        DisplayLocal = BuiltAtUtc.ToLocalTime().ToString("dd/MM/yyyy - HH:mm");
    }

    internal static DateTimeOffset Resolve(IHostEnvironment env)
    {
        foreach (var dir in DistinctDirs(env.ContentRootPath, AppContext.BaseDirectory))
        {
            var versionPath = Path.Combine(dir, "VERSION.json");
            if (TryReadStampFromVersionJson(versionPath, out var fromJson))
                return fromJson;
        }

        var asm = Assembly.GetExecutingAssembly();
        if (!string.IsNullOrEmpty(asm.Location) && File.Exists(asm.Location))
            return ToUtcOffset(File.GetLastWriteTimeUtc(asm.Location));

        foreach (var dir in DistinctDirs(AppContext.BaseDirectory, env.ContentRootPath))
        {
            var dll = Path.Combine(dir, "Heimdall.Api.dll");
            if (File.Exists(dll))
                return ToUtcOffset(File.GetLastWriteTimeUtc(dll));
        }

        return DateTimeOffset.UtcNow;
    }

    private static bool TryReadStampFromVersionJson(string path, out DateTimeOffset stamp)
    {
        stamp = default;
        if (!File.Exists(path))
            return false;

        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(path));
            var root = doc.RootElement;
            foreach (var name in new[] { "builtAtUtc", "packedAtUtc", "publishedAtUtc" })
            {
                if (!root.TryGetProperty(name, out var prop))
                    continue;
                var raw = prop.GetString();
                if (string.IsNullOrWhiteSpace(raw))
                    continue;
                if (TryParseStamp(raw, out stamp))
                    return true;
            }
        }
        catch
        {
            // Corrupt/missing stamp — fall through to assembly write time.
        }

        return false;
    }

    private static bool TryParseStamp(string raw, out DateTimeOffset stamp)
    {
        if (DateTimeOffset.TryParse(
                raw,
                CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
                out stamp))
            return true;

        // Client pack style: "Wed 12/08/2026 19:37:04.72" (local wall clock written as UTC label).
        if (DateTime.TryParse(
                raw,
                CultureInfo.GetCultureInfo("en-AU"),
                DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
                out var dt))
        {
            stamp = new DateTimeOffset(DateTime.SpecifyKind(dt, DateTimeKind.Utc));
            return true;
        }

        stamp = default;
        return false;
    }

    private static DateTimeOffset ToUtcOffset(DateTime utcUnspecified) =>
        new(DateTime.SpecifyKind(utcUnspecified, DateTimeKind.Utc));

    private static IEnumerable<string> DistinctDirs(params string?[] dirs)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var d in dirs)
        {
            if (string.IsNullOrWhiteSpace(d))
                continue;
            var full = Path.GetFullPath(d);
            if (seen.Add(full))
                yield return full;
        }
    }
}
