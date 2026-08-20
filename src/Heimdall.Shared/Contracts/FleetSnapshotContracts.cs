namespace Heimdall.Shared.Contracts;

/// <summary>One always-on fleet sample from an agent (POST /api/fleet/snapshot).</summary>
public sealed class FleetSnapshotDto
{
    public required string Hostname { get; init; }
    public DateTimeOffset SampledAtUtc { get; init; }
    /// <summary>Primary interactive session user when present.</summary>
    public string? Username { get; init; }
    public bool TuflowRunning { get; init; }
    public double? CpuPercent { get; init; }
    public double? GpuPercent { get; init; }
    public double? GpuMemoryUsedMb { get; init; }
    public double? RamUsedMb { get; init; }
    public double? DiskReadMBps { get; init; }
    public double? DiskWriteMBps { get; init; }
    public double? NetworkInMBps { get; init; }
    public double? NetworkOutMBps { get; init; }

    /// <summary>TUFLOW-process CPU % (sum of matching processes). Used for Active/Idle only.</summary>
    public double? ProcessCpuPercent { get; init; }
    /// <summary>TUFLOW-process GPU % (sum of matching processes). Used for Active/Idle only.</summary>
    public double? ProcessGpuPercent { get; init; }
    /// <summary>TUFLOW-process disk read MB/s. Used for Active/Idle only.</summary>
    public double? ProcessDiskReadMBps { get; init; }
    /// <summary>TUFLOW-process disk write MB/s. Used for Active/Idle only.</summary>
    public double? ProcessDiskWriteMBps { get; init; }

    /// <summary>Top CPU processes at this sample (for util drill-down). Empty on older agents.</summary>
    public List<TopProcessSampleDto> TopCpuProcesses { get; init; } = [];
    /// <summary>Top GPU processes at this sample.</summary>
    public List<TopProcessSampleDto> TopGpuProcesses { get; init; } = [];
    /// <summary>Top disk-read processes at this sample (Value = bytes/sec).</summary>
    public List<TopProcessSampleDto> TopDiskReadProcesses { get; init; } = [];
    /// <summary>Top disk-write processes at this sample (Value = bytes/sec).</summary>
    public List<TopProcessSampleDto> TopDiskWriteProcesses { get; init; } = [];

    /// <summary>
    /// GPU Engine counter fragments where the PID mapped to a fleet process (TUFLOW).
    /// Empty on older agents or when TUFLOW is not running.
    /// </summary>
    public List<GpuEngineSightingDto> GpuEngineSightings { get; init; } = [];

    /// <summary>
    /// Agent sample cadence used for this snapshot (10 when TUFLOW present, else ~30).
    /// Null on older agents — ingest assumes normal fleet interval.
    /// </summary>
    public int? SampleIntervalSeconds { get; init; }

    /// <summary>Count of local TUFLOW-like processes (null on older agents).</summary>
    public int? TuflowInstanceCount { get; init; }

    /// <summary>
    /// Estimated HPC seats from local process args (-nt / GPU / HPC binary). Not CodeMeter truth.
    /// </summary>
    public int? ClaimedHpcSeats { get; init; }

    /// <summary>Estimated Classic seats (typically 1 per Classic process). Not CodeMeter truth.</summary>
    public int? ClaimedClassicSeats { get; init; }

    /// <summary>Short evidence string for tooltips (process#-ntN; …). Truncated by agent.</summary>
    public string? TuflowClaimDetail { get; init; }
}

/// <summary>Active / Idle thresholds applied when TUFLOW is running (POC defaults).</summary>
public static class FleetActiveThresholds
{
    public const double GpuPercentMin = 5.0;
    public const double CpuPercentMin = 10.0;
    public const double DiskReadMBpsMin = 5.0;
    public const double DiskWriteMBpsMin = 5.0;

    public static bool ComputeIsActive(
        bool tuflowRunning,
        double? cpuPercent,
        double? gpuPercent,
        double? diskReadMBps,
        double? diskWriteMBps)
    {
        if (!tuflowRunning)
            return false;

        if (gpuPercent is > GpuPercentMin) return true;
        if (cpuPercent is > CpuPercentMin) return true;
        if (diskReadMBps is > DiskReadMBpsMin) return true;
        if (diskWriteMBps is > DiskWriteMBpsMin) return true;
        return false;
    }
}
