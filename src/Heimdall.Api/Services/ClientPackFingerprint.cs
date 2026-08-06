using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Heimdall.Api.Services;

/// <summary>
/// Builds stable SHA256 fingerprints over Heimdall client source inputs and pack folders.
/// Same algorithm is used at pack time (Write-ClientPackManifest.ps1) and by ClientPackReadinessService.
/// </summary>
public static class ClientPackFingerprint
{
    /// <summary>Relative paths/globs under repo root that affect the client pack.</summary>
    public static IReadOnlyList<string> SourceRoots { get; } =
    [
        "src/Heimdall.Agent",
        "src/Heimdall.Shared",
        "tuflow-automation/TuflowLauncher",
        "Directory.Build.props",
        "scripts/Pack-WorkstationCollector.cmd",
        "scripts/Write-ClientPackManifest.ps1",
        "scripts/Install.cmd",
        "scripts/Install-Client.ps1",
        "scripts/Heimdall-VersionCompare.ps1",
        "scripts/Heimdall-CollectorInstall.ps1",
        "scripts/Install-WorkstationCollector.cmd",
        "scripts/Heimdall-Setup.cmd",
        "scripts/Heimdall-LaunchControl.cmd",
        "scripts/Heimdall-LaunchControl.ps1",
        "scripts/New-HeimdallShortcut.ps1",
        "docs/portable-client/README.md",
        "docs/portable-client/FILES.md",
        "assets/heimdall.ico"
    ];

    private static readonly HashSet<string> SkipDirNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "bin", "obj", ".git", "node_modules"
    };

    public static string ComputeSourceFingerprint(string repoRoot)
    {
        var files = EnumerateSourceFiles(repoRoot).OrderBy(f => f.Rel, StringComparer.OrdinalIgnoreCase).ToList();
        return HashFileList(repoRoot, files);
    }

    public static IReadOnlyList<(string Rel, string Full)> EnumerateSourceFiles(string repoRoot)
    {
        var root = Path.GetFullPath(repoRoot);
        var results = new List<(string Rel, string Full)>();

        foreach (var entry in SourceRoots)
        {
            var full = Path.GetFullPath(Path.Combine(root, entry.Replace('/', Path.DirectorySeparatorChar)));
            if (File.Exists(full))
            {
                results.Add((ToRel(root, full), full));
                continue;
            }

            if (!Directory.Exists(full))
                continue;

            foreach (var file in Directory.EnumerateFiles(full, "*", SearchOption.AllDirectories))
            {
                if (IsSkipped(root, file))
                    continue;
                results.Add((ToRel(root, file), file));
            }
        }

        return results;
    }

    public static Dictionary<string, string> BuildManifestMap(string packFolder)
    {
        var root = Path.GetFullPath(packFolder);
        var map = new SortedDictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (!Directory.Exists(root))
            return new Dictionary<string, string>(map);

        foreach (var file in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
        {
            var rel = ToRel(root, file).Replace('\\', '/');
            if (rel.Equals("MANIFEST.sha256", StringComparison.OrdinalIgnoreCase))
                continue;
            map[rel] = HashFile(file);
        }

        return new Dictionary<string, string>(map);
    }

    public static void WriteManifestFile(string packFolder, IReadOnlyDictionary<string, string> map)
    {
        var path = Path.Combine(packFolder, "MANIFEST.sha256");
        var sb = new StringBuilder();
        foreach (var kv in map.OrderBy(k => k.Key, StringComparer.OrdinalIgnoreCase))
            sb.Append(kv.Value).Append("  ").Append(kv.Key.Replace('\\', '/')).Append('\n');
        File.WriteAllText(path, sb.ToString(), new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
    }

    public static string? TryReadSourceFingerprintFromVersionJson(string packFolder)
    {
        var path = Path.Combine(packFolder, "VERSION.json");
        if (!File.Exists(path))
            return null;
        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(path));
            if (doc.RootElement.TryGetProperty("sourceFingerprint", out var fp))
                return fp.GetString();
            if (doc.RootElement.TryGetProperty("SOURCE_FINGERPRINT", out var fp2))
                return fp2.GetString();
        }
        catch
        {
            /* ignore */
        }

        return null;
    }

    public static string? TryReadProductVersion(string packFolder)
    {
        var path = Path.Combine(packFolder, "VERSION.json");
        if (!File.Exists(path))
            return null;
        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(path));
            if (doc.RootElement.TryGetProperty("productVersion", out var v))
                return v.GetString();
        }
        catch
        {
            /* ignore */
        }

        return null;
    }

    public static string HashFile(string path)
    {
        using var stream = File.OpenRead(path);
        var hash = SHA256.HashData(stream);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    public static string HashBytes(byte[] data) =>
        Convert.ToHexString(SHA256.HashData(data)).ToLowerInvariant();

    private static string HashFileList(string repoRoot, List<(string Rel, string Full)> files)
    {
        using var hasher = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        foreach (var (rel, full) in files)
        {
            var line = Encoding.UTF8.GetBytes(rel.Replace('\\', '/').ToLowerInvariant() + "\n");
            hasher.AppendData(line);
            using var stream = File.OpenRead(full);
            var buf = new byte[81920];
            int read;
            while ((read = stream.Read(buf, 0, buf.Length)) > 0)
                hasher.AppendData(buf.AsSpan(0, read));
        }

        return Convert.ToHexString(hasher.GetHashAndReset()).ToLowerInvariant();
    }

    private static bool IsSkipped(string root, string file)
    {
        var rel = ToRel(root, file);
        var parts = rel.Split(new[] { '/', '\\' }, StringSplitOptions.RemoveEmptyEntries);
        return parts.Any(p => SkipDirNames.Contains(p));
    }

    private static string ToRel(string root, string full)
    {
        var r = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
                + Path.DirectorySeparatorChar;
        var f = Path.GetFullPath(full);
        return f.StartsWith(r, StringComparison.OrdinalIgnoreCase)
            ? f[r.Length..].Replace('\\', '/')
            : f.Replace('\\', '/');
    }
}
