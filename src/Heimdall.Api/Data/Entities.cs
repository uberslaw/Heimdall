using Heimdall.Shared.Contracts;

namespace Heimdall.Api.Data;

public class Machine
{
    public int Id { get; set; }
    public required string Hostname { get; set; }
    /// <summary>Human-facing name on the Machines list; hostname remains the agent key.</summary>
    public string? FriendlyName { get; set; }
    /// <summary>Optional team for Machines list section grouping.</summary>
    public int? TeamId { get; set; }
    public Team? Team { get; set; }
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
    /// <summary>Last merged process inventory snapshot from agent discovery + ProcessRuns (paths for UI/CSV).</summary>
    public string? DiscoveredInventoryJson { get; set; }
    /// <summary>When the agent last uploaded a process inventory snapshot (UTC).</summary>
    public DateTimeOffset? InventoryCollectedUtc { get; set; }
    /// <summary>JSON array of <see cref="Heimdall.Shared.Contracts.DiskVolumeDto"/> from the agent.</summary>
    public string? DiskVolumesJson { get; set; }
    /// <summary>When DiskVolumesJson was last refreshed (UTC).</summary>
    public DateTimeOffset? DiskVolumesUtc { get; set; }

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

    /// <summary>Primary IPv4 from agent heartbeat.</summary>
    public string? LastIp { get; set; }
    /// <summary>TermService status from agent: Running, Stopped, Unknown, etc.</summary>
    public string? TermServiceStatus { get; set; }
    public DateTimeOffset? TermServiceCheckedUtc { get; set; }
    /// <summary>JSON from last API-side RDP probe (RdpResponding, Error, etc.).</summary>
    public string? LastRdpProbeResultJson { get; set; }
    public DateTimeOffset? LastRdpProbeUtc { get; set; }
    /// <summary>JSON from last API-side ping (Reachable, Detail, Target).</summary>
    public string? LastPingResultJson { get; set; }
    public DateTimeOffset? LastPingUtc { get; set; }
    /// <summary>JSON array of pending agent commands (RestartTermService, …).</summary>
    public string? PendingCommandsJson { get; set; }
    /// <summary>Restart RDS workflow progress (phase, attempts, verification result).</summary>
    public string? RestartRdsProgressJson { get; set; }

    /// <summary>JSON TuflowStartRequestDto queued for the agent; cleared once heartbeat status confirms pickup.</summary>
    public string? PendingTuflowStartJson { get; set; }
    /// <summary>Latest JSON TuflowRunStatusDto reported by the agent for the run it is tracking, if any.</summary>
    public string? TuflowRunStatusJson { get; set; }

    /// <summary>JSON ClientUpdateRequestDto queued for silent agent self-update.</summary>
    public string? PendingClientUpdateJson { get; set; }
    /// <summary>Client update workflow progress (phase, detail, timestamps).</summary>
    public string? ClientUpdateProgressJson { get; set; }

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
    /// <summary>Free-text narrative (e.g. AI-filled app description). Import/export via App Lists CSV.</summary>
    public string? Description { get; set; }
    public DateTimeOffset UpdatedUtc { get; set; }
}

