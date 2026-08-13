namespace Heimdall.Api.Services;

/// <summary>
/// Polls on a configurable interval and, when due, queues fleet storage scans to enrolled agents
/// via <see cref="StorageScanService"/> (PendingDiskUsageScanJson).
/// </summary>
public sealed class StorageScanHostedService(
    IServiceScopeFactory scopeFactory,
    IConfiguration configuration,
    ILogger<StorageScanHostedService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var section = configuration.GetSection("Heimdall:StorageScan");
        if (!section.GetValue("Enabled", true))
        {
            logger.LogInformation("Weekly storage scan hosted service is disabled.");
            return;
        }

        var initialDelaySeconds = Math.Max(0, section.GetValue("InitialDelaySeconds", 90));
        var pollMinutes = Math.Max(1, section.GetValue("PollMinutes", 15));

        if (initialDelaySeconds > 0)
        {
            try
            {
                await Task.Delay(TimeSpan.FromSeconds(initialDelaySeconds), stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
        }

        var interval = TimeSpan.FromMinutes(pollMinutes);
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await using var scope = scopeFactory.CreateAsyncScope();
                var scans = scope.ServiceProvider.GetRequiredService<StorageScanService>();
                var queued = await scans.TryRunWeeklyIfDueAsync(stoppingToken);
                if (queued > 0)
                    logger.LogInformation("Weekly storage scan queued on {Count} machine(s).", queued);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Weekly storage scan sweep failed.");
            }

            try
            {
                await Task.Delay(interval, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
        }
    }
}
