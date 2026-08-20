using Microsoft.Extensions.Options;

namespace Heimdall.Api.Services;

/// <summary>
/// Polls CodeMeter on a fixed interval, or sooner when <see cref="CodeMeterLicenseHub.RequestPollSoon"/> fires
/// (Flood TuflowRunning flip / Heimdall TUFLOW start-stop). Skips if a poll is already running.
/// </summary>
public sealed class CodeMeterPollHostedService(
    IServiceScopeFactory scopeFactory,
    CodeMeterLicenseHub hub,
    IOptions<CodeMeterOptions> options,
    ILogger<CodeMeterPollHostedService> logger) : BackgroundService
{
    private int _running;
    private DateTimeOffset _lastPollStartedUtc = DateTimeOffset.MinValue;

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
        var eventMinGap = TimeSpan.FromSeconds(Math.Clamp(opts.EventPollMinSeconds, 5, 120));

        while (!stoppingToken.IsCancellationRequested)
        {
            var nudged = hub.TryConsumeNudge(out var nudgeReason);
            if (nudged)
                hub.ArmNudgeWait();

            var dueForInterval = DateTimeOffset.UtcNow - _lastPollStartedUtc >= interval;
            var canEventPoll = DateTimeOffset.UtcNow - _lastPollStartedUtc >= eventMinGap;
            var shouldPoll = dueForInterval || (nudged && canEventPoll);

            if (nudged && !canEventPoll)
            {
                // Re-queue so we poll as soon as the min gap elapses.
                hub.RequestPollSoon(nudgeReason ?? "event");
                logger.LogDebug(
                    "CodeMeter event nudge deferred ({Reason}); last poll {Ago:0}s ago (min gap {Gap}s).",
                    nudgeReason,
                    (DateTimeOffset.UtcNow - _lastPollStartedUtc).TotalSeconds,
                    eventMinGap.TotalSeconds);
            }

            if (shouldPoll)
            {
                if (Interlocked.CompareExchange(ref _running, 1, 0) == 0)
                {
                    _lastPollStartedUtc = DateTimeOffset.UtcNow;
                    var reason = nudged && !dueForInterval ? (nudgeReason ?? "event") : "interval";
                    _ = PollOnceAsync(reason, stoppingToken);
                }
                else
                {
                    if (nudged)
                        hub.RequestPollSoon(nudgeReason ?? "event");
                    logger.LogDebug("CodeMeter poll still running; skipping this tick.");
                }
            }

            try
            {
                var remainingToInterval = interval - (DateTimeOffset.UtcNow - _lastPollStartedUtc);
                if (remainingToInterval < TimeSpan.FromMilliseconds(200))
                    remainingToInterval = TimeSpan.FromMilliseconds(200);
                if (remainingToInterval > interval)
                    remainingToInterval = interval;

                var delayTask = Task.Delay(remainingToInterval, stoppingToken);
                var nudgeTask = hub.WaitNudgeAsync(stoppingToken);
                await Task.WhenAny(delayTask, nudgeTask);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
        }
    }

    private async Task PollOnceAsync(string reason, CancellationToken ct)
    {
        try
        {
            await using var scope = scopeFactory.CreateAsyncScope();
            var query = scope.ServiceProvider.GetRequiredService<CodeMeterQueryService>();
            var snap = await query.QueryAsync(ct);
            hub.Publish(snap);
            logger.LogInformation(
                "CodeMeter poll ({Reason}) done in {Ms:0}ms: HPC {HpcUsed}/{HpcTotal}, Classic {ClassicUsed}/{ClassicTotal}, partial={Partial}",
                reason,
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
            logger.LogWarning(ex, "CodeMeter poll failed ({Reason})", reason);
        }
        finally
        {
            Interlocked.Exchange(ref _running, 0);
        }
    }
}
