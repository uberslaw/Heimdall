using Heimdall.Api.Data;

namespace Heimdall.Api.Services;

/// <summary>Curated corporate SOE / security / agent process catalog for auto-exclude.</summary>
public static class SoeCatalog
{
    public static IReadOnlyList<(string DisplayName, string ProcessName, string? Vendor)> Entries { get; } =
    [
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
        ("Microsoft Defender Antivirus", "MsMpEng", "Microsoft"),
        ("Windows Defender UI", "SecurityHealthService", "Microsoft"),
        ("Sense (Defender for Endpoint)", "Sense", "Microsoft"),

        // Thycotic / Delinea
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
