namespace Heimdall.Api.Services;

/// <summary>Daily Spec presence reconcile + stale network alerts (12 months).</summary>
public sealed class SpecReviewHostedService(
    IServiceScopeFactory scopeFactory,
    IConfiguration configuration,
    ILogger<SpecReviewHostedService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var section = configuration.GetSection("Heimdall:SpecReview");
        if (!section.GetValue("Enabled", true))
        {
            logger.LogInformation("Spec review hosted service is disabled.");
            return;
        }

        var initialDelaySeconds = Math.Max(0, section.GetValue("InitialDelaySeconds", 120));
        var runHourUtc = Math.Clamp(section.GetValue("RunHourUtc", 1), 0, 23);
        var runOnStartup = section.GetValue("RunOnStartup", false);

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

        if (runOnStartup && !stoppingToken.IsCancellationRequested)
            await RunOnceAsync(stoppingToken);

        while (!stoppingToken.IsCancellationRequested)
        {
            var delay = DelayUntilNextRun(runHourUtc);
            logger.LogInformation("Spec review reconcile: next run in {Delay} (daily at {Hour:00}:00 UTC).", delay, runHourUtc);
            try
            {
                await Task.Delay(delay, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }

            await RunOnceAsync(stoppingToken);
        }
    }

    private async Task RunOnceAsync(CancellationToken ct)
    {
        try
        {
            await using var scope = scopeFactory.CreateAsyncScope();
            var spec = scope.ServiceProvider.GetRequiredService<SpecReviewService>();
            await spec.EnsureArchiveListAsync(ct);
            var summary = await spec.ReconcilePresenceAsync(ct);
            logger.LogInformation("{Summary}", summary);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Spec review presence reconcile failed.");
        }
    }

    private static TimeSpan DelayUntilNextRun(int runHourUtc)
    {
        var now = DateTimeOffset.UtcNow;
        var next = new DateTimeOffset(now.Year, now.Month, now.Day, runHourUtc, 0, 0, TimeSpan.Zero);
        if (next <= now)
            next = next.AddDays(1);
        return next - now;
    }
}