/// <summary>
/// Central catalog of every unique process ever discovered, keyed by ProcessName + ExecutablePath
/// (same name at a different path is a separate entry). Persists across lookups so the discovered
/// universe of apps is never lost, and tracks Windows file-version metadata when available.
/// </summary>
public class ProcessCatalogEntry
{
    public int Id { get; set; }
    public required string ProcessName { get; set; }
    /// <summary>Empty string (not null) when path is unknown, so identity is stable for the unique index.</summary>
    public string ExecutablePath { get; set; } = "";
    public string? DisplayName { get; set; }
    /// <summary>From Win32 FileVersionInfo on the agent, when the path was readable.</summary>
    public string? FileVersion { get; set; }
    public string? ProductVersion { get; set; }
    /// <summary>Human-entered version, used on the Discovery page when FileVersion/ProductVersion are unknown and a path-derived guess isn't good enough.</summary>
    public string? ManualVersion { get; set; }
    /// <summary>Free-text app description (Discovery edits, classification CSV import).</summary>
    public string? Description { get; set; }
    /// <summary>User-facing category label (Discovery edits, optional CSV column).</summary>
    public string? Category { get; set; }
    /// <summary>User-facing subcategory label (Discovery edits, optional CSV column).</summary>
    public string? Subcategory { get; set; }
    public string? CompanyName { get; set; }
    public string? FileDescription { get; set; }
    public DateTimeOffset FirstSeenUtc { get; set; }
    public DateTimeOffset LastSeenUtc { get; set; }
    public int SeenCount { get; set; } = 1;
    public string? FirstSeenHostname { get; set; }
    public string? LastSeenHostname { get; set; }
    /// <summary>JSON map hostname → { lastSeenUtc, count } for every machine that reported this process.</summary>
    public string? SeenHostnamesJson { get; set; }
    /// <summary>When true, excluded from "awaiting classification" counts (catalog noise / known junk).</summary>
    public bool Ignored { get; set; }
    /// <summary>Heuristic suggestion computed when first seen and not yet explicitly classified; see ProcessCatalogService.</summary>
    public AppGroup? SuggestedGroup { get; set; }
    public string? SuggestionReason { get; set; }
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
    /// <summary>When true, machines on this team appear in the Staff RDP pool.</summary>
    public bool IsPublicFacing { get; set; }
    /// <summary>Entra ID (Azure AD) group object id for membership sync via Microsoft Graph.</summary>
    public string? EntraGroupId { get; set; }
    /// <summary>Cached Entra group display name from last successful resolve/sync.</summary>
    public string? EntraGroupName { get; set; }
    public DateTimeOffset? EntraMembersSyncedUtc { get; set; }
    /// <summary>Last sync error message (null when last sync succeeded or never run).</summary>
    public string? EntraLastSyncError { get; set; }
    public List<Team> Children { get; set; } = [];
    public List<PersonTeam> Members { get; set; } = [];
    public List<TeamAppListLink> AppListLinks { get; set; } = [];
}

/// <summary>Staff RDP booking window (max 24h) for a public-facing machine.</summary>
public class MachineBooking
{
    public int Id { get; set; }
    public int MachineId { get; set; }
    public Machine Machine { get; set; } = null!;
    public required string BookedByEmail { get; set; }
    public DateTimeOffset StartUtc { get; set; }
    public DateTimeOffset EndUtc { get; set; }
    public DateTimeOffset CreatedUtc { get; set; }
    public string? Notes { get; set; }
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

/// <summary>
/// Apply/ignore relationship between a team and an app list (many-to-many).
/// Tracking visibility on Machines uses links with <see cref="IsExcluded"/> = false.
/// </summary>
public class TeamAppListLink
{
    public int Id { get; set; }
    public int TeamId { get; set; }
    public Team Team { get; set; } = null!;
    public int AppListId { get; set; }
    public AppList AppList { get; set; } = null!;
    /// <summary>When true, list is linked but ignored for this team (Do Not Track).</summary>
    public bool IsExcluded { get; set; }
}

/// <summary>Named schema / app list of process names, optionally with a primary team for metadata.</summary>
public class AppList
{
    public int Id { get; set; }
    public required string Name { get; set; }
    /// <summary>Optional primary/owning team metadata (App Lists form). Team apply/ignore uses <see cref="TeamAppListLink"/>.</summary>
    public int? TeamId { get; set; }
    public Team? Team { get; set; }
    public string? Notes { get; set; }
    public DateTimeOffset CreatedUtc { get; set; }
    public DateTimeOffset UpdatedUtc { get; set; }
    /// <summary>True when generated by machine app analysis (“Discovered on {host}”).</summary>
    public bool IsAutoDiscovered { get; set; }
    /// <summary>Legacy single-team exclude flag; prefer <see cref="TeamAppListLink.IsExcluded"/>.</summary>
    public bool IsTeamExcluded { get; set; }
    /// <summary>Protected classification-backed list (Core Windows / SOE / Specialization). Cannot be deleted.</summary>
    public bool IsSystem { get; set; }
    /// <summary>Stable identity for system lists: <c>CoreWindows</c>, <c>Soe</c>, or <c>Specialization</c>.</summary>
    public string? SystemKey { get; set; }

