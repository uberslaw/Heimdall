# Patch: Heimdall.Api/Services/IngestService.cs

This one file holds both `IngestService` and `ConfigService` (confirmed by
reading it) — three separate edits, all in this file.

## 1. `IngestService` constructor — add the new service dependency

```diff
-public class IngestService(HeimdallDbContext db, AppListService appLists, ProcessCatalogService catalog, IConfiguration configuration, RemoteMachineService remoteMachines)
+public class IngestService(HeimdallDbContext db, AppListService appLists, ProcessCatalogService catalog, IConfiguration configuration, RemoteMachineService remoteMachines, TuflowRunService tuflowRuns)
```

## 2. `UpsertMachineAsync` — fold in the TUFLOW heartbeat alongside the existing remote-machine one

Current code (inside `private async Task<(Machine, bool, bool)> UpsertMachineAsync(...)`):

```diff
         ApplyIdentityFromHeartbeat(machine, heartbeat, isNew);
         ApplyHardwareFromHeartbeat(machine, heartbeat);
         var verifyRestartRdp = remoteMachines.ApplyHeartbeat(machine, heartbeat);
+        await tuflowRuns.ApplyHeartbeatAsync(machine, heartbeat, ct);
 
         return (machine, isNew, verifyRestartRdp);
```

`UpsertMachineAsync` already takes `CancellationToken ct` as a parameter
(confirmed reading the real method signature), so `ct` is in scope here.

Note: `TuflowRunService.ApplyHeartbeatAsync` is `async Task`, not the `void`
it started as — it now also upserts that run's `TuflowRunRecord` history
row (needs a DB round trip to find the row by `RunId`), so this call needs
the `await` shown above. `RemoteMachineService.ApplyHeartbeat` alongside it
stays synchronous/unchanged; only the TUFLOW one grew a DB lookup.
`db.SaveChangesAsync(ct)` a few lines down in `IngestAsync` still persists
everything from this call (both the `Machine` field mutations and the new/
updated `TuflowRunRecord`) in the same batch — no extra `SaveChangesAsync`
needed inside `ApplyHeartbeatAsync` itself.

## 3. `ConfigService.ResolveForHostAsync` — populate `PendingTuflowStart`

Add one line to the `AgentConfigDto` object being returned, next to
`PendingCommands`:

```diff
             PendingAppAnalysis = machine?.PendingAppAnalysis == true,
             PendingCommands = RemoteMachineService.DeserializeCommands(machine?.PendingCommandsJson),
+            PendingTuflowStart = TuflowRunService.DeserializeStartRequest(machine?.PendingTuflowStartJson),
             FleetSamplingEnabled = fleetSamplingEnabled,
             FleetProcessNames = ["tuflow"]
         };
```

`ConfigService` doesn't currently take a `TuflowRunService` constructor
dependency and doesn't need one — `DeserializeStartRequest` is `internal
static` on `TuflowRunService`, called the same way
`RemoteMachineService.DeserializeCommands` already is a few lines above.
