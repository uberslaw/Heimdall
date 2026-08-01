using Heimdall.Shared.Contracts;

namespace Heimdall.Api.Data;

public class Machine
{
    public int Id { get; set; }
    public required string Hostname { get; set; }
    public string? MachineGroup { get; set; }
    /// <summary>Region for tree scoping (e.g. APAC). Parsed from MachineGroup or set explicitly.</summary>
    public string? Region { get; set; }
    /// <summary>Office location within region (e.g. Sydney).</summary>
    public string? Office { get; set; }
    /// <summary>Country for Stats scoping (POC: derived from Region, e.g. APAC → Australia).</summary>
    public string? Country { get; set; }
    public string? OsVersion { get; set; }
    public DateTimeOffset FirstSeenUtc { get; set; }
    public DateTimeOffset LastSeenUtc { get; set; }
    public bool IsInUse { get; set; }
    public string? AgentVersion { get; set; }
    /// <summary>When non-SOE app analysis last completed for this host (null = never).</summary>
    public DateTimeOffset? AppsAnalyzedAt { get; set; }
    /// <summary>Ask agent to upload a one-shot process inventory on next cycle.</summary>
    public bool PendingAppAnalysis { get; set; }
    /// <summary>None → PendingApproval → Approved | Dismissed. Discovered apps are not tracked until Approved.</summary>
    public AppAnalysisStatus AppAnalysisStatus { get; set; } = AppAnalysisStatus.None;
    /// <summary>JSON array of proposed apps awaiting approval: [{processName,displayName,source}].</summary>
    public string? AppAnalysisProposalJson { get; set; }

    // --- Cost / hardware inventory (manual + optional agent enrich) ---
    public decimal? PurchaseCost { get; set; }
    /// <summary>ISO-ish currency code; default AUD when purchase cost is set.</summary>
    public string? PurchaseCurrency { get; set; }
    public DateOnly? WarrantyStartDate { get; set; }
    public DateOnly? WarrantyEndDate { get; set; }
    public string? HardwareGpu { get; set; }
    public string? HardwareCpu { get; set; }
    public double? HardwareRamGb { get; set; }
    /// <summary>Total disk capacity in GB (logical/physical sum from agent or manual).</summary>
    public double? HardwareDiskGb { get; set; }
    public string? HardwareBrand { get; set; }
    public string? HardwareModel { get; set; }
    /// <summary>Preferred display / asset serial (hostname-derived when BIOS is generic).</summary>
    public string? HardwareSerialNumber { get; set; }
    /// <summary>Raw BIOS serial when distinct from asset serial.</summary>
    public string? BiosSerial { get; set; }
    /// <summary>Serial parsed from hostname (city + DT/LT + remainder).</summary>
    public string? AssetSerial { get; set; }
    public string? HostnameCityCode { get; set; }
    /// <summary>DT = desktop, LT = laptop when present in hostname.</summary>
    public string? HostnameChassisHint { get; set; }
    /// <summary>When true, agent heartbeat must not overwrite hardware inventory fields.</summary>
    public bool HardwareManualOverride { get; set; }
    /// <summary>PSU rated wattage — manual only; not available via WMI.</summary>
    public int? PsuWatts { get; set; }
    /// <summary>Optional live draw stub — agent does not measure desktops reliably; leave null.</summary>
    public int? PowerDrawWatts { get; set; }
    /// <summary>Optional $/hr for ops. support sessions (POC cost context).</summary>
    public decimal? SupportHourlyRate { get; set; }
    /// <summary>WMI/registry OS install date — often moves on Windows feature update.</summary>
    public DateTimeOffset? OsInstallDateUtc { get; set; }
    /// <summary>Creation time of %SystemRoot% — often closer to original image.</summary>
    public DateTimeOffset? WindowsFolderCreatedUtc { get; set; }
    /// <summary>HKLM Cryptography MachineGuid — changes on OS reimage.</summary>
    public string? MachineGuid { get; set; }
    /// <summary>SMBIOS UUID — hardware; usually survives reimage.</summary>
    public string? SmbiosUuid { get; set; }
    /// <summary>Most recent reimage detection (MachineGuid change for same hostname).</summary>
    public DateTimeOffset? LastReimagedUtc { get; set; }

    public List<UserSession> Sessions { get; set; } = [];
    public List<ProcessRun> ProcessRuns { get; set; } = [];
    public List<MachineIdentityEvent> IdentityEvents { get; set; } = [];
}

/// <summary>POC identity history: reimage / MachineGuid change for a hostname.</summary>
public class MachineIdentityEvent
{
    public int Id { get; set; }
    public int MachineId { get; set; }
    public Machine Machine { get; set; } = null!;
    /// <summary>Reimaged | GuidChanged | FirstSeen</summary>
    public required string EventType { get; set; }
    public string? OldMachineGuid { get; set; }
    public string? NewMachineGuid { get; set; }
    public string? OldSmbiosUuid { get; set; }
    public string? NewSmbiosUuid { get; set; }
    public DateTimeOffset ObservedAtUtc { get; set; }
    public string? Detail { get; set; }
}