    public List<AppListEntry> Entries { get; set; } = [];
    public List<AppListAssignment> Assignments { get; set; } = [];
    public List<TeamAppListLink> TeamLinks { get; set; } = [];
}

public class AppListEntry
{
    public int Id { get; set; }
    public int AppListId { get; set; }
    public AppList AppList { get; set; } = null!;
    public required string ProcessName { get; set; }
    public string? DisplayName { get; set; }
}

/// <summary>
/// Per-machine override of a team's app-list track/ignore. Wins over <see cref="TeamAppListLink"/> for that host.
/// IsExcluded = true → do not track this list on this machine; false → force track even if team ignores.
/// </summary>
public class MachineAppListOverride
{
    public int Id { get; set; }
    public int MachineId { get; set; }
    public Machine Machine { get; set; } = null!;
    public int AppListId { get; set; }
    public AppList AppList { get; set; } = null!;
    public bool IsExcluded { get; set; }
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

/// <summary>
/// A Staff Access / Remote Access Group: a named set of staff emails and the machines they may view on
/// their staff page. Access control is enforced by group membership — a machine not assigned to any group
/// the signed-in email belongs to is never returned to that staff member.
/// </summary>
public class RemoteAccessGroup
{
    public int Id { get; set; }
    public required string Name { get; set; }
    /// <summary>Shared per-group preference: when true, staff page shows only favourited processes (see complete-first-time notes on why per-group, not per-staff).</summary>
    public bool FavoritesOnly { get; set; }
    public DateTimeOffset CreatedUtc { get; set; }
    public DateTimeOffset UpdatedUtc { get; set; }

