# Patch: Heimdall.Shared/Contracts/RemoteMachineCommands.cs

Add one constant alongside the existing `RestartTermService` token. This is
the zero-payload command used for graceful stop — no separate DTO needed
since one machine tracks at most one active run (the agent resolves *which*
run to stop from its own local run-pointer, see TuflowRunHelper.cs).

```diff
 public static class RemoteMachineCommands
 {
     public const string RestartTermService = "RestartTermService";
+
+    /// <summary>
+    /// Zero-payload graceful-stop token for the TUFLOW run the agent is currently tracking.
+    /// See Heimdall.Agent.Collectors.TuflowRunHelper.TryExecuteCommand and TuflowLauncher's
+    /// stop.request-file / CTRL_BREAK_EVENT mechanism.
+    /// </summary>
+    public const string TuflowStopGraceful = "TuflowStopGraceful";
 }
```

Nothing else in this file changes — `CommandExecutionReportDto` and
`RestartRdsPhases` are reused as-is (TUFLOW stop/start don't need their own
phase enum since QueueStopGracefulAsync tracks state via
`TuflowRunStatusDto.State` directly instead).
