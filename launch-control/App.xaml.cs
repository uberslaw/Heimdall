using System.IO;
using System.Windows;
using LaunchControl.Standard.Host;

namespace Heimdall.LaunchControl;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        var root = FindRoot();
        var scripts = Path.Combine(root, "scripts");
        var setupPs1 = Path.Combine(scripts, "Heimdall-LaunchControl.ps1");
        var logs = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "Heimdall", "logs");
        Directory.CreateDirectory(logs);
        var health = ResolveHealthUrl();
        var displayApi = TryReadAgentApiBaseUrl()?.TrimEnd('/')
                         ?? health?.Replace("/api/health", "", StringComparison.OrdinalIgnoreCase)
                         ?? "http://127.0.0.1:5080";

        // Run Setup actions without opening the full WinForms Setup shell.
        // Progress is written to %ProgramData%\Heimdall\logs\launch-control-live.log (followed below).
        ExtraAction Mode(string title, string mode, string group, bool elevate = true) =>
            new(title, w =>
            {
                if (!File.Exists(setupPs1))
                {
                    w.AppendLog($"Missing {setupPs1}", "ERROR");
                    return;
                }
                w.AppendLog($"Starting {title}…", "STEP");
                w.AppendLog("Progress streams into this console (big steps only). Approve UAC if prompted.", "IMPORTANT");
                w.SetFollowLogs(true);
                ProcessUtil.StartPowerShell(setupPs1, scripts, elevate, "-Mode", mode, "-ActionOnly");
            }, group);

        var liveLog = Path.Combine(logs, "launch-control-live.log");
        try
        {
            if (!File.Exists(liveLog))
                File.WriteAllText(liveLog, $"# Heimdall Launch Control live console{Environment.NewLine}");
        }
        catch { /* ignore */ }

        var logPaths = new List<string> { liveLog };
        if (Directory.Exists(logs))
        {
            logPaths.AddRange(
                Directory.GetFiles(logs, "launch-control-*.log")
                    .OrderByDescending(File.GetLastWriteTimeUtc)
                    .Take(2));
            logPaths.AddRange(
                Directory.GetFiles(logs, "republish-api-deploy.log"));
        }

        LaunchControlApp.Run(this, new LaunchControlProfile
        {
            ProductId = "heimdall",
            ProductName = "Heimdall",
            AppDataFolder = "Heimdall",
            ServiceNames = ["HeimdallApi", "HeimdallAgent"],
            CompactServiceControls = true,
            ShowRefreshButton = false,
            ShowFollowLogsButton = true,
            HealthUrl = health,
            BrowserUrl = health?.Replace("/api/health", "/", StringComparison.OrdinalIgnoreCase) ?? "http://localhost:5080/",
            LogPaths = logPaths.Distinct(StringComparer.OrdinalIgnoreCase).Take(5).ToArray(),
            CrashLogPath = Path.Combine(root, "logs", "launch-control.log"),
            DefaultColors = new Dictionary<string, string>
            {
                ["ChromeColor"] = "#1B365D",
                ["PrimaryActionColor"] = "#2E6DA4",
                // OK / success lines in the console (StatusRunningBrush) — light green on dark console.
                ["StatusRunningColor"] = "#8FDBA8"
            },
            MetaText = () => $"API {displayApi}  Logs %ProgramData%\\Heimdall\\logs",
            StartupNotes =
            [
                "Closing this window does not stop Heimdall services.",
                "Redeploy / Pack progress appears in this console (follow launch-control-live.log).",
                "Server migration: expand that group and run steps 1→7 in order when moving the API host.",
                "Scroll up to read history — console stays put until you scroll back to the bottom.",
                "Expand Diagnostics / Recovery when needed. Log colors: blue = steps, green = OK, amber = warn, red = failures."
            ],
            ExtraActionLayout = new ExtraActionLayout
            {
                CompactGroups = ["Republish", "Setup"],
                CollapsedGroups = ["Diagnostics", "Recovery"]
            },
            ExtraActions =
            [
                // Republish first (preserve-config redeploy + pack)
                Mode("Redeploy API (preserve config)", "RedeployApi", "Republish"),
                Mode("Create client pack", "PackCollector", "Republish"),
                // Setup
                Mode("Install API on this PC", "InstallApi", "Setup"),
                Mode("Install agent on this PC", "InstallCollector", "Setup"),
                Mode("Push client pack to PC(s)…", "PushClientPack", "Setup"),
                Mode("Push pack zip to network share…", "PushPackZip", "Setup"),
                Mode("Deposit pack via API…", "DepositPack", "Setup"),
                // Server migration — run top to bottom when moving the API host
                Mode("1 · Backup database from OLD API host…", "BackupApiDatabase", "Server migration"),
                Mode("2 · Backup config from OLD API host…", "BackupApiConfig", "Server migration"),
                Mode("3 · Install API on NEW host (run LC on that PC)…", "MigrationInstallApi", "Server migration"),
                Mode("4 · Copy DB + secrets onto NEW host…", "MigrationRestoreData", "Server migration"),
                Mode("5 · Verify new API /api/health…", "MigrationVerifyHealth", "Server migration"),
                Mode("6 · Retarget agents (open Client version)…", "MigrationRetargetAgents", "Server migration"),
                Mode("7 · Stop HeimdallApi on OLD host…", "MigrationStopOldApi", "Server migration"),
                // Diagnostics (collapsed) — includes refresh / follow logs
                new("Refresh status", w => { _ = w.RequestStatusRefreshAsync(); }, "Diagnostics"),
                new("Follow logs (toggle)", w => w.ToggleFollowLogs(), "Diagnostics"),
                Mode("Client health check", "ClientCheck", "Diagnostics", elevate: false),
                Mode("Collect diagnostics", "Diagnostics", "Diagnostics"),
                Mode("Check prerequisites", "Prerequisites", "Diagnostics", elevate: false),
                Mode("Open logs folder", "OpenLogs", "Diagnostics", elevate: false),
                Mode("Open remote logs folder…", "OpenRemoteLogs", "Diagnostics", elevate: false),
                new("Open dashboard", w =>
                {
                    var url = health?.Replace("/api/health", "/", StringComparison.OrdinalIgnoreCase) ?? "http://localhost:5080/";
                    ProcessUtil.OpenUrl(url.TrimEnd('/') + "/");
                    w.AppendLog($"Opened {url}", "OK");
                }, "Diagnostics"),
                // Recovery — routine backups + demos
                Mode("Backup API database…", "BackupApiDatabase", "Recovery"),
                Mode("Backup API config (appsettings)…", "BackupApiConfig", "Recovery"),
                Mode("Remove seed/demo machines…", "RemoveSeedDemos", "Recovery"),
            ]
        });
    }

    private static string FindRoot()
    {
        var dir = AppContext.BaseDirectory;
        for (var i = 0; i < 8 && !string.IsNullOrEmpty(dir); i++)
        {
            if (File.Exists(Path.Combine(dir, "scripts", "Heimdall-LaunchControl.ps1")))
                return dir;
            dir = Directory.GetParent(dir)?.FullName ?? "";
        }
        return @"C:\Heimdall";
    }

    private static string? ResolveHealthUrl()
    {
        var configured = TryReadAgentApiBaseUrl();
        if (!string.IsNullOrWhiteSpace(configured))
        {
            // Prefer loopback for the local health probe — hostname can take >2s and false-fail.
            var probeBase = PreferLoopbackIfLocal(configured.TrimEnd('/'));
            return probeBase + "/api/health";
        }

        return "http://127.0.0.1:5080/api/health";
    }

    private static string? TryReadAgentApiBaseUrl()
    {
        var agent = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Heimdall", "Agent", "appsettings.json");
        try
        {
            if (!File.Exists(agent))
                return null;
            var text = File.ReadAllText(agent);
            var idx = text.IndexOf("ApiBaseUrl", StringComparison.OrdinalIgnoreCase);
            if (idx < 0)
                return null;
            var q1 = text.IndexOf('"', idx + 10);
            var q2 = text.IndexOf('"', q1 + 1);
            var q3 = text.IndexOf('"', q2 + 1);
            if (q2 <= 0 || q3 <= q2)
                return null;
            var url = text[(q2 + 1)..q3].Trim().TrimEnd('/');
            return url.StartsWith("http", StringComparison.OrdinalIgnoreCase) ? url : null;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// When ApiBaseUrl points at this machine by hostname, probe via 127.0.0.1 (same port).
    /// Meta/display still shows the configured hostname URL.
    /// </summary>
    private static string PreferLoopbackIfLocal(string apiBaseUrl)
    {
        if (!Uri.TryCreate(apiBaseUrl, UriKind.Absolute, out var uri))
            return apiBaseUrl;

        var host = uri.Host;
        if (string.Equals(host, "127.0.0.1", StringComparison.OrdinalIgnoreCase)
            || string.Equals(host, "localhost", StringComparison.OrdinalIgnoreCase)
            || string.Equals(host, "::1", StringComparison.OrdinalIgnoreCase)
            || string.Equals(host, Environment.MachineName, StringComparison.OrdinalIgnoreCase)
            || string.Equals(host, Environment.MachineName + "." + Environment.UserDomainName, StringComparison.OrdinalIgnoreCase))
        {
            var builder = new UriBuilder(uri) { Host = "127.0.0.1" };
            return builder.Uri.GetLeftPart(UriPartial.Authority).TrimEnd('/');
        }

        return apiBaseUrl;
    }
}
