# Patch: Heimdall.Agent/Worker.cs

Six small edits to the real 587-line file (exact current text confirmed by
reading it — quoting enough context on each to locate it unambiguously).
Edits 1-4 are the base start/stop wiring; 5-6 add the fast ~20s poll so
launch/stop signals don't wait for the full `ConfigRefreshSeconds` cycle
(default 300s) — see the "Why a separate fast poll" note at the bottom.

## 1. Config-refresh block — start a new run when one is pending

Current code in `ExecuteAsync`:

```diff
                 if (remote is not null)
                 {
                     _config = remote;
                     if (remote.PendingAppAnalysis)
                         _sendInventoryNextUpload = true;
                     ProcessPendingCommands(remote.PendingCommands);
+                    TuflowRunHelper.TryStartIfRequested(remote.PendingTuflowStart, logger);
                     logger.LogInformation("Config refreshed v{Version}; tracking {Count} processes{Inventory}{Commands}",
```

This is deliberately alongside `ProcessPendingCommands`, not inside it —
`PendingTuflowStart` carries a real payload (paths, scenarios), unlike the
bare string tokens `ProcessPendingCommands` handles, so it doesn't fit that
method's `IReadOnlyList<string> commands` signature. `TryStartIfRequested`
is itself idempotent (see TuflowRunHelper.cs — no-ops if this RunId is
already tracked), so calling it every refresh cycle is safe.

## 2. `ProcessPendingCommands` — try TuflowRunHelper for commands TermServiceHelper doesn't recognise

Current code:

```diff
             if (TermServiceHelper.TryExecuteCommand(command, logger, out var detail))
             {
                 _executedPendingCommands.Add(command);
                 lock (_commandsToAck)
                 {
                     if (!_commandsToAck.Contains(command, StringComparer.OrdinalIgnoreCase))
                         _commandsToAck.Add(command);
                 }
                 RecordCommandReport(command, success: true, detail);
             }
             else
             {
-                logger.LogWarning("Pending command failed: {Command} — {Detail}", command, detail);
-                RecordCommandReport(command, success: false, detail);
+                if (TuflowRunHelper.TryExecuteCommand(command, logger, out detail))
+                {
+                    _executedPendingCommands.Add(command);
+                    lock (_commandsToAck)
+                    {
+                        if (!_commandsToAck.Contains(command, StringComparer.OrdinalIgnoreCase))
+                            _commandsToAck.Add(command);
+                    }
+                    RecordCommandReport(command, success: true, detail);
+                }
+                else
+                {
+                    logger.LogWarning("Pending command failed: {Command} — {Detail}", command, detail);
+                    RecordCommandReport(command, success: false, detail);
+                }
             }
```

This is the smallest change that keeps `TermServiceHelper` completely
untouched: unknown-to-it commands (starting with `TuflowStopGraceful`) fall
through to `TuflowRunHelper` before being logged as a genuine failure. If a
third command source ever gets added, this two-level if/else should become
a small ordered list of `(string prefix, Func<...> handler)` instead — not
worth the abstraction for two handlers today.

## 3. `FlushAsync` — include TUFLOW run status in the heartbeat

Current code building the `HeartbeatDto`:

```diff
                 PrimaryIpAddress = NetworkInfoHelper.TryGetPrimaryIPv4(),
                 TermServiceStatus = TermServiceHelper.GetStatus(),
+                TuflowRunStatus = TuflowRunHelper.ReadCurrentStatus(),
                 AcknowledgedCommands = acks,
                 CommandExecutionReports = reports
             },
```

Same cadence as `TermServiceHelper.GetStatus()` right above it — both are
read fresh on every upload cycle (`UploadIntervalSeconds`, default 60s).

## 4. `using` statement

`Worker.cs` already has `using Heimdall.Agent.Collectors;` (that's how it
reaches `TermServiceHelper` today) — `TuflowRunHelper` lives in the same
namespace, so no new `using` is needed.

## 5. New fields + main-loop call — fast poll tick