public enum AppAnalysisStatus
{
    None = 0,
    PendingApproval = 1,
    Approved = 2,
    Dismissed = 3
}

public class UserSession
{
    public int Id { get; set; }
    public int MachineId { get; set; }
    public Machine Machine { get; set; } = null!;
    public required string ExternalEventId { get; set; }
    public int SessionId { get; set; }
    public required string Username { get; set; }
    public string? Domain { get; set; }
    public SessionType SessionType { get; set; }
    public SessionState State { get; set; }
    public DateTimeOffset StartedAtUtc { get; set; }
    public DateTimeOffset? EndedAtUtc { get; set; }
    public DateTimeOffset LastObservedUtc { get; set; }
    public string? ClientName { get; set; }
    public string? ClientAddress { get; set; }
    /// <summary>Total active seconds (Local + Inbound RDP buckets).</summary>
    public long ActiveSeconds { get; set; }
    /// <summary>Total disconnected seconds (Local + Inbound RDP buckets).</summary>
    public long DisconnectedSeconds { get; set; }
    public long LocalActiveSeconds { get; set; }
    public long LocalDisconnectedSeconds { get; set; }
    public long InboundRdpActiveSeconds { get; set; }
    public long InboundRdpDisconnectedSeconds { get; set; }
}

public class ProcessRun
{
    public int Id { get; set; }
    public int MachineId { get; set; }
    public Machine Machine { get; set; } = null!;
    public required string ExternalRunId { get; set; }
    public required string Username { get; set; }
    public required string ProcessName { get; set; }
    public string? ExecutablePath { get; set; }
    public int ProcessId { get; set; }
    public DateTimeOffset StartedAtUtc { get; set; }
    public DateTimeOffset? EndedAtUtc { get; set; }
    public DateTimeOffset LastSeenAtUtc { get; set; }
    public int SampleCount { get; set; }
    public double? PeakCpuPercent { get; set; }
    /// <summary>Optional peak GPU % — agents may not report yet.</summary>
    public double? PeakGpuPercent { get; set; }
    /// <summary>Optional cumulative disk read bytes — agents may not report yet.</summary>
    public long? DiskReadBytes { get; set; }
    /// <summary>Optional cumulative disk write bytes — agents may not report yet.</summary>
    public long? DiskWriteBytes { get; set; }
}

public class TrackingConfig
{
    public int Id { get; set; }
    public string Name { get; set; } = "Default";
    public ConfigScope Scope { get; set; } = ConfigScope.All;
    public string? ScopeValue { get; set; }
    public int Priority { get; set; }
    public bool IsEnabled { get; set; } = true;
    public int SampleIntervalSeconds { get; set; } = 30;
    public int UploadIntervalSeconds { get; set; } = 60;
    public int ConfigRefreshSeconds { get; set; } = 300;
    public double MinCpuPercentToTrack { get; set; }
    public string IncludeProcessesJson { get; set; } = "[]";
    public string ExcludeProcessesJson { get; set; } = "[]";

    public List<ProcessPause> ProcessPauses { get; set; } = [];
}

/// <summary>
/// Temporary override for a process on a tracking config.
/// Include pause = skip tracking; Exclude pause = stop applying that exclude rule.
/// </summary>
public class ProcessPause
{
    public int Id { get; set; }
    public int TrackingConfigId { get; set; }
    public TrackingConfig TrackingConfig { get; set; } = null!;
    public required string ProcessName { get; set; }
    public ProcessListKind ListKind { get; set; }
    public DateTimeOffset PausedUntilUtc { get; set; }
    public string? Reason { get; set; }
}

public class KnownApp
{
    public int Id { get; set; }
    public required string DisplayName { get; set; }
    public required string ProcessName { get; set; }
    public bool EnabledByDefault { get; set; } = true;
}

/// <summary>Curated corporate SOE / security / agent processes for auto-exclude.</summary>
public class SoeApp
{
    public int Id { get; set; }
    public required string DisplayName { get; set; }
    public required string ProcessName { get; set; }
    public string Category { get; set; } = "SOE";
    public string? Vendor { get; set; }
}

/// <summary>User override for process group membership (wins over static catalogs and SoeApps).</summary>
public class ProcessGroupAssignment
{
    public int Id { get; set; }
    public required string ProcessName { get; set; }
    public AppGroup Group { get; set; }
    public string? DisplayName { get; set; }
    public DateTimeOffset UpdatedUtc { get; set; }
}

/// <summary>Performance metric threshold policy scoped to All / Region / Office / Group / Machine.</summary>
public class MetricPolicy
{
    public int Id { get; set; }
    public string Name { get; set; } = "";
    public MetricType MetricType { get; set; }
    public ConfigScope Scope { get; set; } = ConfigScope.All;
    public string? ScopeValue { get; set; }
    public bool IsEnabled { get; set; } = true;
    public double? RamPercentThreshold { get; set; }
    public double? RamMbThreshold { get; set; }
    public double? GpuPercentThreshold { get; set; }
    public double? DiskReadMBpsThreshold { get; set; }
    public double? DiskWriteMBpsThreshold { get; set; }
    public double? DiskCombinedMBpsThreshold { get; set; }
}

