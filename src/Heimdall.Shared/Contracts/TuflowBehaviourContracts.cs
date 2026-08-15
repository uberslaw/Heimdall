namespace Heimdall.Shared.Contracts;

/// <summary>
/// CPU-based TUFLOW run start/stop detection thresholds (fleet snapshot ingest).
/// Defaults: process CPU &gt; 20% for 2 consecutive samples = start; ≤ 20% (or gone) for 2 = stop.
/// </summary>
public static class TuflowBehaviourDefaults
{
    public const double CpuPercentThreshold = 20.0;
    public const int ConfirmIntervals = 2;
    public const int SampleRetentionDays = 180;
    public const int FastSampleSeconds = 10;
    public const int NormalFleetSampleSeconds = 30;
    /// <summary>How long a finished detection stays visible as "Stopped …" on the Active column.</summary>
    public const int RecentStopDisplayHours = 4;
}

/// <summary>TuflowBehaviourRun.State values.</summary>
public static class TuflowBehaviourStates
{
    /// <summary>Process seen; waiting for elevated-CPU confirmation (or process exit without a run).</summary>
    public const string Watching = "Watching";
    /// <summary>Confirmed elevated (run active).</summary>
    public const string Active = "Active";
    /// <summary>Confirmed stop (CPU low / process gone); may still be collecting idle-tail samples.</summary>
    public const string Ended = "Ended";
}

/// <summary>GPU Engine instance labels observed for tuflow.exe (engtype / phys from counter names).</summary>
public sealed class GpuEngineSightingDto
{
    /// <summary>Short label e.g. "phys0/engtype_3D" or raw instance fragment.</summary>
    public required string Label { get; init; }
    public double UtilizationPercent { get; init; }
}
