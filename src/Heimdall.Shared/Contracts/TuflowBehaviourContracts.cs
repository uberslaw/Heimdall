namespace Heimdall.Shared.Contracts;

/// <summary>
/// TUFLOW run start/stop detection thresholds (fleet snapshot ingest).
/// Defaults: process CPU &gt; 20% or process/system GPU &gt; 5% for 2 consecutive samples = start;
/// both below those bars (or process gone) for 2 = stop. GPU covers GPU-bound TUFLOW that sits
/// under the CPU threshold while still busy (Fleet Live Active column).
/// </summary>
public static class TuflowBehaviourDefaults
{
    public const double CpuPercentThreshold = 20.0;
    /// <summary>Aligns with <see cref="FleetActiveThresholds.GpuPercentMin"/> so GPU-busy runs stay Active.</summary>
    public const double GpuPercentThreshold = 5.0;
    public const int ConfirmIntervals = 2;
    public const int SampleRetentionDays = 180;
    public const int FastSampleSeconds = 10;
    public const int NormalFleetSampleSeconds = 30;
    /// <summary>
    /// Legacy: stop stamps on Live no longer expire by age (they stay until the next run starts).
    /// Kept so existing appsettings keys still bind without error.
    /// </summary>
    public const int RecentStopDisplayHours = 4;
}

/// <summary>TuflowBehaviourRun.State values.</summary>
public static class TuflowBehaviourStates
{
    /// <summary>Process seen; waiting for elevated CPU/GPU confirmation (or process exit without a run).</summary>
    public const string Watching = "Watching";
    /// <summary>Confirmed elevated (run active).</summary>
    public const string Active = "Active";
    /// <summary>Confirmed stop (CPU+GPU low / process gone); may still be collecting idle-tail samples.</summary>
    public const string Ended = "Ended";
}

/// <summary>GPU Engine instance labels observed for tuflow.exe (engtype / phys from counter names).</summary>
public sealed class GpuEngineSightingDto
{
    /// <summary>Short label e.g. "phys0/engtype_3D" or raw instance fragment.</summary>
    public required string Label { get; init; }
    public double UtilizationPercent { get; init; }
}