    public List<RemoteAccessGroupStaff> Staff { get; set; } = [];
    public List<RemoteAccessGroupMachine> Machines { get; set; } = [];
    public List<RemoteAccessFavoriteProcess> FavoriteProcesses { get; set; } = [];
}

public class RemoteAccessGroupStaff
{
    public int Id { get; set; }
    public int GroupId { get; set; }
    public RemoteAccessGroup Group { get; set; } = null!;
    public required string Email { get; set; }
}

public class RemoteAccessGroupMachine
{
    public int Id { get; set; }
    public int GroupId { get; set; }
    public RemoteAccessGroup Group { get; set; } = null!;
    public required string Hostname { get; set; }
    /// <summary>Per-group display label on the Staff page; hostname remains the canonical id.</summary>
    public string? FriendlyName { get; set; }
}

/// <summary>Favourited process name for a group (persisted per-group — see RemoteAccessGroup.FavoritesOnly).</summary>
public class RemoteAccessFavoriteProcess
{
    public int Id { get; set; }
    public int GroupId { get; set; }
    public RemoteAccessGroup Group { get; set; } = null!;
    public required string ProcessName { get; set; }
}

/// <summary>
/// One active browser tab viewing a group's Staff Access page. Heartbeats every ~20s and an explicit
/// leave (sendBeacon on pagehide/unload) drive ref-counted fan-in: sampling starts on the API's next
/// resolution once any viewer exists for a host and stops once none remain (see LiveSamplingService).
/// </summary>
public class RemoteAccessViewer
{
    public int Id { get; set; }
    public int GroupId { get; set; }
    public required string ViewerId { get; set; }
    public string? Email { get; set; }
    public DateTimeOffset LastHeartbeatUtc { get; set; }
}

/// <summary>
/// One active browser tab viewing a single machine's Sessions "Open" drill-down modal — the same
/// ref-counted heartbeat/leave pattern as RemoteAccessViewer, but keyed directly by hostname instead of a
/// Remote Access Group, since any machine can appear on the Sessions page (not just staff-assigned ones).
/// LiveSamplingService.IsHostnameActiveAsync treats a host as active if either source has a live viewer.
/// </summary>
public class SessionDrilldownViewer
{
    public int Id { get; set; }
    public required string Hostname { get; set; }
    public required string ViewerId { get; set; }
    public DateTimeOffset LastHeartbeatUtc { get; set; }
}

/// <summary>Curated enrollment for the Historical Dashboard (TUFLOW fleet POC). Only enrolled hosts get 30s snapshots.</summary>
public class FleetDashboardMachine
{
    public int Id { get; set; }
    public int MachineId { get; set; }
    public Machine Machine { get; set; } = null!;
    public DateTimeOffset AddedUtc { get; set; }
    public string? Notes { get; set; }
}

/// <summary>Append-only 30s fleet metric sample (always-on for known Machines with Heimdall client).</summary>
public class FleetMetricSnapshot
{
    public long Id { get; set; }
    public DateTimeOffset SampledAtUtc { get; set; }
    public int MachineId { get; set; }
    public Machine Machine { get; set; } = null!;
    public string? Username { get; set; }
    public bool TuflowRunning { get; set; }
    public double? CpuPercent { get; set; }
    public double? GpuPercent { get; set; }
    public double? GpuMemoryUsedMb { get; set; }
    public double? RamUsedMb { get; set; }
    public double? DiskReadMBps { get; set; }
    public double? DiskWriteMBps { get; set; }
    public double? NetworkInMBps { get; set; }
    public double? NetworkOutMBps { get; set; }
    /// <summary>TUFLOW-process CPU % at this sample — see FleetSnapshotDto.ProcessCpuPercent. Null for
    /// samples from older agents that don't report process-specific figures yet.</summary>
    public double? ProcessCpuPercent { get; set; }
    /// <summary>TUFLOW-process GPU % at this sample — see FleetSnapshotDto.ProcessGpuPercent.</summary>
    public double? ProcessGpuPercent { get; set; }
    /// <summary>TUFLOW-process disk read MB/s at this sample — see FleetSnapshotDto.ProcessDiskReadMBps.</summary>
    public double? ProcessDiskReadMBps { get; set; }
    /// <summary>TUFLOW-process disk write MB/s at this sample — see FleetSnapshotDto.ProcessDiskWriteMBps.</summary>
    public double? ProcessDiskWriteMBps { get; set; }
    /// <summary>True when TuflowRunning and any active threshold is met (stored at ingest for stable history).</summary>
    public bool IsActive { get; set; }
}

/// <summary>
/// One row per TUFLOW run request (keyed by RunId), created when queued and updated in place as the
/// run progresses/finishes. Unlike Machine.TuflowRunStatusJson (which only ever holds the latest run),
/// each run gets its own permanent row here — so a crash's ErrorSummary/ExitCode survives even after a
/// later run overwrites the "current status" field. Powers the per-machine run history on Machine.cshtml.
/// </summary>
public class TuflowRunRecord
{
    public int Id { get; set; }
    public required string RunId { get; set; }
    /// <summary>"Which simulation" — see TuflowStartRequestDto.RunName for how it's resolved.</summary>
    public required string RunName { get; set; }
    public int MachineId { get; set; }
    public Machine Machine { get; set; } = null!;
    public required string TcfPath { get; set; }
    public DateTimeOffset RequestedUtc { get; set; }
    public string? RequestedBy { get; set; }
    public DateTimeOffset? StartedUtc { get; set; }
    /// <summary>Set once, the first time this run's state becomes Stopped/Completed/Failed.</summary>
    public DateTimeOffset? EndedUtc { get; set; }
    /// <summary>Live-updated — one of TuflowRunStates.* (Starting/Running/.../Stopped/Completed/Failed).</summary>
    public required string State { get; set; }
    public double? PercentComplete { get; set; }
    public double? SimulationTimeHours { get; set; }
    public double? SimulationEndTimeHours { get; set; }
    /// <summary>TUFLOW's own "Approximate Clock Time Remaining (h)" from the .tsf.</summary>
    public double? ClockTimeRemainingHours { get; set; }
    public int? WarningCount { get; set; }
    public double? MassErrorPercent { get; set; }
    public int? ExitCode { get; set; }
    public string? ErrorSummary { get; set; }
    public string? LastCheckpointFile { get; set; }
    public DateTimeOffset UpdatedUtc { get; set; }
}

/// <summary>Latest live resource-metrics snapshot reported by the agent for a machine (one row per machine, upserted).</summary>
public class MachineResourceMetric
{
    public int Id { get; set; }
    public int MachineId { get; set; }
    public Machine Machine { get; set; } = null!;
    public DateTimeOffset SampledAtUtc { get; set; }
    public bool IsCalibrationAverage { get; set; }

