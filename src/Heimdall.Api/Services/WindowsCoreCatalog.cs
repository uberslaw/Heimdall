namespace Heimdall.Api.Services;

/// <summary>Curated Windows core / system processes — not user-editable for POC.</summary>
public static class WindowsCoreCatalog
{
    /// <summary>
    /// Processes treated as Core Windows (excluded from default app tracking / proposals).
    /// Extends the historical noise baseline with common shell, service, and runtime hosts.
    /// </summary>
    public static IReadOnlyCollection<string> Names { get; } = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        // Kernel / session / core services
        "Idle", "System", "Registry", "Memory Compression", "Secure System",
        "svchost", "csrss", "smss", "wininit", "services", "lsass", "fontdrvhost", "lsm",
        "winlogon", "LogonUI", "userinit", "LsaIso", "WUDFHost", "dasHost", "WmiApSrv",

        // Shell / UX
        "explorer", "dwm", "sihost", "taskhostw", "ShellHost", "ShellExperienceHost",
        "StartMenuExperienceHost", "SearchHost", "SearchApp", "SearchProtocolHost", "SearchFilterHost",
        "SearchIndexer", "RuntimeBroker", "ApplicationFrameHost", "SystemSettings",
        "SystemSettingsBroker", "SystemSettingsAdminFlows", "SettingSyncHost", "LockApp",
        "ctfmon", "TextInputHost", "TabTip", "PhoneExperienceHost", "CrossDeviceResume",
        "Widgets", "WidgetService", "AggregatorHost", "AppActions", "backgroundTaskHost",
        "GameBar", "GameBarFTServer", "SCNotification", "UserOOBEBroker",

        // Consoles / terminals / scripting
        "conhost", "OpenConsole", "WindowsTerminal", "cmd", "powershell", "powershell_ise",
        "mmc", "notepad", "Notepad",

        // RDP / remote desktop client stack
        "mstsc", "msrdc", "msrdcw", "rdpclip", "rdpinput",

        // Security / Defender (in-box)
        "MsMpEng", "MpDefenderCoreService", "MpDlpService", "NisSrv", "smartscreen",
        "SecurityHealthSystray", "SecurityHealthService", "SecurityHealthHost",
        "SgrmBroker", "SgrmAgent", "MsSense",

        // Windows Update / servicing
        "MoUsoCoreWorker", "UsoClient", "TrustedInstaller", "TiWorker",
        "MusNotification", "MusNotificationUx", "DeviceEnroller", "DeviceCensus",

        // Common system hosts / brokers
        "dllhost", "WmiPrvSE", "spoolsv", "audiodg", "taskmgr", "CompPkgSrv",
        "unsecapp", "ProviderHost", "TrustedPeerMessageBrokerService",
        "BridgeCommunication", "CryptoService", "msdtc",

        // OEM / driver / firmware helpers (typical Win11 laptop baseline)
        "NVDisplay.Container", "nvWmi64", "ipfsvc", "ipf_uf",
        "PresentMonService", "WMIRegistrationService",

        // HP / Dell / Lenovo service caps (OEM baseline noise)
        "AppHelperCap", "DiagsCap", "NetworkCap", "SysInfoCap",
        "HotkeyServiceDSU", "LanWlanWwanSwitchingServiceDSU",
        "HpSfuService64", "hpsvcsscan", "IntelGraphicsSoftware.Service",

        // Microsoft Edge / WebView stack (in-box browser baseline)
        "msedge", "msedgewebview2", "MicrosoftEdgeUpdate", "MicrosoftEdgeSH", "MicrosoftEdgeCP",

        // Entra / broker plugins
        "Microsoft.AAD.BrokerPlugin",

        // Cloud / metering (Windows)
        "cloudmeteringhost",

        // Misc system utilities
        "crashpad_handler", "DtsApo4Service", "RtkAudUService64",

        // Heimdall / dev tooling on POC hosts
        "Heimdall.Agent", "dotnet"
    };

    /// <summary>Case-insensitive prefix stems for Core Windows process families.</summary>
    public static IReadOnlyList<string> PrefixStems { get; } =
    [
        "Sense" // Defender for Endpoint: SenseCE, SenseIR, SenseNdR, SenseTracer, SenseTVM, SenseDlPProcessor, …
    ];

    /// <summary>
    /// Core Windows processes that may still contribute machine-use / session-adjacent signals
    /// (logon, RDP client) without being proposed as trackable applications.
    /// </summary>
    public static IReadOnlyCollection<string> PresenceAllowlist { get; } = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "winlogon",
        "LogonUI",
        "mstsc",
        "msrdc",
        "msrdcw",
        "rdpclip",
        "rdpinput"
    };

    public static bool IsCoreWindows(string? processName)
    {
        if (string.IsNullOrWhiteSpace(processName))
            return false;
        var name = processName.Trim();
        if (Names.Contains(name))
            return true;
        return PrefixStems.Any(p => name.StartsWith(p, StringComparison.OrdinalIgnoreCase));
    }

    public static bool AllowForPresence(string? processName) =>
        !string.IsNullOrWhiteSpace(processName) && PresenceAllowlist.Contains(processName.Trim());
}
