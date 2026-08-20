import sqlite3
con = sqlite3.connect(r"C:\ProgramData\Heimdall\heimdall.db")
con.row_factory = sqlite3.Row
cur = con.cursor()

print("=== global latest fleet snaps ===")
for r in cur.execute(
    """SELECT m.Hostname, m.FriendlyName, s.SampledAtUtc, s.GpuPercent, s.TuflowRunning
       FROM FleetMetricSnapshots s JOIN Machines m ON m.Id=s.MachineId
       ORDER BY s.SampledAtUtc DESC LIMIT 15"""
):
    print(dict(r))

print("\n=== behaviour runs for beef/star/mega ===")
ids = (26, 12, 13)
for mid in ids:
    for r in cur.execute(
        """SELECT Id, MachineId, Username, State, DetectedStartUtc, DetectedEndUtc, ProcessGoneUtc, UpdatedUtc, SampleCount, PeakGpuPercent
           FROM TuflowBehaviourRuns WHERE MachineId=? ORDER BY UpdatedUtc DESC LIMIT 3""",
        (mid,),
    ):
        print(dict(r))

print("\n=== latest behaviour samples for those runs ===")
for mid in ids:
    runs = cur.execute(
        "SELECT Id FROM TuflowBehaviourRuns WHERE MachineId=? ORDER BY UpdatedUtc DESC LIMIT 1",
        (mid,),
    ).fetchall()
    if not runs:
        continue
    rid = runs[0][0]
    for r in cur.execute(
        """SELECT SampledAtUtc, TuflowRunning, ProcessGpuPercent, MachineGpuPercent
           FROM TuflowBehaviourSamples WHERE BehaviourRunId=? ORDER BY SampledAtUtc DESC LIMIT 3""",
        (rid,),
    ):
        print(mid, dict(r))
