# Patch: Heimdall.Api/Services/FleetDashboardService.cs

## Persist the TUFLOW-process figures `IngestSnapshotAsync` already computes but discards

Confirmed exact current text of `IngestSnapshotAsync` below (real file, line
~98). It already pulls `dto.ProcessCpuPercent` / `dto.ProcessGpuPercent` /
`dto.ProcessDiskReadMBps` / `dto.ProcessDiskWriteMBps` out of the incoming
`FleetSnapshotDto` to compute `isActive` — but the `FleetMetricSnapshot` it
saves only carries the whole-machine gauges below them, so the
process-specific numbers are thrown away right after being read. That's the
only thing this patch changes: four more assignments on the object
initializer, no new logic.

```diff
         db.FleetMetricSnapshots.Add(new FleetMetricSnapshot
         {
             SampledAtUtc = dto.SampledAtUtc == default ? DateTimeOffset.UtcNow : dto.SampledAtUtc,
             MachineId = machine.Id,
             Username = string.IsNullOrWhiteSpace(dto.Username) ? null : dto.Username.Trim(),
             TuflowRunning = dto.TuflowRunning,
             CpuPercent = dto.CpuPercent,
             GpuPercent = dto.GpuPercent,
             GpuMemoryUsedMb = dto.GpuMemoryUsedMb,
             RamUsedMb = dto.RamUsedMb,
             DiskReadMBps = dto.DiskReadMBps,
             DiskWriteMBps = dto.DiskWriteMBps,
             NetworkInMBps = dto.NetworkInMBps,
             NetworkOutMBps = dto.NetworkOutMBps,
+            ProcessCpuPercent = dto.ProcessCpuPercent,
+            ProcessGpuPercent = dto.ProcessGpuPercent,
+            ProcessDiskReadMBps = dto.ProcessDiskReadMBps,
+            ProcessDiskWriteMBps = dto.ProcessDiskWriteMBps,
             IsActive = isActive
         });
         await db.SaveChangesAsync(ct);
         return true;
```

Requires the `FleetMetricSnapshot` entity columns from
`Entities.cs.patch.md` section 6 and the schema patch from its section 7 —
apply those first or this won't compile / the columns won't exist yet.

## Why this file, not `TuflowRunService.cs`

`IngestSnapshotAsync` is the one and only place `FleetSnapshotDto` values
get written to a `FleetMetricSnapshot` row — it's owned by
`FleetDashboardService`, the same class that already reads these same four
`dto.Process*` fields for the `isActive` calculation two lines above. Adding
the persistence here (rather than, say, re-ingesting in
`TuflowRunService`) keeps "how a snapshot becomes a row" in one place.

## No change needed elsewhere in this file

`GetLiveFleetAsync`, `AggregateFleetAsync`/`AggregateMachineAsync`/
`AggregateInternal`, and `GetTimeSeriesAsync` are all untouched by this
patch — the new columns are additive and nothing here needs to read them.
`TuflowRunService.GetFleetProgressAsync` (new, see
`TuflowRunService.cs.patch.md` — actually the full new method appended
directly to the already-delivered `TuflowRunService.cs` in this package)
is what reads `ProcessCpuPercent` etc. back out, using the same
"`LoadSnapshotsForMachinesAsync`-style load-then-filter-in-memory" pattern
already established in this file for SQLite `DateTimeOffset` safety.