    public double? CpuPercent { get; set; }
    public double? GpuPercent { get; set; }
    public double? RamPercent { get; set; }
    public double? RamUsedGb { get; set; }
    public double? RamTotalGb { get; set; }

    public double? DiskReadBytesPerSec { get; set; }
    public double? DiskWriteBytesPerSec { get; set; }
    public string DiskReadLevel { get; set; } = "Low";
    public string DiskWriteLevel { get; set; } = "Low";

    /// <summary>JSON array of TopProcessSampleDto.</summary>
    public string TopCpuProcessesJson { get; set; } = "[]";
    public string TopGpuProcessesJson { get; set; } = "[]";
    public string TopRamProcessesJson { get; set; } = "[]";
    public string TopDiskReadProcessesJson { get; set; } = "[]";
    public string TopDiskWriteProcessesJson { get; set; } = "[]";
    /// <summary>JSON array of FavoriteProcessSampleDto.</summary>
    public string FavoriteProcessesJson { get; set; } = "[]";
}

/// <summary>
/// A user-defined UI theme skin layered on top of one of the built-in structural presets
/// (see UiTheme.Presets). Colours are hex strings; *Opacity fields are 0..1 fractions used to build
/// rgba() CSS values. HeadingFont/BodyFont are keys into ThemeFonts.Catalog — null inherits the base
/// preset's font. See CustomThemeStyle for how these fields become CSS custom properties.
/// </summary>
public class CustomTheme
{
    public int Id { get; set; }
    public required string Name { get; set; }

    /// <summary>One of UiTheme.Presets — supplies structural CSS (blur/glass vs flat panels, brand logo).</summary>
    public string BasePreset { get; set; } = "cosmic";

    // Primary / secondary / accent
    public string PrimaryHex { get; set; } = "#d4b86a";
    public string SecondaryHex { get; set; } = "#c8ced8";
    public string AccentHex { get; set; } = "#a88838";

    // Text
    public string TextHex { get; set; } = "#eef1f8";
    public string MutedHex { get; set; } = "#a4aec4";

    // Panels / cards (with transparency)
    public string PanelHex { get; set; } = "#0a0e1a";
    public double PanelOpacity { get; set; } = 0.72;
    public string PanelAltHex { get; set; } = "#0e1220";
    public double PanelAltOpacity { get; set; } = 0.80;

    // Header / nav background
    public string HeaderBgHex { get; set; } = "#060810";
    public double HeaderBgOpacity { get; set; } = 0.72;

    // Borders / gold accent
    public string BorderHex { get; set; } = "#c0c6d0";
    public double BorderOpacity { get; set; } = 0.16;
    public string GoldHex { get; set; } = "#d4b86a";

    // Shading / overlays (glass shine + rim highlights)
    public string ShadeHex { get; set; } = "#ffecbe";
    public double ShadeOpacityPercent { get; set; } = 12;

    // Click / hover / active secondary colour
    public string HoverHex { get; set; } = "#ecd898";

    // Background colour, shader overlay, image
    public string BackgroundHex { get; set; } = "#060810";
    public string? BackgroundImagePath { get; set; }
    public double BackgroundOverlayOpacity { get; set; } = 0.38;

    // Fonts (null = inherit base preset default)
    public string? HeadingFont { get; set; }
    public string? BodyFont { get; set; }

    public DateTime CreatedUtc { get; set; }
    public DateTime UpdatedUtc { get; set; }
}
