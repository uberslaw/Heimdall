namespace Heimdall.Api.Services;

using Heimdall.Shared.Contracts;

/// <summary>
/// Periodic purge of FleetMetricSnapshots older than the configured retention window
/// (default 90 days — matches Help / FleetDashboardService.RetentionDaysDefault).
/// Also purges ended TuflowBehaviourRuns past Heimdall:TuflowBehaviour:SampleRetentionDays.
/// </summary>
public sealed class FleetSnapshotRetentionHostedService(
    IServiceScopeFactory scopeFactory,
    IConfiguration configuration,
    ILogger<FleetSnapshotRetentionHostedService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var section = configuration.GetSection("Heimdall:FleetSnapshotRetention");
        if (!section.GetValue("Enabled", true))
        {
            logger.LogInformation("Fleet snapshot retention purge is disabled.");
            return;
        }

        var retentionDays = Math.Max(1, section.GetValue("RetentionDays", FleetDashboardService.RetentionDaysDefault));
        var initialDelaySeconds = Math.Max(0, section.GetValue("InitialDelaySeconds", 90));
        var intervalHours = Math.Max(1, section.GetValue("IntervalHours", 24));
        var runOnStartup = section.GetValue("RunOnStartup", true);

        if (initialDelaySeconds > 0)
        {
            logger.LogInformation(
                "Fleet snapshot retention: waiting {Seconds}s after startup (keep {Days}d).",
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

        if (runOnStartup && !stoppingToken.IsCancellationRequested)
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
            await using var scope = scopeFactory.CreateAsyncScope();
            var fleet = scope.ServiceProvider.GetRequiredService<FleetDashboardService>();
            var deleted = await fleet.PurgeSnapshotsOlderThanAsync(retentionDays, ct);
            logger.LogInformation(
                "Fleet snapshot retention purge complete: deleted {Deleted} rows older than {Days} days.",
                deleted, retentionDays);

            var behaviour = scope.ServiceProvider.GetRequiredService<TuflowBehaviourService>();
            var behaviourSection = configuration.GetSection("Heimdall:TuflowBehaviour");
            var behaviourDays = Math.Max(1, behaviourSection.GetValue(
                "SampleRetentionDays", TuflowBehaviourDefaults.SampleRetentionDays));
            var behaviourDeleted = await behaviour.PurgeOlderThanAsync(behaviourDays, ct);
            if (behaviourDeleted > 0)
            {
                logger.LogInformation(
                    "TUFLOW behaviour retention purge: deleted {Deleted} runs older than {Days} days.",
                    behaviourDeleted, behaviourDays);
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // shutting down
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Fleet snapshot retention purge failed.");
        }
    }
}
