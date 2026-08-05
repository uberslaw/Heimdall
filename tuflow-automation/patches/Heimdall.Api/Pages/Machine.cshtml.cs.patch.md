# Patch: Heimdall.Api/Pages/Machine.cshtml.cs

This is the real per-machine detail page (`/Machine?hostname=X`) — confirmed
by reading the full file. Adding a `TuflowRunService` dependency and one
property, loaded alongside everything else in `LoadAsync`.

## 1. Constructor — add the dependency

```diff
 public class MachineModel(
     StatsQueryService stats,
     AppListService appLists,
-    ConfigService config) : PageModel
+    ConfigService config,
+    TuflowRunService tuflowRuns) : PageModel
```

## 2. New property

Next to the other `Detail`/`AppListsView` properties:

```diff
     public MachineDetailSnapshot? Detail { get; private set; }
     public bool HostNotFound { get; private set; }
     public string RangeLabel { get; private set; } = "7 day";
     public int RangeDays { get; private set; } = 7;
+
+    /// <summary>Null-if-not-Flood-enrolled — the .cshtml hides the whole TUFLOW panel when this is null
+    /// or FloodEnrolled is false. See TuflowRunService.GetMachineViewAsync.</summary>
+    public TuflowMachineView? Tuflow { get; private set; }
```

## 3. `LoadAsync` — fetch it alongside the rest

```diff
         Detail = await stats.QueryMachineDetailAsync(host, fromUtc, toUtc, selectedApps, ct);
         HostNotFound = Detail is null;
         if (HostNotFound)
             return;

         AppListsView = await appLists.GetEffectiveForHostAsync(host, ct);
         AppListPicker = await appLists.ListForPickerAsync(ct);
         MachineExcludedProcesses = await config.GetMachineExcludeProcessesAsync(host, ct);
+        Tuflow = await tuflowRuns.GetMachineViewAsync(host, ct);
```

## 4. Two new static helpers, next to `FormatLocalTimestamp`

```diff
     public static string FormatLocalTimestamp(DateTimeOffset utc) =>
         RemoteMachineService.FormatAgentContact(utc);
+
+    public static string TuflowStateBadgeClass(string? state) => state switch
+    {
+        TuflowRunStates.Running => "badge-active",
+        TuflowRunStates.Starting or TuflowRunStates.StopRequested => "badge-ended",
+        TuflowRunStates.Completed or TuflowRunStates.Stopped => "badge-local",
+        TuflowRunStates.Failed => "badge-expired",
+        _ => "badge-ended"
+    };
+
+    /// <summary>"badge-expired" doesn't appear to be used elsewhere on this page (Machine.cshtml uses
+    /// badge-active/badge-ended/badge-local) — check it exists in your theme CSS before relying on it
+    /// for Failed; substitute another red/warning badge class if not (e.g. reuse badge-ended and rely
+    /// on the red inline error text below it instead — see Machine.cshtml patch).</summary>
+    public static string FormatDuration(DateTimeOffset? startedUtc, DateTimeOffset? endedUtc)
+    {
+        if (startedUtc is not DateTimeOffset start)
+            return "—";
+        var end = endedUtc ?? DateTimeOffset.UtcNow;
+        var span = end - start;
+        return span.TotalHours >= 1
+            ? $"{(int)span.TotalHours}h {span.Minutes}m"
+            : $"{(int)span.TotalMinutes}m {span.Seconds}s";
+    }
```

## `using` additions

`Machine.cshtml.cs` already has `using Heimdall.Api.Services;` and `using
Heimdall.Shared.Contracts;` — `TuflowRunService`, `TuflowMachineView`, and
`TuflowRunStates` all resolve through those, no new `using` needed.

## On `badge-expired` for Failed

I used `badge-active`/`badge-ended`/`badge-local` because I've directly
confirmed those three exist (they're used elsewhere on this exact page —
`Status` stat and the app-list scope pills). `badge-expired` is used
elsewhere in the codebase (`RemoteMachinesModel.TermServiceBadgeClass`,
`RdpBadgeClass`) for "bad/red" states, so it's very likely a real class too,
but I haven't seen it used on *this specific page*, so it's worth a quick
visual check rather than taking it on faith — if it's missing or looks
wrong, swap it for whatever red/danger badge class your theme actually
defines.
