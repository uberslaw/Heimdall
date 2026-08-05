# Patch: Heimdall.Api/Program.cs — DI registration

Next to the existing `RemoteMachineService` registration:

```diff
     builder.Services.AddScoped<RemoteMachineService>();
+    builder.Services.AddScoped<TuflowRunService>();
     builder.Services.AddScoped<RemoteAccessGroupService>();
```

(Exact line taken from the real Program.cs — `AddScoped<RemoteMachineService>()`
is registered at line 46 alongside the other per-request services.)

`TuflowRunService`'s constructor also takes `FleetDashboardService` (to scope
the machine list to Flood/Historical-Dashboard enrollment and read live
TuflowRunning status — see TuflowRunService.cs remarks). That's already
registered as `AddScoped<FleetDashboardService>()` at line 51 in the real
Program.cs; ASP.NET Core's DI container resolves constructor dependencies
lazily, so registration order between the two doesn't matter.

## Fast poll endpoint

Next to the existing `/api/config/{hostname}` and
`/api/resource-sampling/{hostname}/status` minimal-API endpoints (same file,
same `IsAuthorized(request)` gate defined a few lines above them):

```diff
 app.MapGet("/api/config/{hostname}", async (string hostname, ConfigService config, HttpRequest request) =>
 {
     if (!IsAuthorized(request))
         return Results.Unauthorized();

     var dto = await config.ResolveForHostAsync(hostname, request.HttpContext.RequestAborted);
     return Results.Ok(dto);
 });
+
+app.MapGet("/api/tuflow/{hostname}/pending", async (string hostname, TuflowRunService runs, HttpRequest request) =>
+{
+    if (!IsAuthorized(request))
+        return Results.Unauthorized();
+
+    var dto = await runs.GetPendingAsync(hostname, request.HttpContext.RequestAborted);
+    return Results.Ok(dto);
+});
```

This is deliberately separate from `/api/config/{hostname}` (which already
carries `AgentConfigDto.PendingTuflowStart` too) — the point of this second
endpoint is that it's cheap enough to poll far more often than the full
config resolution. See `TuflowRunService.GetPendingAsync` (a two-column
projection, no `ConfigService` pipeline, no `FleetDashboardService` join)
and `Worker.cs.patch.md`'s fast tick for why.
