namespace Heimdall.Shared.Contracts;

/// <summary>Fixed local volume free/used snapshot from the agent (Win32_LogicalDisk DriveType=3).</summary>
public sealed class DiskVolumeDto
{
    /// <summary>Drive letter root, e.g. <c>C:</c>.</summary>
    public required string Name { get; init; }

    public string? Label { get; init; }

    public double TotalGb { get; init; }

    public double FreeGb { get; init; }

    public double UsedGb => Math.Max(0, TotalGb - FreeGb);

    public double UsedPct => TotalGb <= 0 ? 0 : Math.Clamp(UsedGb / TotalGb * 100.0, 0, 100);
}
