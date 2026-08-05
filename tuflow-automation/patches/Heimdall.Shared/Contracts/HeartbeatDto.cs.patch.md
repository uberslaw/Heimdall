# Patch: Heimdall.Shared/Contracts/HeartbeatDto.cs

Add one nullable property, next to the other agent-reported status fields:

```diff
     /// <summary>TermService (Remote Desktop Services) status: Running, Stopped, Unknown, etc.</summary>
     public string? TermServiceStatus { get; init; }
+
+    /// <summary>
+    /// Current state of the TUFLOW run this agent is tracking (if any) — read from TuflowLauncher's
+    /// status.json each upload cycle. Null when no run has ever been started on this machine, or once
+    /// a finished run's pointer file has been cleared (see TuflowRunHelper.ReadCurrentStatus).
+    /// </summary>
+    public TuflowRunStatusDto? TuflowRunStatus { get; init; }
 
     /// <summary>Commands executed since last ingest; API clears matching PendingCommands.</summary>
     public List<string> AcknowledgedCommands { get; init; } = [];
```

No other changes to this file.
