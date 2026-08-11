namespace Heimdall.Shared.Contracts;

/// <summary>
/// Returned by GET /api/resource-sampling/{hostname}/status — polled frequently (~10s) by the agent,
/// independent of the slow ConfigRefreshSeconds cycle, so live sampling can start/stop promptly
/// when staff open/close the Staff Access page. See LiveSamplingService for the ref-counted fan-in.
/// </summary>
public sealed class ResourceSamplingStatusDto
{
    /// <summary>True when at least one active staff viewer needs live metrics for this host right now.</summary>
    public bool Active { get; init; }

    /// <summary>
    /// Process names favourited by any Remote Access Group this host belongs to. The agent guarantees
    /// these are included in the report (as FavoriteProcesses) even when they fall outside the top 3
    /// for every resource, so "track only favourites" can surface them on the Staff page.
    /// </summary>
    public List<string> FavoriteProcessNames { get; init; } = [];
}

/// <summary>One resource-sampling report from the agent for a single host (calibration average or steady-state reading).</summary>
public sealed class ResourceSampleReportDto
{
    public required string Hostname { get; init; }
    public DateTimeOffset SampledAtUtc { get; init; }

    /// <summary>True for the initial 1-sample/sec × 10s calibration average; false for steady-state 10s readings.</summary>
    public bool IsCalibrationAverage { get; init; }

    public double? CpuPercent { get; init; }
    /// <summary>Null when no GPU perf-counter category is available on the host (best-effort; degrades gracefully).</summary>
    public double? GpuPercent { get; init; }
    public double? RamPercent { get; init; }
    public double? RamUsedGb { get; init; }
    public double? RamTotalGb { get; init; }

    public double? DiskReadBytesPerSec { get; init; }
    public double? DiskWriteBytesPerSec { get; init; }
    /// <summary>Low / Med / High — see DiskActivityLevel.Classify.</summary>
    public string DiskReadLevel { get; init; } = DiskActivityLevel.Low;
    public string DiskWriteLevel { get; init; } = DiskActivityLevel.Low;

    public List<TopProcessSampleDto> TopCpuProcesses { get; init; } = [];
    public List<TopProcessSampleDto> TopGpuProcesses { get; init; } = [];
    public List<TopProcessSampleDto> TopRamProcesses { get; init; } = [];
    public List<TopProcessSampleDto> TopDiskReadProcesses { get; init; } = [];
    public List<TopProcessSampleDto> TopDiskWriteProcesses { get; init; } = [];

    /// <summary>Live values for favourited processes even when they rank outside the top 3.</summary>
    public List<FavoriteProcessSampleDto> FavoriteProcesses { get; init; } = [];
}

public sealed class TopProcessSampleDto
{
    public required string ProcessName { get; init; }
    public int ProcessId { get; init; }
    /// <summary>Meaning depends on the list: % for CPU/GPU, MB for RAM, bytes/sec for disk.</summary>
    public double Value { get; init; }
}

/// <summary>Body for /api/staff/groups/{groupId}/viewer/heartbeat and /leave — viewerId is a random per-tab id generated client-side (sessionStorage) so the same person opening two tabs counts as two viewers, and closing one does not stop sampling if the other is still open.</summary>
public sealed class ViewerHeartbeatRequestDto
{
    public required string ViewerId { get; init; }
}

public sealed class FavoriteProcessSampleDto
{
    public required string ProcessName { get; init; }
    public int ProcessId { get; init; }
    public double? CpuPercent { get; init; }
    public double? GpuPercent { get; init; }
    public double? RamMb { get; init; }
    public double? DiskReadBytesPerSec { get; init; }
    public double? DiskWriteBytesPerSec { get; init; }
}

/// <summary>
/// Disk activity classification — deliberately coarse (Low/Med/High) rather than raw bytes/sec, per product
/// requirement. Thresholds are on _Total PhysicalDisk throughput (all disks combined), documented here so the
/// bands are auditable/tunable in one place. These are POC defaults, not measured against real fleet data.
/// </summary>
public static class DiskActivityLevel
{
    public const string Low = "Low";
    public const string Med = "Med";
    public const string High = "High";

    /// <summary>Below this (MB/s) is Low — e.g. background sync, idle disk.</summary>
    public const double LowMaxMBps = 5.0;

    /// <summary>Below this (MB/s) is Med — e.g. app load, moderate file copy; at/above is High (sustained heavy I/O).</summary>
    public const double MedMaxMBps = 40.0;

    public static string Classify(double? bytesPerSec)
    {
        if (bytesPerSec is null || bytesPerSec.Value <= 0)
            return Low;

        var mbps = bytesPerSec.Value / (1024.0 * 1024.0);
        return ClassifyMBps(mbps);
    }

    /// <summary>Classify already-converted MB/s (e.g. fleet snapshots).</summary>
    public static string ClassifyMBps(double? mbps)
    {
        if (mbps is null || mbps.Value <= 0)
            return Low;
        if (mbps.Value < LowMaxMBps) return Low;
        if (mbps.Value < MedMaxMBps) return Med;
        return High;
    }

    public static string ClassifyCombinedMBps(double? readMBps, double? writeMBps) =>
        ClassifyMBps((readMBps ?? 0) + (writeMBps ?? 0));
}

/// <summary>
/// Network activity Low/Med/High — same coarse style as <see cref="DiskActivityLevel"/>.
/// There is no industry-standard L/M/H for workstation NIC utilisation (depends on link speed and workload);
/// these are POC absolute MB/s bands on combined send+receive, tunable in one place.
/// </summary>
public static class NetworkActivityLevel
{
    public const string Low = "Low";
    public const string Med = "Med";
    public const string High = "High";

    /// <summary>Below this combined MB/s is Low — idle / light sync on a typical office link.</summary>
    public const double LowMaxMBps = 2.0;

    /// <summary>Below this is Med; at/above is High (sustained transfers, large sync, etc.).</summary>
    public const double MedMaxMBps = 25.0;

    public static string ClassifyMBps(double? mbps)
    {
        if (mbps is null || mbps.Value <= 0)
            return Low;
        if (mbps.Value < LowMaxMBps) return Low;
        if (mbps.Value < MedMaxMBps) return Med;
        return High;
    }

    public static string ClassifyCombinedMBps(double? inMBps, double? outMBps) =>
        ClassifyMBps((inMBps ?? 0) + (outMBps ?? 0));
}
