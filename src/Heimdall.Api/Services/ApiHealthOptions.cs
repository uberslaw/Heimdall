namespace Heimdall.Api.Services;

public sealed class ApiHealthOptions
{
    public const string SectionName = "Heimdall:ApiHealth";

    public bool Enabled { get; set; } = true;
    public int ProbeIntervalSeconds { get; set; } = 60;
    public int SampleRetentionDays { get; set; } = 30;
    public int IncidentRetentionDays { get; set; } = 90;
    public int InitialDelaySeconds { get; set; } = 15;
    /// <summary>Fleet snapshot gap longer than this (minutes) flags a machine on the dashboard.</summary>
    public int FleetGapAlertMinutes { get; set; } = 10;
    /// <summary>Lookback window for fleet / TUFLOW gap analysis.</summary>
    public int GapLookbackHours { get; set; } = 168;
}
