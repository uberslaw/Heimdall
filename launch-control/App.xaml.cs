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

        // Run Setup actions without opening the full WinForms Setup shell.
        ExtraAction Mode(string title, string mode, string group, bool elevate = true) =>
            new(title, w =>
            {
                if (!File.Exists(setupPs1))
                {
                    w.AppendLog($"Missing {setupPs1}", "ERROR");
                    return;
                }
                w.AppendLog($"Starting {title}…", "STEP");
                w.SetFollowLogs(true);
                ProcessUtil.StartPowerShell(setupPs1, scripts, elevate, "-Mode", mode, "-ActionOnly");
                w.AppendLog($"Launched Setup action {mode} (no full Setup window). Approve UAC if prompted.", "IMPORTANT");
            }, group);

        LaunchControlApp.Run(this, new LaunchControlProfile
        {
            ProductId = "heimdall",
            ProductName = "Heimdall",
            AppDataFolder = "Heimdall",
            ServiceNames = ["HeimdallApi", "HeimdallAgent"],
            CompactServiceControls = true,
            ShowRefreshButton = false,
            ShowFollowLogsButton = false,
            HealthUrl = health,
            BrowserUrl = health?.Replace("/api/health", "/", StringComparison.OrdinalIgnoreCase) ?? "http://localhost:5080/",
            LogPaths = Directory.Exists(logs)
                ? Directory.GetFiles(logs, "launch-control-*.log")
                    .OrderByDescending(File.GetLastWriteTimeUtc)
                    .Take(2)
                    .Concat(Directory.GetFiles(logs, "*.log").Take(2))
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .Take(4)
                    .ToArray()
                : [],
            CrashLogPath = Path.Combine(root, "logs", "launch-control.log"),
            DefaultColors = new Dictionary<string, string>
            {
                ["ChromeColor"] = "#1B365D",
                ["PrimaryActionColor"] = "#2E6DA4"
            },
            MetaText = () => $"API {health ?? "(not configured)"}  Logs %ProgramData%\\Heimdall\\logs",
            StartupNotes =
            [
                "Closing this window does not stop Heimdall services.",
                "Republish / Setup run as actions in this window (not the old Setup shell).",
                "Expand Diagnostics / Recovery when needed. Log colors: blue = important/steps, green = OK, amber = warn, red = failures only."
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
                Mode("Backup API database…", "BackupApiDatabase", "Recovery"),
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
        var agent = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Heimdall", "Agent", "appsettings.json");
        try
        {
            if (File.Exists(agent))
            {
                var text = File.ReadAllText(agent);
                var idx = text.IndexOf("ApiBaseUrl", StringComparison.OrdinalIgnoreCase);
                if (idx >= 0)
                {
                    var q1 = text.IndexOf('"', idx + 10);
                    var q2 = text.IndexOf('"', q1 + 1);
                    var q3 = text.IndexOf('"', q2 + 1);
                    if (q2 > 0 && q3 > q2)
                    {
                        var url = text[(q2 + 1)..q3].Trim().TrimEnd('/');
                        if (url.StartsWith("http", StringComparison.OrdinalIgnoreCase))
                            return url + "/api/health";
                    }
                }
            }
        }
        catch { }

        return "http://127.0.0.1:5080/api/health";
    }
}
