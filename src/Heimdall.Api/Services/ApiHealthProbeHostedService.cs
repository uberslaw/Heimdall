using System.Diagnostics;
using Heimdall.Api.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Heimdall.Api.Services;

/// <summary>
/// In-process health probes + retention purge for <see cref="ApiHealthService"/>.
/// </summary>
public sealed class ApiHealthProbeHostedService(
    IServiceScopeFactory scopeFactory,
    IOptions<ApiHealthOptions> options,
    ILogger<ApiHealthProbeHostedService> logger) : BackgroundService
{
    private DateTimeOffset _lastPurgeUtc = DateTimeOffset.MinValue;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var opts = options.Value;
        if (!opts.Enabled)
        {
            logger.LogInformation("API health probe is disabled.");
            return;
        }

        var delay = TimeSpan.FromSeconds(Math.Max(5, opts.InitialDelaySeconds));
        try
        {
            await Task.Delay(delay, stoppingToken);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            return;
        }

        await using (var scope = scopeFactory.CreateAsyncScope())
        {
            var health = scope.ServiceProvider.GetRequiredService<ApiHealthService>();
            try
            {
                await health.CloseOrphanIncidentsOnStartupAsync(stoppingToken);
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Could not close orphan API health incidents on startup");
            }
        }

        var interval = TimeSpan.FromSeconds(Math.Max(15, opts.ProbeIntervalSeconds));
        while (!stoppingToken.IsCancellationRequested)
        {
            await RunProbeOnceAsync(stoppingToken);
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

    private async Task RunProbeOnceAsync(CancellationToken ct)
    {
        try
        {
            await using var scope = scopeFactory.CreateAsyncScope();
            var db = scope.ServiceProvider.GetRequiredService<HeimdallDbContext>();
            var health = scope.ServiceProvider.GetRequiredService<ApiHealthService>();

            var sw = Stopwatch.StartNew();
            var ok = false;
            string? detail = null;
            try
            {
                await db.Database.ExecuteSqlRawAsync("SELECT 1;", ct);
                sw.Stop();
                ok = true;
            }
            catch (Exception ex)
            {
                sw.Stop();
                detail = ex.Message;
                logger.LogWarning(ex, "API health DB probe failed");
            }

            await health.RecordProbeAsync(ok, (int)sw.ElapsedMilliseconds, detail, ct);

            var now = DateTimeOffset.UtcNow;
            if (now - _lastPurgeUtc > TimeSpan.FromHours(24))
            {
                await health.PurgeOldAsync(ct);
                _lastPurgeUtc = now;
            }
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "API health probe cycle failed");
        }
    }
}
