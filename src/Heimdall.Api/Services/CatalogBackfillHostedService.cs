namespace Heimdall.Api.Services;

/// <summary>
/// Daily idempotent catalog backfill: merges discovery keys (ProcessRuns, inventories, lists, assignments)
/// into ProcessCatalogEntries via <see cref="ProcessCatalogService.BackfillFromDiscoveriesAsync"/>.
/// Uses Upsert + blank-path / volatile-path duplicate coalescing — safe to re-run (no duplicate rows).
/// </summary>
public sealed class CatalogBackfillHostedService(
    IServiceScopeFactory scopeFactory,
    IConfiguration configuration,
    ILogger<CatalogBackfillHostedService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var section = configuration.GetSection("Heimdall:CatalogBackfill");
        if (!section.GetValue("Enabled", true))
        {
            logger.LogInformation("Catalog discovery backfill hosted service is disabled.");
            return;
        }

        var initialDelaySeconds = Math.Max(0, section.GetValue("InitialDelaySeconds", 60));
        var runHourUtc = Math.Clamp(section.GetValue("RunHourUtc", 0), 0, 23);
        var runOnStartup = section.GetValue("RunOnStartup", true);

        if (initialDelaySeconds > 0)
        {
            logger.LogInformation(
                "Catalog discovery backfill: waiting {Seconds}s after startup before scheduling.",
                initialDelaySeconds);
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
            logger.LogInformation(
                "Catalog discovery backfill: next run in {Delay} (daily at {Hour:00}:00 UTC).",
                delay, runHourUtc);
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
            var catalog = scope.ServiceProvider.GetRequiredService<ProcessCatalogService>();
            var result = await catalog.BackfillFromDiscoveriesAsync(ct);
            // NewCount = newly inserted catalog rows; UpdatedCount includes refreshed rows + merged duplicates.
            logger.LogInformation(
                "Catalog discovery backfill complete: {Added} added, {UpdatedOrMerged} updated/merged (duplicates coalesced by Upsert).",
                result.NewCount,
                result.UpdatedCount);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // shutting down
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Catalog discovery backfill failed.");
        }
    }

    /// <summary>Wait until the next occurrence of <paramref name="runHourUtc"/>:00 UTC (at least 1 minute ahead).</summary>
    internal static TimeSpan DelayUntilNextRun(int runHourUtc, DateTimeOffset? utcNow = null)
    {
        var now = utcNow ?? DateTimeOffset.UtcNow;
        var todayAt = new DateTimeOffset(now.Year, now.Month, now.Day, runHourUtc, 0, 0, TimeSpan.Zero);
        var next = todayAt <= now.AddMinutes(1) ? todayAt.AddDays(1) : todayAt;
        return next - now;
    }
}
