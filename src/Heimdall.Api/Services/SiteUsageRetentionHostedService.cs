using Heimdall.Api.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Heimdall.Api.Services;

/// <summary>Periodic purge of SiteUsageEvents older than Heimdall:UsageAnalytics:RetentionDays (live + sandbox).</summary>
public sealed class SiteUsageRetentionHostedService(
    IConfiguration configuration,
    IOptions<UsageAnalyticsOptions> options,
    ILogger<SiteUsageRetentionHostedService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var opts = options.Value;
        if (!opts.Enabled || !opts.RetentionEnabled)
        {
            logger.LogInformation("Site usage retention purge is disabled.");
            return;
        }

        var retentionDays = Math.Max(1, opts.RetentionDays);
        var initialDelaySeconds = Math.Max(0, opts.RetentionInitialDelaySeconds);
        var intervalHours = Math.Max(1, opts.RetentionIntervalHours);

        if (initialDelaySeconds > 0)
        {
            logger.LogInformation(
                "Site usage retention: waiting {Seconds}s after startup (keep {Days}d).",
                initialDelaySeconds, retentionDays);
            try
            {
                await Task.Delay(TimeSpan.FromSeconds(initialDelaySeconds), stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
        }

        if (opts.RetentionRunOnStartup && !stoppingToken.IsCancellationRequested)
            await RunOnceAsync(retentionDays, stoppingToken);

        var interval = TimeSpan.FromHours(intervalHours);
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(interval, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }

            await RunOnceAsync(retentionDays, stoppingToken);
        }
    }

    private async Task RunOnceAsync(int retentionDays, CancellationToken ct)
    {
        try
        {
            var cutoff = DateTimeOffset.UtcNow.AddDays(-retentionDays);
            // SQLite + DateTimeOffset cannot translate ExecuteDelete; use parameterized SQL.
            // Values are stored as text like "2026-08-21 00:00:34.0694944+00:00" (lexical ISO-sortable).
            var cutoffText = cutoff.ToOffset(TimeSpan.Zero)
                .ToString("yyyy-MM-dd HH:mm:ss.fffffffzzz", System.Globalization.CultureInfo.InvariantCulture);
            var total = 0;
            foreach (var mode in new[] { HeimdallDatabaseMode.Live, HeimdallDatabaseMode.Sandbox })
            {
                var conn = HeimdallDatabaseMode.GetConnectionStringForMode(configuration, mode);
                var optionsBuilder = new DbContextOptionsBuilder<HeimdallDbContext>();
                optionsBuilder.UseSqlite(conn);
                await using var db = new HeimdallDbContext(optionsBuilder.Options);
                var deleted = await db.Database.ExecuteSqlRawAsync(
                    """DELETE FROM "SiteUsageEvents" WHERE "OccurredUtc" < {0}""",
                    [cutoffText],
                    ct);
                total += deleted;
            }

            logger.LogInformation(
                "Site usage retention purge complete: deleted {Deleted} rows older than {Days} days.",
                total, retentionDays);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // shutting down
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Site usage retention purge failed.");
        }
    }
}