/// <summary>Business unit / team for org mapping (POC hierarchy via ParentTeamId).</summary>
public class Team
{
    public int Id { get; set; }
    public required string Name { get; set; }
    public string? Code { get; set; }
    public int? ParentTeamId { get; set; }
    public Team? ParentTeam { get; set; }
    public List<Team> Children { get; set; } = [];
    public List<PersonTeam> Members { get; set; } = [];
}

/// <summary>Maps a Windows username (and optional domain) to a team.</summary>
public class PersonTeam
{
    public int Id { get; set; }
    public required string Username { get; set; }
    public string? Domain { get; set; }
    public string? DisplayName { get; set; }
    public string? Email { get; set; }
    public int TeamId { get; set; }
    public Team Team { get; set; } = null!;
}

/// <summary>
/// Global (POC) ideal-utilization weights and targets for Socratize scoring.
/// Per-region scopes can come later — one row with Scope=Global for now.
/// </summary>
public class UtilizationCriteria
{
    public int Id { get; set; }
    /// <summary>POC: always "Global".</summary>
    public string Scope { get; set; } = "Global";
    public string? ScopeValue { get; set; }

    public double WeightUsers { get; set; } = 25;
    public double WeightDailyUtil { get; set; } = 35;
    public double WeightMetricBusy { get; set; } = 20;
    public double WeightAppValue { get; set; } = 20;

    /// <summary>Ideal distinct users in the analysis period.</summary>
    public int IdealMinUsers { get; set; } = 2;

    /// <summary>
    /// Ideal % of working capacity that is active session time.
    /// Working capacity = period days × WorkingHoursPerDay.
    /// </summary>
    public double IdealDailyUtilPct { get; set; } = 40;

    /// <summary>Assumed productive hours per calendar day for util / busy denominators.</summary>
    public double WorkingHoursPerDay { get; set; } = 8;

    /// <summary>Peak CPU % on a process run that counts as “busy” for metric time.</summary>
    public double BusyCpuPercentThreshold { get; set; } = 25;

    /// <summary>Peak GPU % that counts as busy (ignored when no GPU samples).</summary>
    public double BusyGpuPercentThreshold { get; set; } = 20;

    /// <summary>Ideal busy metric time as % of working capacity.</summary>
    public double IdealMetricBusyPct { get; set; } = 15;

    /// <summary>Ideal max license $/hour (cost/year ÷ annualized open hours). Lower is better.</summary>
    public double IdealMaxCostPerHour { get; set; } = 50;

    public double HighScoreThreshold { get; set; } = 75;
    public double AdequateScoreThreshold { get; set; } = 50;
    public double MixedScoreThreshold { get; set; } = 30;
}

/// <summary>Annual license cost for a tracked process name (business-value scoring).</summary>
public class AppLicenseCost
{
    public int Id { get; set; }
    public required string ProcessName { get; set; }
    public string? DisplayName { get; set; }
    public double LicenseCostPerYear { get; set; }
}

/// <summary>Named schema / app list of process names, optionally linked to a team.</summary>
public class AppList
{
    public int Id { get; set; }
    public required string Name { get; set; }
    public int? TeamId { get; set; }
    public Team? Team { get; set; }
    public string? Notes { get; set; }
    public DateTimeOffset CreatedUtc { get; set; }
    public DateTimeOffset UpdatedUtc { get; set; }
    /// <summary>True when generated by machine app analysis (“Discovered on {host}”).</summary>
    public bool IsAutoDiscovered { get; set; }

    public List<AppListEntry> Entries { get; set; } = [];
    public List<AppListAssignment> Assignments { get; set; } = [];
}

public class AppListEntry
{
    public int Id { get; set; }
    public int AppListId { get; set; }
    public AppList AppList { get; set; } = null!;
    public required string ProcessName { get; set; }
    public string? DisplayName { get; set; }
}

/// <summary>Applies an AppList to Global / Region / Country / Office / Group / Machine scope(s).</summary>
public class AppListAssignment
{
    public int Id { get; set; }
    public int AppListId { get; set; }
    public AppList AppList { get; set; } = null!;
    public ConfigScope Scope { get; set; } = ConfigScope.All;
    public string? ScopeValue { get; set; }
    public int Priority { get; set; }
    public bool IsEnabled { get; set; } = true;
}

/// <summary>Audit trail for app-list assign / unassign / upload / analysis actions.</summary>
public class AppListAuditLog
{
    public int Id { get; set; }
    public DateTimeOffset Utc { get; set; }
    public required string Action { get; set; }
    public int? AppListId { get; set; }
    public string? AppListName { get; set; }
    public ConfigScope? Scope { get; set; }
    public string? ScopeValue { get; set; }
    public string? MachineHostname { get; set; }
    public required string Detail { get; set; }
    public string? Actor { get; set; }
}