`Worker.cs` already runs several independent cadences inside one
`BackgroundService` loop (`_nextConfigRefresh`, `_nextSample`, `_nextUpload`,
`_nextResourceControlPoll`, `_nextFleetSample` — see the field block near
the top and the tick calls in `ExecuteAsync`). Add a fifth cadence for
TUFLOW the same way, right next to the Historical Dashboard fleet-sampling
fields (which are the closest existing precedent — an always-on tick,
unlike resource sampling's viewer-gated one):

```diff
     // Historical Dashboard fleet sampling — always-on while config says enrolled; independent of Staff live sampling.
     private DateTimeOffset _nextFleetSample = DateTimeOffset.MinValue;
     private static readonly TimeSpan FleetSampleInterval = TimeSpan.FromSeconds(30);
+
+    // Fast TUFLOW start/stop poll — independent of ConfigRefreshSeconds (default 300s) so a queued
+    // start or graceful-stop reaches the agent in ~20s instead of up to 5 minutes. Always-on, same as
+    // fleet sampling above (no "someone is viewing a page" gate the way live resource sampling has).
+    private DateTimeOffset _nextTuflowPoll = DateTimeOffset.MinValue;
+    private static readonly TimeSpan TuflowPollInterval = TimeSpan.FromSeconds(20);
```

And the call site, next to the other two ticks in `ExecuteAsync`'s main loop:

```diff
             await RunResourceSamplingTickAsync(hostname, now, stoppingToken);
             await RunFleetSamplingTickAsync(hostname, now, stoppingToken);
+            await RunTuflowPollTickAsync(hostname, now, stoppingToken);

             await Task.Delay(1000, stoppingToken);
```

(20s is a middle-of-the-road pick for the 15-30s range you asked about —
change `TuflowPollInterval` to taste. The main loop already ticks every 1s
via `Task.Delay(1000, ...)`, so anything down to a few seconds is cheap on
the agent side; the real cost is one small HTTP request to the Api per tick
per machine, which is what `GetPendingAsync`'s lightweight query keeps cheap
on that side too.)

## 6. New method — `RunTuflowPollTickAsync`

Mirrors `RunResourceSamplingTickAsync`'s shape (same file, a bit further
down) but simpler — no active/inactive toggle, just "is anything pending":

```csharp
private async Task RunTuflowPollTickAsync(string hostname, DateTimeOffset now, CancellationToken ct)
{
    if (now < _nextTuflowPoll)
        return;

    _nextTuflowPoll = now.Add(TuflowPollInterval);

    var pending = await api.GetTuflowPendingAsync(hostname, ct);
    if (pending is null)
        return; // transient failure — next tick (or the slower config-refresh path) will catch it

    if (pending.PendingTuflowStart is not null)
        TuflowRunHelper.TryStartIfRequested(pending.PendingTuflowStart, logger);

    if (pending.StopRequested)
        TryExecuteTuflowStopFastPath();
}

/// <summary>
/// Shares _executedPendingCommands/_commandsToAck with ProcessPendingCommands (edit 2) on purpose —
/// both this fast tick and the slower config-refresh path can see TuflowStopGraceful and race to
/// execute it; the shared dedupe set means whichever gets there first wins and the other is a no-op,
/// so running both is safe rather than something to guard against.
/// Failures are logged at Debug, not surfaced as a CommandExecutionReport, on this path specifically —
/// "no run tracked yet" is the expected state for most of this tick's life (nothing pending most of the
/// time), and ProcessPendingCommands' slower pass already produces a real failure report if it's still
/// unresolved next config refresh.
/// </summary>
private void TryExecuteTuflowStopFastPath()
{
    const string command = RemoteMachineCommands.TuflowStopGraceful;
    if (_executedPendingCommands.Contains(command))
        return;

    if (!TuflowRunHelper.TryExecuteCommand(command, logger, out var detail))
    {
        logger.LogDebug("TUFLOW stop not actioned yet on fast poll: {Detail}", detail);
        return;
    }

    _executedPendingCommands.Add(command);
    lock (_commandsToAck)
    {
        if (!_commandsToAck.Contains(command, StringComparer.OrdinalIgnoreCase))
            _commandsToAck.Add(command);
    }
    RecordCommandReport(command, success: true, detail);
}
```

## Why a separate fast poll instead of just lowering `ConfigRefreshSeconds`

`ConfigRefreshSeconds` also gates `_nextSample`/hardware refresh timing
indirectly and drives `ConfigService.ResolveForHostAsync` — a heavier query
(`TrackingConfigs`, `KnownApps`, `AppListAssignments`, `ProcessPauses`,
`MetricPolicies`, `FleetDashboardMachines`) than a single Machine row.
Lowering the global default to 20s to speed up TUFLOW specifically would run
that whole pipeline for every machine, for every feature, four times a
minute — probably fine for a handful of machines, but scales worse than a
dedicated cheap endpoint as your Flood fleet grows. This mirrors exactly why
`GetResourceSamplingStatusAsync`/`_nextResourceControlPoll` already exists
as its own 10s cadence separate from config refresh — same tradeoff,
same answer, already precedented in your codebase.

I considered a genuinely separate OS process (closer to "tuflowcheck
subprocess") instead of another tick in this same loop, since
`TuflowRunHelper`'s state is already file-based (the run pointer + launcher
status.json), so a second process wouldn't need IPC with the Agent to read
it. Went with the in-loop tick instead because every other fast/independent
poll in this codebase (resource sampling, fleet sampling) already uses this
pattern, it reuses the existing authenticated `HttpClient`/`ApplyKey()`
instead of standing up a second one, and there's no real gap in coverage a
separate process would close here — a hung `Worker` loop would stop the
config-refresh path too, so a second process only helps if you specifically
want TUFLOW polling to survive an Agent redeploy/restart independently,
which isn't a requirement you've mentioned. Worth revisiting if that
changes.
