namespace Heimdall.Api.Services;

/// <summary>First-party site usage analytics (Admin → Usage). Bound from Heimdall:UsageAnalytics.</summary>
public sealed class UsageAnalyticsOptions
{
    public const string SectionName = "Heimdall:UsageAnalytics";

    public bool Enabled { get; set; } = true;

    /// <summary>When true, store client IP on page views (and beacon events that omit one).</summary>
    public bool LogClientIp { get; set; } = true;

    /// <summary>Delete SiteUsageEvents older than this many days (hosted prune).</summary>
    public int RetentionDays { get; set; } = 90;

    public bool RetentionEnabled { get; set; } = true;

    public bool RetentionRunOnStartup { get; set; } = true;

    public int RetentionInitialDelaySeconds { get; set; } = 120;

    public int RetentionIntervalHours { get; set; } = 24;

    /// <summary>Max beacon events accepted per client IP per minute.</summary>
    public int BeaconMaxEventsPerMinute { get; set; } = 120;

    /// <summary>Max JSON body bytes for /api/usage/beacon.</summary>
    public int BeaconMaxBodyBytes { get; set; } = 16_384;

    /// <summary>Client heartbeat interval hint (seconds) — written into layout for the usage script.</summary>
    public int HeartbeatSeconds { get; set; } = 30;
}
