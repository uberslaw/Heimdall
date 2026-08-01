using Heimdall.Api.Data;

namespace Heimdall.Api.Services;

/// <summary>Curated corporate SOE / security / agent process catalog for auto-exclude.</summary>
public static class SoeCatalog
{
    public static IReadOnlyList<(string DisplayName, string ProcessName, string? Vendor)> Entries { get; } =
    [
        // —— Arup SOE (from field inventory) ——

        // 1E / Nomad
        ("1E Client", "1E.Client", "1E"),
        ("1E Client Interaction", "1E.Client.Interaction", "1E"),
        ("Nomad Branch", "NomadBranch", "1E"),

        // Cisco Secure Client / Umbrella / AMP
        ("Cisco Secure Client daemon", "ciscod", "Cisco"),
        ("Cisco NVM Agent", "acnvmagent", "Cisco"),
        ("Cisco Umbrella Agent", "acumbrellaagent", "Cisco"),
        ("Cisco Secure Client TE agent", "csc_te_agent", "Cisco"),
        ("Cisco Secure Client TE guardian", "csc_te_guardian", "Cisco"),
        ("Cisco Secure Client TE proxy", "csc_te_proxy", "Cisco"),
        ("Cisco Secure Client TE user agent", "csc_te_user_agent", "Cisco"),
        ("Cisco Secure Client ZTA agent", "csc_zta_agent", "Cisco"),
        ("Cisco Secure Client CM ID", "csc_cmid", "Cisco"),
        ("Cisco Secure Client CM S", "csc_cms", "Cisco"),
        ("Cisco Secure Client PM", "csc_pm", "Cisco"),
        ("Cisco Secure Client SWG agent", "csc_swgagent", "Cisco"),

        // Rapid7
        ("Rapid7 Agent Core", "rapid7_agent_core", "Rapid7"),
        ("Rapid7 Endpoint Broker", "rapid7_endpoint_broker", "Rapid7"),
        ("Rapid7 Events Monitor", "rapid7_events_monitor", "Rapid7"),
        ("Rapid7 Sysmon Installer", "rapid7_sysmon_installer", "Rapid7"),
        ("Rapid7 Velociraptor", "rapid7_velociraptor", "Rapid7"),
        ("Rapid7 IR Agent", "ir_agent", "Rapid7"),

        // Thycotic / Delinea / Arellia
        ("Delinea Agent Tray", "Delinea.Agent.TrayIcon", "Delinea"),
        ("Arellia Agent Service", "Arellia.Agent.Service", "Delinea"),
        ("Arellia AC Service", "ArelliaACSvc", "Delinea"),

        // Absolute DDS / Computrace
        ("Absolute Ctes", "Ctes", "Absolute"),
        ("Absolute Ctes Duration Service", "CtesDurSvc", "Absolute"),
        ("Absolute Ctes Host Service", "CtesHostSvc", "Absolute"),
        ("Absolute CtGeo Service", "CtGeoSvc", "Absolute"),
        ("Absolute CtHwi Provider", "CtHwiPrvService", "Absolute"),
        ("Absolute CtrRar Service", "CtrRarSvc", "Absolute"),
        ("Absolute rpcnet", "rpcnet", "Absolute"),

        // Duo
        ("Duo Desktop", "Duo Desktop", "Duo"),

        // Snow
        ("Snow Agent", "snowagent", "Snow Software"),

        // Templafy
        ("Templafy Desktop", "Templafy.Desktop", "Templafy"),

        // Microsoft Intune / M365 managed baseline
        ("Intune Windows Agent", "Microsoft.Management.Services.IntuneWindowsAgent", "Microsoft"),
        ("Office Click-to-Run", "OfficeClickToRun", "Microsoft"),
        ("MBAM Agent", "MBAMAgent", "Microsoft"),

        // Armor / MVArmor
        ("Armor Plugin Host", "ArmorPluginHost64", "Armor"),
        ("MV Armor Service (32)", "MVArmorService32", "Armor"),
        ("MV Armor Service (64)", "MVArmorService64", "Armor"),

        // ValueTrack
        ("ValueTrack Service", "ValueTrackSvc", "ValueTrack"),
        ("ValueTrack Tray", "ValueTrackTray", "ValueTrack"),

        // Hive Streaming
        ("Hive Streaming Desktop Helper", "HiveStreamingDesktopHelper2", "Hive Streaming"),
        ("Hive Streaming Service", "HiveStreamingService", "Hive Streaming"),

        // Print / PC-Print
        ("PC Print", "pc-print", "PC-Print"),
        ("PC Print Deploy Client", "pc-print-deploy-client", "PC-Print"),

        // HP Touchpoint Analytics
        ("HP Touchpoint Analytics", "TouchpointAnalyticsClientService", "HP"),

        // VNC (enterprise remote support)
        ("VNC Agent", "vncagent", "RealVNC"),
        ("VNC Server", "vncserver", "RealVNC"),

        // DLP / DNS / misc Arup agents
        ("DLP User Agent", "DlpUserAgent", "Microsoft"),
        ("DNSCrypt Proxy", "dnscryptproxy", "DNSCrypt"),
        ("Pluggable Service", "pluggablesvc", "SOE"),
        ("User Context Service", "usercontextservice", "SOE"),
        ("Auto Update Service", "AutoUpdateService", "SOE"),
        ("SPS Agent", "sps", "SOE"),

        // —— Common enterprise SOE (retained) ——

        // Cisco Secure Endpoint / AMP / Secure Client
        ("Cisco Secure Endpoint", "sfc", "Cisco"),
        ("Cisco Secure Endpoint UI", "iptray", "Cisco"),
        ("Cisco AMP", "amp", "Cisco"),
        ("Cisco Orbital", "orbital", "Cisco"),
        ("Cisco Secure Client UI", "csc_ui", "Cisco"),
        ("Cisco AnyConnect UI", "vpnui", "Cisco"),
        ("Cisco AnyConnect agent", "vpnagent", "Cisco"),
        ("Cisco Secure Endpoint Daemon", "CiscoAMP", "Cisco"),

        // CrowdStrike
        ("CrowdStrike Falcon", "CSFalconService", "CrowdStrike"),
        ("CrowdStrike Falcon container", "CSFalconContainer", "CrowdStrike"),
        ("CrowdStrike Falcon UI", "CSFalcon", "CrowdStrike"),

        // Carbon Black / VMware
        ("Carbon Black", "CbDefense", "Carbon Black"),
        ("Carbon Black Sensor", "RepMgr", "Carbon Black"),
        ("Carbon Black Live Response", "CbOsxSensorService", "Carbon Black"),

        // Microsoft endpoint / management
        ("ConfigMgr / SCCM", "CcmExec", "Microsoft"),
        ("ConfigMgr Messaging", "CmRcService", "Microsoft"),
        ("Intune Management Extension", "IntuneManagementExtension", "Microsoft"),
        ("Company Portal", "CompanyPortal", "Microsoft"),

        // Thycotic / Delinea (legacy names)
        ("Delinea / Thycotic agent", "ssrdp-service", "Delinea"),
        ("Delinea Secret Server helper", "SecretServerHelper", "Delinea"),
        ("Delinea Privilege Manager", "Thycotic.Agent", "Delinea"),
        ("Delinea Local Security", "Thycotic.LocalSecurity", "Delinea"),

        // Splunk / logging
        ("Splunk Universal Forwarder", "splunkd", "Splunk"),
        ("Splunk UF Windows", "splunk-winevtlog", "Splunk"),

        // Other common SOE / agents
        ("Qualys Cloud Agent", "QualysAgent", "Qualys"),
        ("Tanium Client", "TaniumClient", "Tanium"),
        ("Tanium CX", "TaniumCX", "Tanium"),
        ("Ivanti / LANDESK", "issuser", "Ivanti"),
        ("Zscaler Client Connector", "ZSATunnel", "Zscaler"),
        ("Zscaler service", "ZSAService", "Zscaler"),
        ("Palo Alto GlobalProtect", "PanGPS", "Palo Alto"),
        ("GlobalProtect UI", "PanGPA", "Palo Alto"),
        ("SentinelOne", "SentinelAgent", "SentinelOne"),
        ("SentinelOne helper", "SentinelHelperService", "SentinelOne"),
        ("Symantec / Broadcom Endpoint", "ccSvcHst", "Broadcom"),
        ("McAfee / Trellix Agent", "macmnsvc", "Trellix"),
        ("Trellix ENS", "mfemms", "Trellix"),
        ("Netskope Client", "nsclient", "Netskope"),
        ("Okta Verify", "OktaVerify", "Okta"),
        ("BeyondTrust Privilege Management", "DefendpointService", "BeyondTrust"),
        ("CyberArk EPM", "VF_Agent", "CyberArk"),
        ("Lansweeper agent", "LansweeperApp", "Lansweeper"),
        ("NinjaRMM / NinjaOne", "NinjaRMMAgent", "NinjaOne"),
        ("ConnectWise Automate", "LTService", "ConnectWise"),
        ("Azure Arc / HIMDS", "himds", "Microsoft"),
        ("Azure Connected Machine Agent", "azcmagent", "Microsoft")
    ];

    /// <summary>Prefix stems matched case-insensitively (e.g. rapid7_agent_core).</summary>
    public static IReadOnlyList<string> PrefixStems { get; } =
    [
        "rapid7_",
        "csc_",
        "1E.",
        "Arellia.",
        "Delinea.",
        "Ctes"
    ];

    private static readonly HashSet<string> ProcessNameSet = new(
        Entries.Select(e => e.ProcessName),
        StringComparer.OrdinalIgnoreCase);

    public static bool Contains(string? processName)
    {
        if (string.IsNullOrWhiteSpace(processName))
            return false;
        var name = processName.Trim();
        if (ProcessNameSet.Contains(name))
            return true;
        return PrefixStems.Any(p => name.StartsWith(p, StringComparison.OrdinalIgnoreCase));
    }

    public static IEnumerable<SoeApp> CreateSeedEntities() =>
        Entries
            .GroupBy(e => e.ProcessName, StringComparer.OrdinalIgnoreCase)
            .Select(g => g.First())
            .Select(e => new SoeApp
            {
                DisplayName = e.DisplayName,
                ProcessName = e.ProcessName,
                Category = "SOE",
                Vendor = e.Vendor
            });
}
