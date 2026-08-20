namespace Heimdall.Api.Services;

/// <summary>Latest CodeMeter poll snapshot + event-driven poll wake for Phase 2 nudges.</summary>
public sealed class CodeMeterLicenseHub
{
    private readonly object _gate = new();
    private CodeMeterLicenseSnapshot _latest = CodeMeterLicenseSnapshot.Disabled;
    private TaskCompletionSource _wake = NewWake();
    private int _pendingNudge;
    private string? _pendingReason;

    public CodeMeterLicenseSnapshot Latest
    {
        get { lock (_gate) return _latest; }
    }

    public void Publish(CodeMeterLicenseSnapshot snapshot)
    {
        lock (_gate) _latest = snapshot;
    }

    /// <summary>
    /// Ask the poller to run sooner than the normal interval (TUFLOW start/stop or TuflowRunning flip).
    /// Coalesces multiple requests until the poller observes them.
    /// </summary>
    public void RequestPollSoon(string reason)
    {
        lock (_gate)
        {
            _pendingNudge = 1;
            _pendingReason = string.IsNullOrWhiteSpace(reason) ? "event" : reason.Trim();
            _wake.TrySetResult();
        }
    }

    /// <summary>True if a nudge is waiting; clears the flag and returns the reason.</summary>
    public bool TryConsumeNudge(out string? reason)
    {
        lock (_gate)
        {
            if (_pendingNudge == 0)
            {
                reason = null;
                return false;
            }

            _pendingNudge = 0;
            reason = _pendingReason;
            _pendingReason = null;
            return true;
        }
    }

    /// <summary>Wait until <see cref="RequestPollSoon"/> fires or <paramref name="ct"/> cancels.</summary>
    public Task WaitNudgeAsync(CancellationToken ct)
    {
        Task wait;
        lock (_gate)
        {
            wait = _wake.Task;
        }

        return wait.WaitAsync(ct);
    }

    /// <summary>Call after handling a nudge so the next WaitNudgeAsync blocks again.</summary>
    public void ArmNudgeWait()
    {
        lock (_gate)
        {
            if (!_wake.Task.IsCompleted)
                return;
            _wake = NewWake();
        }
    }

    private static TaskCompletionSource NewWake() =>
        new(TaskCreationOptions.RunContinuationsAsynchronously);
}
