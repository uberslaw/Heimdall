using System.Diagnostics;
using System.ServiceProcess;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Heimdall.Shared;

namespace Heimdall.Api.Services;

public sealed class DiagnosticBundleResult
{
    public required string BundleDirectory { get; init; }
    public required string ZipPath { get; init; }
    public required string Message { get; init; }
}

/// <summary>
/// In-process diagnostic collector (no repo scripts required on the API host).
/// Writes under C:\Temp\Heimdall.API\Logs\diagnostics-{stamp}\ + sibling zip.
/// </summary>
public sealed class DiagnosticBundleService(
    IConfiguration configuration,
    IWebHostEnvironment environment,
    ClientPackReadinessService packReadiness,
    ILogger<DiagnosticBundleService> logger)
{
    private static readonly JsonSerializerOptions JsonWrite = new() { WriteIndented = true };

    public DiagnosticBundleResult Collect()
    {
        var stamp = DateTime.Now.ToString("yyyyMMdd-HHmmss");
        var root = HeimdallLogPaths.DiagnosticsDumpRoot;
        Directory.CreateDirectory(root);
        var bundleDir = Path.Combine(root, $"diagnostics-{stamp}");
        Directory.CreateDirectory(bundleDir);

        logger.LogInformation("Collecting diagnostics bundle to {Dir}", bundleDir);
        OpsFileLog.Write("CollectDiagnostics", $"bundle={bundleDir}");

        WriteEnvironment(bundleDir);
        WriteServices(bundleDir);
        WriteHealth(bundleDir);
        CopyRecentLogs(bundleDir);
        WriteRedactedAppSettings(bundleDir);
        WriteEventLogExcerpt(bundleDir);
        WriteDatabasePointers(bundleDir);
        WritePackStatus(bundleDir);
        WriteScQuery(bundleDir);

        var zipPath = bundleDir + ".zip";
        if (File.Exists(zipPath))
            File.Delete(zipPath);
        System.IO.Compression.ZipFile.CreateFromDirectory(bundleDir, zipPath, System.IO.Compression.CompressionLevel.Optimal, includeBaseDirectory: true);

        var message = $"Diagnostics written to {bundleDir} and {zipPath}";
        logger.LogInformation("{Message}", message);
        return new DiagnosticBundleResult
        {
            BundleDirectory = bundleDir,
            ZipPath = zipPath,
            Message = message
        };
    }

    private void WriteEnvironment(string bundleDir)
    {
        var lines = new List<string>
        {
            $"CollectedAt: {DateTimeOffset.UtcNow:o}",
            $"Hostname: {Environment.MachineName}",
            $"User: {Environment.UserName}",
            $"OS: {Environment.OSVersion}",
            $"ContentRoot: {environment.ContentRootPath}",
            $"BaseDirectory: {AppContext.BaseDirectory}",
            $"EnvironmentName: {environment.EnvironmentName}",
            $"ApiLogsDir: {HeimdallLogPaths.ApiLogsDir}",
            $"OpsLogsDir: {HeimdallLogPaths.OpsLogsDir}",
            $"LogsRoot: {HeimdallLogPaths.LogsRoot}",
            $"DiagnosticsDumpRoot: {HeimdallLogPaths.DiagnosticsDumpRoot}"
        };
        try
        {
            var ver = typeof(DiagnosticBundleService).Assembly.GetName().Version?.ToString();
            lines.Add($"ApiAssemblyVersion: {ver}");
        }
        catch { /* ignore */ }

        File.WriteAllLines(Path.Combine(bundleDir, "environment.txt"), lines);
    }

    private static void WriteServices(string bundleDir)
    {
        var sb = new StringBuilder();
        foreach (var name in new[] { "HeimdallApi", "HeimdallAgent" })
        {
            try
            {
#pragma warning disable CA1416 // Windows-only ServiceController — API is Windows-hosted
                using var sc = new ServiceController(name);
                sb.AppendLine($"{name}: Status={sc.Status} StartType={sc.StartType} DisplayName={sc.DisplayName}");
#pragma warning restore CA1416
            }
            catch (Exception ex)
            {
                sb.AppendLine($"{name}: NOT AVAILABLE ({ex.Message})");
            }
        }

        File.WriteAllText(Path.Combine(bundleDir, "services.txt"), sb.ToString());
    }

    private void WriteHealth(string bundleDir)
    {
        var urls = configuration["Urls"]
                   ?? configuration["ASPNETCORE_URLS"]
                   ?? "http://127.0.0.1:5080";
        var first = urls.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .FirstOrDefault() ?? "http://127.0.0.1:5080";
        // Prefer loopback for local collect
        if (first.Contains("0.0.0.0", StringComparison.Ordinal) || first.Contains("+", StringComparison.Ordinal))
            first = Regex.Replace(first, @"0\.0\.0\.0|\+", "127.0.0.1");

        var healthUrl = first.TrimEnd('/') + "/api/health";
        var outPath = Path.Combine(bundleDir, "health.json");
        try
        {
            using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
            var json = client.GetStringAsync(healthUrl).GetAwaiter().GetResult();
            File.WriteAllText(outPath, json);
            File.WriteAllText(Path.Combine(bundleDir, "health.txt"), $"URL: {healthUrl}\r\nOK\r\n{json}");
        }
        catch (Exception ex)
        {
            File.WriteAllText(Path.Combine(bundleDir, "health.txt"), $"URL: {healthUrl}\r\nERROR: {ex.Message}");
            File.WriteAllText(outPath, JsonSerializer.Serialize(new { error = ex.Message, url = healthUrl }, JsonWrite));
        }
    }

    private static void CopyRecentLogs(string bundleDir)
    {
        var destRoot = Path.Combine(bundleDir, "logs");
        Directory.CreateDirectory(destRoot);
        var src = HeimdallLogPaths.LogsRoot;
        if (!Directory.Exists(src))
        {
            File.WriteAllText(Path.Combine(destRoot, "README.txt"), $"(no folder: {src})");
            return;
        }

        // Copy newest files from logs root + api/ + ops/ (cap count and size).
        const int maxFiles = 40;
        const long maxBytesPerFile = 2 * 1024 * 1024;
        var files = new List<FileInfo>();
        void AddFiles(string dir, string pattern)
        {
            if (!Directory.Exists(dir))
                return;
            files.AddRange(new DirectoryInfo(dir).EnumerateFiles(pattern, SearchOption.TopDirectoryOnly));
        }

        AddFiles(src, "*.log");
        AddFiles(HeimdallLogPaths.ApiLogsDir, "*.log");
        AddFiles(HeimdallLogPaths.OpsLogsDir, "*.log");

        foreach (var fi in files.OrderByDescending(f => f.LastWriteTimeUtc).Take(maxFiles))
        {
            try
            {
                var rel = Path.GetRelativePath(src, fi.FullName);
                if (rel.StartsWith("..", StringComparison.Ordinal))
                    rel = fi.Name;
                var dest = Path.Combine(destRoot, rel.Replace(':', '_'));
                var destParent = Path.GetDirectoryName(dest);
                if (!string.IsNullOrEmpty(destParent))
                    Directory.CreateDirectory(destParent);

                if (fi.Length <= maxBytesPerFile)
                {
                    File.Copy(fi.FullName, dest, overwrite: true);
                }
                else
                {
                    // Tail large files
                    using var fs = fi.Open(FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                    fs.Seek(-maxBytesPerFile, SeekOrigin.End);
                    using var outFs = File.Create(dest);
                    fs.CopyTo(outFs);
                }
            }
            catch (Exception ex)
            {
                File.AppendAllText(
                    Path.Combine(destRoot, "copy-errors.txt"),
                    $"{fi.FullName}: {ex.Message}{Environment.NewLine}");
            }
        }
    }

    private static void WriteRedactedAppSettings(string bundleDir)
    {
        var cfgDir = Path.Combine(bundleDir, "appsettings-redacted");
        Directory.CreateDirectory(cfgDir);
        RedactAppSettings(
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Heimdall", "Api", "appsettings.json"),
            Path.Combine(cfgDir, "api-appsettings.json"));
        RedactAppSettings(
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Heimdall", "Agent", "appsettings.json"),
            Path.Combine(cfgDir, "agent-appsettings.json"));
        // Also capture content-root / base-dir copies used in dev
        var baseSettings = Path.Combine(AppContext.BaseDirectory, "appsettings.json");
        if (File.Exists(baseSettings))
            RedactAppSettings(baseSettings, Path.Combine(cfgDir, "api-appsettings-basedir.json"));
    }

    private static void RedactAppSettings(string path, string dest)
    {
        if (!File.Exists(path))
        {
            File.WriteAllText(dest, $"(file not found: {path})");
            return;
        }

        try
        {
            var raw = File.ReadAllText(path);
            using var doc = JsonDocument.Parse(raw);
            using var stream = new MemoryStream();
            using (var writer = new Utf8JsonWriter(stream, new JsonWriterOptions { Indented = true }))
            {
                WriteRedactedElement(doc.RootElement, writer);
            }

            File.WriteAllBytes(dest, stream.ToArray());
        }
        catch
        {
            var redacted = Regex.Replace(
                File.ReadAllText(path),
                @"(""ApiKey""\s*:\s*"")([^""]*)("")",
                m =>
                {
                    var v = m.Groups[2].Value;
                    var tail = v.Length >= 4 ? v[^4..] : v;
                    return $"{m.Groups[1].Value}****{tail}{m.Groups[3].Value}";
                });
            File.WriteAllText(dest, redacted);
        }
    }

    private static void WriteRedactedElement(JsonElement el, Utf8JsonWriter writer)
    {
        switch (el.ValueKind)
        {
            case JsonValueKind.Object:
                writer.WriteStartObject();
                foreach (var prop in el.EnumerateObject())
                {
                    writer.WritePropertyName(prop.Name);
                    if (prop.Name.Equals("ApiKey", StringComparison.OrdinalIgnoreCase)
                        && prop.Value.ValueKind == JsonValueKind.String)
                    {
                        var v = prop.Value.GetString() ?? "";
                        var tail = v.Length >= 4 ? v[^4..] : v;
                        writer.WriteStringValue("****" + tail);
                    }
                    else
                    {
                        WriteRedactedElement(prop.Value, writer);
                    }
                }

                writer.WriteEndObject();
                break;
            case JsonValueKind.Array:
                writer.WriteStartArray();
                foreach (var item in el.EnumerateArray())
                    WriteRedactedElement(item, writer);
                writer.WriteEndArray();
                break;
            default:
                el.WriteTo(writer);
                break;
        }
    }

    private static void WriteEventLogExcerpt(string bundleDir)
    {
        var path = Path.Combine(bundleDir, "eventlog-recent.txt");
        try
        {
#pragma warning disable CA1416
            var start = DateTime.Now.AddDays(-3);
            var entries = new EventLog("Application").Entries.Cast<EventLogEntry>()
                .Where(e => e.TimeGenerated >= start)
                .Where(e =>
                {
                    var src = e.Source ?? "";
                    var msg = e.Message ?? "";
                    return src.Contains("Heimdall", StringComparison.OrdinalIgnoreCase)
                           || src.Contains(".NET", StringComparison.OrdinalIgnoreCase)
                           || src.Contains("Service Control Manager", StringComparison.OrdinalIgnoreCase)
                           || msg.Contains("Heimdall", StringComparison.OrdinalIgnoreCase);
                })
                .OrderByDescending(e => e.TimeGenerated)
                .Take(80)
                .ToList();
#pragma warning restore CA1416

            if (entries.Count == 0)
            {
                File.WriteAllText(path, "No matching recent Application events.");
                return;
            }

            var sb = new StringBuilder();
            foreach (var e in entries)
            {
                sb.AppendLine($"--- {e.TimeGenerated:o} [{e.EntryType}] {e.Source} ---");
                sb.AppendLine(e.Message);
                sb.AppendLine();
            }

            File.WriteAllText(path, sb.ToString());
        }
        catch (Exception ex)
        {
            File.WriteAllText(path, "Event log query failed: " + ex.Message);
        }
    }

    private void WriteDatabasePointers(string bundleDir)
    {
        var lines = new List<string>
        {
            "NOTE: Full SQLite DB files are not copied into this bundle (size/sensitivity).",
            "Copy manually from the paths below if needed.",
            ""
        };
        foreach (var mode in new[] { HeimdallDatabaseMode.Live, HeimdallDatabaseMode.Sandbox })
        {
            try
            {
                var dbPath = HeimdallDatabaseMode.GetDisplayDatabasePath(configuration, mode);
                lines.Add($"[{mode}] {dbPath}");
                if (File.Exists(dbPath))
                {
                    var fi = new FileInfo(dbPath);
                    lines.Add($"  Exists=true Size={fi.Length} LastWriteUtc={fi.LastWriteTimeUtc:o}");
                }
                else
                {
                    lines.Add("  Exists=false");
                }
            }
            catch (Exception ex)
            {
                lines.Add($"[{mode}] error: {ex.Message}");
            }
        }

        File.WriteAllLines(Path.Combine(bundleDir, "database-pointers.txt"), lines);
    }

    private void WritePackStatus(string bundleDir)
    {
        try
        {
            var status = packReadiness.GetStatus();
            var obj = new
            {
                status = status.Status.ToString(),
                message = status.Message,
                packFolder = status.PackFolder,
                packProductVersion = status.PackProductVersion,
                deployUnlocked = status.DeployUnlocked,
                isPacking = status.IsPacking,
                lastPackLogPath = status.LastPackLogPath,
                lastPackLogTail = status.LastPackLogTail,
                lastPackExitCode = status.LastPackExitCode,
                lastPackMessage = status.LastPackMessage,
                lastPackFinishedUtc = status.LastPackFinishedUtc
            };
            File.WriteAllText(
                Path.Combine(bundleDir, "pack-status.json"),
                JsonSerializer.Serialize(obj, JsonWrite));
        }
        catch (Exception ex)
        {
            File.WriteAllText(Path.Combine(bundleDir, "pack-status.json"),
                JsonSerializer.Serialize(new { error = ex.Message }, JsonWrite));
        }
    }

    private static void WriteScQuery(string bundleDir)
    {
        var sb = new StringBuilder();
        foreach (var name in new[] { "HeimdallApi", "HeimdallAgent" })
        {
            sb.AppendLine($"===== sc.exe query {name} =====");
            sb.AppendLine(RunProcess("sc.exe", $"query {name}"));
            sb.AppendLine($"===== sc.exe qc {name} =====");
            sb.AppendLine(RunProcess("sc.exe", $"qc {name}"));
        }

        File.WriteAllText(Path.Combine(bundleDir, "sc-query.txt"), sb.ToString());
    }

    private static string RunProcess(string fileName, string args)
    {
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = fileName,
                Arguments = args,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            };
            using var p = Process.Start(psi);
            if (p is null)
                return "(failed to start)";
            var stdout = p.StandardOutput.ReadToEnd();
            var stderr = p.StandardError.ReadToEnd();
            p.WaitForExit(15_000);
            return stdout + stderr;
        }
        catch (Exception ex)
        {
            return ex.Message;
        }
    }
}
