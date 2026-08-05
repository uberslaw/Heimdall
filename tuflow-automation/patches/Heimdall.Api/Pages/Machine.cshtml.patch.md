# Patch: Heimdall.Api/Pages/Machine.cshtml

Insert a new panel right after the top stat grid (Group/Status/Last
check-in/Last user) and before the "App lists" panel — confirmed exact
insertion point below (the `</div>` closes the first `hd-grid`, and
`@if (appLists is not null)` is the very next thing in the real file):

```diff
         <div class="hd-stat">
             <div class="label">@shortLabel utilisation</div>
             <div class="value">@d.UtilisationPct.ToString("0")%</div>
         </div>
     </div>
 
+    @if (Model.Tuflow is { FloodEnrolled: true } tuflow)
+    {
+        <div class="hd-panel mb-3">
+            <h2 class="hd-section-title">TUFLOW</h2>
+
+            @if (tuflow.Current is { } cur)
+            {
+                @if (!string.IsNullOrEmpty(cur.RunName))
+                {
+                    <p class="text-secondary small mb-2" style="font-size:1.05rem;font-weight:600;">@cur.RunName</p>
+                }
+                <div class="hd-grid mb-3">
+                    <div class="hd-stat">
+                        <div class="label">Status</div>
+                        <div class="value" style="font-size:1.1rem;">
+                            <span class="badge-pill @MachineModel.TuflowStateBadgeClass(cur.State)">@cur.State</span>
+                        </div>
+                    </div>
+                    @if (cur.PercentComplete is double pct)
+                    {
+                        <div class="hd-stat">
+                            <div class="label">Progress</div>
+                            <div class="value">@pct.ToString("0.#")%</div>
+                        </div>
+                    }
+                    @if (cur.SimulationTimeHours is double simT)
+                    {
+                        <div class="hd-stat">
+                            <div class="label">Simulated time</div>
+                            <div class="value" style="font-size:1rem;margin-top:0.35rem">
+                                @simT.ToString("0.##")h@(cur.SimulationEndTimeHours is double simEnd ? $" / {simEnd.ToString("0.##")}h" : "")
+                            </div>
+                        </div>
+                    }
+                    @if (cur.ClockTimeRemainingHours is double remain)
+                    {
+                        <div class="hd-stat">
+                            <div class="label">Est. remaining</div>
+                            <div class="value" style="font-size:1rem;margin-top:0.35rem">@remain.ToString("0.#")h</div>
+                        </div>
+                    }
+                    @if (cur.WarningCount is int warn)
+                    {
+                        <div class="hd-stat">
+                            <div class="label">Warnings</div>
+                            <div class="value">@warn</div>
+                        </div>
+                    }
+                    @if (cur.MassErrorPercent is double mErr)
+                    {
+                        <div class="hd-stat">
+                            <div class="label">Mass error</div>
+                            <div class="value">@mErr.ToString("0.00")%</div>
+                        </div>
+                    }
+                </div>
+                <p class="text-secondary small mb-0">
+                    <span title="@cur.TcfPath">@System.IO.Path.GetFileName(cur.TcfPath)</span>
+                    @if (!string.IsNullOrEmpty(cur.LastCheckpointFile))
+                    {
+                        <br /><span>Last checkpoint: @cur.LastCheckpointFile</span>
+                    }
+                    @if (!string.IsNullOrEmpty(cur.ErrorSummary))
+                    {
+                        <br /><span style="color:#f0a0a0;">@cur.ErrorSummary</span>
+                    }
+                </p>
+            }
+            else
+            {
+                <p class="text-secondary mb-0">No TUFLOW run has been queued from Heimdall on this machine yet — see the TUFLOW Runs page.</p>
+            }
+
+            @if (tuflow.History.Count > 0)
+            {
+                <h3 class="hd-section-title mt-4" style="font-size:1rem;">Recent runs</h3>
+                <div class="table-responsive">
+                    <table class="hd-table mb-0">
+                        <thead>
+                            <tr>
+                                <th>Run</th>
+                                <th>By</th>
+                                <th>Requested</th>
+                                <th>.tcf</th>
+                                <th>Outcome</th>
+                                <th>Duration</th>
+                                <th>Detail</th>
+                            </tr>
+                        </thead>
+                        <tbody>
+                            @foreach (var run in tuflow.History)
+                            {
+                                <tr>
+                                    <td>@run.RunName</td>
+                                    <td class="text-secondary small">@(run.RequestedBy ?? "—")</td>
+                                    <td>@MachineModel.FormatLocalTimestamp(run.RequestedUtc)</td>
+                                    <td class="text-secondary small" title="@run.TcfPath">@System.IO.Path.GetFileName(run.TcfPath)</td>
+                                    <td><span class="badge-pill @MachineModel.TuflowStateBadgeClass(run.State)">@run.State</span></td>
+                                    <td class="text-secondary small">@MachineModel.FormatDuration(run.StartedUtc, run.EndedUtc)</td>
+                                    <td class="text-secondary small">
+                                        @if (run.IsFailure && !string.IsNullOrEmpty(run.ErrorSummary))
+                                        {
+                                            <span style="color:#f0a0a0;" title="@run.ErrorSummary">
+                                                @(run.ErrorSummary!.Length > 80 ? run.ErrorSummary[..80] + "…" : run.ErrorSummary)
+                                            </span>
+                                        }
+                                        else if (run.PercentComplete is double p)
+                                        {
+                                            @($"{p:0.#}% complete")
+                                        }
+                                    </td>
+                                </tr>
+                            }
+                        </tbody>
+                    </table>
+                </div>
+            }
+        </div>
+    }
+
     @if (appLists is not null)
     {
```

## Notes

- `hd-panel` / `hd-section-title` / `hd-grid` / `hd-stat` / `label` / `value`
  / `badge-pill` / `text-secondary small` / `hd-table` / `table-responsive`
  are all classes I confirmed are used elsewhere in this exact file (or, for
  `hd-table`/`table-responsive`, in sibling pages like `Cost.cshtml` /
  `AppListChangelog.cshtml`) — not guessed the way the standalone TUFLOW
  Runs page's markup was. This panel should actually look native to your
  site, unlike that one.
- The panel is entirely absent (not just empty) for machines that aren't
  Flood-enrolled — `@if (Model.Tuflow is { FloodEnrolled: true } tuflow)`
  short-circuits before rendering anything.
- `ErrorSummary` is shown both on the current-run summary (if it's the most
  recent thing that happened) and per-row in history — same string, no
  separate formatting logic to keep in sync.
