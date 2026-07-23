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

    public List<UserSession> Sessions { get; set; } = [];
    public List<ProcessRun> ProcessRuns { get; set; } = [];
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
    public long ActiveSeconds { get; set; }
    public long DisconnectedSeconds { get; set; }
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
}

public class KnownApp
{
    public int Id { get; set; }
    public required string DisplayName { get; set; }
    public required string ProcessName { get; set; }
    public bool EnabledByDefault { get; set; } = true;
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
