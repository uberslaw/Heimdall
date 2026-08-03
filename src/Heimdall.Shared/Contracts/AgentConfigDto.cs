namespace Heimdall.Shared.Contracts;

public sealed class AgentConfigDto
{
    public int ConfigVersion { get; init; }
    public int SampleIntervalSeconds { get; init; } = 30;
    public int UploadIntervalSeconds { get; init; } = 60;
    public int ConfigRefreshSeconds { get; init; } = 300;
    public double MinCpuPercentToTrack { get; init; } = 0;
    public List<string> IncludeProcesses { get; init; } = [];
    public List<string> ExcludeProcesses { get; init; } = [];
    public List<KnownAppDto> KnownApps { get; init; } = [];
    /// <summary>Effective metric thresholds for this host (most-specific scope wins per metric).</summary>
    public List<MetricThresholdDto> MetricThresholds { get; init; } = [];
    /// <summary>Active pauses applied when resolving include/exclude for this host.</summary>
    public List<ProcessPauseDto> ProcessPauses { get; init; } = [];
    /// <summary>When true, agent should upload a one-shot process inventory on next ingest.</summary>
    public bool PendingAppAnalysis { get; init; }

    /// <summary>One-shot commands for the agent (e.g. RestartTermService). Cleared after heartbeat ack.</summary>
    public List<string> PendingCommands { get; init; } = [];

    /// <summary>When true, agent runs the always-on 30s fleet sampler (Historical Dashboard enrollment).</summary>
    public bool FleetSamplingEnabled { get; init; }

    /// <summary>
    /// Process name substrings used to detect TUFLOW (case-insensitive contains). Default: ["tuflow"].
    /// </summary>
    public List<string> FleetProcessNames { get; init; } = ["tuflow"];
}

public sealed class KnownAppDto
{
    public required string DisplayName { get; init; }
    public required string ProcessName { get; init; }
    public bool Enabled { get; init; } = true;
}

public sealed class MetricThresholdDto
{
    public required string MetricType { get; init; }
    public string Scope { get; init; } = "All";
    public string? ScopeValue { get; init; }
    public double? RamPercent { get; init; }
    public double? RamMb { get; init; }
    public double? GpuPercent { get; init; }
    public double? DiskReadMBps { get; init; }
    public double? DiskWriteMBps { get; init; }
    public double? DiskCombinedMBps { get; init; }
}

public sealed class ProcessPauseDto
{
    public required string ProcessName { get; init; }
    public required string ListKind { get; init; }
    public DateTimeOffset PausedUntilUtc { get; init; }
    public string? Reason { get; init; }
}
