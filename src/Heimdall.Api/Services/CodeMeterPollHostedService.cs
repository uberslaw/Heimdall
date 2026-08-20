using Microsoft.Extensions.Options;

namespace Heimdall.Api.Services;

/// <summary>
/// Polls CodeMeter license servers on a fixed interval. Skips a tick if the previous poll is still running.
/// </summary>
public sealed class CodeMeterPollHostedService(
    IServiceScopeFactory scopeFactory,
    CodeMeterLicenseHub hub,
    IOptions<CodeMeterOptions> options,
    ILogger<CodeMeterPollHostedService> logger) : BackgroundService
{
    private int _running;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var opts = options.Value;
        if (!opts.Enabled)
        {
            logger.LogInformation("CodeMeter license poller is disabled (Heimdall:CodeMeter:Enabled=false).");
            hub.Publish(CodeMeterLicenseSnapshot.Disabled);
            return;
        }

        var initial = Math.Max(0, opts.InitialDelaySeconds);
        if (initial > 0)
        {
            try
            {
                await Task.Delay(TimeSpan.FromSeconds(initial), stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
        }

        var interval = TimeSpan.FromSeconds(Math.Clamp(opts.PollSeconds, 15, 600));
        while (!stoppingToken.IsCancellationRequested)
        {
            if (Interlocked.CompareExchange(ref _running, 1, 0) == 0)
            {
                _ = PollOnceAsync(stoppingToken);
            }
            else
            {
                logger.LogDebug("CodeMeter poll still running; skipping this tick.");
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

    private async Task PollOnceAsync(CancellationToken ct)
    {
        try
        {
            await using var scope = scopeFactory.CreateAsyncScope();
            var query = scope.ServiceProvider.GetRequiredService<CodeMeterQueryService>();
            var snap = await query.QueryAsync(ct);
            hub.Publish(snap);
            logger.LogInformation(
                "CodeMeter poll done in {Ms:0}ms: HPC {HpcUsed}/{HpcTotal}, Classic {ClassicUsed}/{ClassicTotal}, partial={Partial}",
                snap.PollDurationMs,
                snap.Hpc.PoolUsed?.ToString() ?? "—",
                snap.Hpc.TotalLicenses,
                snap.Classic.PoolUsed?.ToString() ?? "—",
                snap.Classic.TotalLicenses,
                snap.Partial);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // shutting down
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "CodeMeter poll failed");
        }
        finally
        {
            Interlocked.Exchange(ref _running, 0);
        }
    }
}
