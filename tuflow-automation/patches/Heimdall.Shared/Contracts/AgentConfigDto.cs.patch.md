# Patch: Heimdall.Shared/Contracts/AgentConfigDto.cs

Add one nullable property to `AgentConfigDto`, right after `PendingCommands`
(current file content confirmed by reading it — inserting exactly here to
sit next to the field it parallels):

```diff
     /// <summary>One-shot commands for the agent (e.g. RestartTermService). Cleared after heartbeat ack.</summary>
     public List<string> PendingCommands { get; init; } = [];
+
+    /// <summary>
+    /// A queued TUFLOW run request, if any. Unlike PendingCommands (bare string tokens) this needs a
+    /// real payload (exe/tcf paths, scenarios), so it's a first-class field rather than being squeezed
+    /// into the token list. Cleared server-side once the agent's heartbeat reports a TuflowRunStatusDto
+    /// with a matching RunId (see TuflowRunService.ApplyHeartbeat).
+    /// </summary>
+    public TuflowStartRequestDto? PendingTuflowStart { get; init; }
 
     /// <summary>When true, agent runs the always-on 30s fleet sampler (Historical Dashboard enrollment).</summary>
     public bool FleetSamplingEnabled { get; init; }
```

No other changes to this file.
