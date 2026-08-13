namespace Heimdall.Api.Services;

/// <summary>
/// Periodically marks Client Version rows Stuck when Applying/Downloading and heartbeats stop
/// (or progress goes stale). Phase 1 operator visibility — not auto-repair.
/// </summary>
public sealed class ClientUpdateStuckHostedService(
    IServiceScopeFactory scopeFactory,
    IConfiguration configuration,
    ILogger<ClientUpdateStuckHostedService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var section = configuration.GetSection("Heimdall:ClientUpdate");
        if (!section.GetValue("StuckDetectionEnabled", true))
        {
            logger.LogInformation("Client update stuck detection is disabled.");
            return;
        }

        var stuckMinutes = Math.Max(1, section.GetValue("ApplyingStuckMinutes", ClientUpdateService.DefaultApplyingStuckMinutes));
        var intervalMinutes = Math.Max(1, section.GetValue("StuckPollMinutes", 2));
        var initialDelaySeconds = Math.Max(0, section.GetValue("StuckInitialDelaySeconds", 60));

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

        var interval = TimeSpan.FromMinutes(intervalMinutes);
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await using var scope = scopeFactory.CreateAsyncScope();
                var updates = scope.ServiceProvider.GetRequiredService<ClientUpdateService>();
                var marked = await updates.MarkStuckApplyingAsync(stuckMinutes, stoppingToken);
                if (marked > 0)
                    logger.LogWarning("Marked {Count} machine(s) Client update Stuck (threshold {Minutes}m).", marked, stuckMinutes);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Client update stuck sweep failed.");
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
