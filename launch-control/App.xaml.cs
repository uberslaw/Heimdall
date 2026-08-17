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

        ExtraAction Mode(string title, string mode, string group, bool elevate = true) =>
            new(title, w =>
            {
                if (!File.Exists(setupPs1))
                {
                    w.AppendLog($"Missing {setupPs1}", "ERROR");
                    return;
                }
                ProcessUtil.StartPowerShell(setupPs1, scripts, elevate, "-Mode", mode);
                w.AppendLog($"Launching Setup -Mode {mode}");
            }, group);

        LaunchControlApp.Run(this, new LaunchControlProfile
        {
            ProductId = "heimdall",
            ProductName = "Heimdall",
            AppDataFolder = "Heimdall",
            ServiceNames = ["HeimdallApi", "HeimdallAgent"],
            HealthUrl = health,
            BrowserUrl = health?.Replace("/api/health", "/", StringComparison.OrdinalIgnoreCase) ?? "http://localhost:5080/",
            LogPaths = Directory.Exists(logs)
                ? Directory.GetFiles(logs, "*.log").Take(4).ToArray()
                : [],
            CrashLogPath = Path.Combine(root, "logs", "launch-control.log"),
            DefaultColors = new Dictionary<string, string>
            {
                ["ChromeColor"] = "#1B365D",
                ["PrimaryActionColor"] = "#2E6DA4"
            },
            MetaText = () => $"API {health ?? "(not configured)"}  Logs %ProgramData%\\Heimdall\\logs",
            ExtraActions =
            [
                Mode("Install API on this PC", "InstallApi", "Setup"),
                Mode("Install agent on this PC", "InstallCollector", "Setup"),
                Mode("Push client pack to PC(s)…", "PushClientPack", "Setup"),
                Mode("Create client pack", "PackCollector", "Republish"),
                Mode("Client health check", "ClientCheck", "Diagnostics", elevate: false),
                Mode("Collect diagnostics", "Diagnostics", "Diagnostics"),
                Mode("Open logs folder", "OpenLogs", "Diagnostics", elevate: false),
                Mode("Open remote logs folder…", "OpenRemoteLogs", "Diagnostics", elevate: false),
                new("Open dashboard", w =>
                {
                    var url = health?.Replace("/api/health", "/", StringComparison.OrdinalIgnoreCase) ?? "http://localhost:5080/";
                    ProcessUtil.OpenUrl(url.TrimEnd('/') + "/");
                    w.AppendLog($"Opened {url}");
                }, "Diagnostics"),
                Mode("Backup API database…", "BackupApiDatabase", "Recovery"),
                Mode("Remove seed/demo machines…", "RemoveSeedDemos", "Recovery"),
                new("Open full Setup (legacy UI)", w =>
                {
                    if (!File.Exists(setupPs1))
                    {
                        w.AppendLog($"Missing {setupPs1}", "ERROR");
                        return;
                    }
                    ProcessUtil.StartElevatedPowerShell(setupPs1, scripts);
                    w.AppendLog("Opened Heimdall Setup (WinForms) for Redeploy / pack zip / deposit / prerequisites.");
                }, "Setup"),
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
