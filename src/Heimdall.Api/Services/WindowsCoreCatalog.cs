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
        "Idle", "System", "Registry", "Memory Compression", "Secure System",
        "svchost", "csrss", "smss", "wininit", "services", "lsass", "fontdrvhost",
        "RuntimeBroker", "SearchHost", "ShellExperienceHost", "dwm", "conhost",
        "explorer", "taskhostw", "sihost", "ctfmon", "dllhost", "WmiPrvSE",
        "spoolsv", "SearchIndexer", "StartMenuExperienceHost", "TextInputHost",
        "ApplicationFrameHost", "SystemSettings", "LockApp", "SecurityHealthSystray",
        "SecurityHealthService", "audiodg", "taskmgr", "MsMpEng", "NisSrv",
        "winlogon", "LogonUI", "userinit", "WUDFHost", "AggregatorHost",
        "backgroundTaskHost", "CompPkgSrv", "DeviceEnroller", "DeviceCensus",
        "MusNotification", "MusNotificationUx", "SgrmBroker", "SgrmAgent",
        "smartscreen", "SecurityHealthHost", "ShellHost", "SystemSettingsBroker",
        "WidgetService", "Widgets", "PhoneExperienceHost", "CrossDeviceResume",
        "SearchProtocolHost", "SearchFilterHost", "SearchApp", "GameBar",
        "GameBarFTServer", "SystemSettingsAdminFlows", "SettingSyncHost",
        "MicrosoftEdgeUpdate", "MicrosoftEdgeSH", "MicrosoftEdgeCP",
        "MoUsoCoreWorker", "UsoClient", "TrustedInstaller", "TiWorker",
        "WmiApSrv", "dasHost", "lsm",
        "Heimdall.Agent", "dotnet"
    };

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
        "rdpclip"
    };

    public static bool IsCoreWindows(string? processName) =>
        !string.IsNullOrWhiteSpace(processName) && Names.Contains(processName.Trim());

    public static bool AllowForPresence(string? processName) =>
        !string.IsNullOrWhiteSpace(processName) && PresenceAllowlist.Contains(processName.Trim());
}
