import sqlite3
from datetime import datetime, timezone, timedelta

db = r"C:\ProgramData\Heimdall\heimdall.db"
con = sqlite3.connect(db)
con.row_factory = sqlite3.Row
cur = con.cursor()

tabs = [r[0] for r in cur.execute("SELECT name FROM sqlite_master WHERE type='table'").fetchall()]
print("fleet-ish tables:", [t for t in tabs if any(x in t for x in ("Fleet", "Metric", "Machine", "Tuflow", "Behaviour"))])

# Machines of interest
hosts = ("BNEDT1GYX7T3", "BNEDTC3XYXL4", "BNEDTJ6XYXL4")
print("\n=== Machines ===")
for row in cur.execute(
    """SELECT Id, Hostname, FriendlyName, LastIp, LastSeenUtc
       FROM Machines WHERE Hostname IN (?,?,?) OR FriendlyName IN ('Beefcake','Star Scream','Megatron')""",
    hosts,
):
    print(dict(row))

ids = [r[0] for r in cur.execute(
    "SELECT Id FROM Machines WHERE Hostname IN (?,?,?)", hosts
).fetchall()]
print("ids", ids)

# Discover snapshot table columns
for t in tabs:
    if "Fleet" in t and "Snapshot" in t or t == "FleetMetricSnapshots":
        cols = list(cur.execute(f"PRAGMA table_info({t})"))
        print(f"\n{t} cols:", [c[1] for c in cols])

snap_table = None
for cand in ("FleetMetricSnapshots", "FleetSnapshots", "MachineFleetSnapshots"):
    if cand in tabs:
        snap_table = cand
        break
if not snap_table:
    for t in tabs:
        if "Snapshot" in t:
            print("candidate", t)

if snap_table and ids:
    ph = ",".join("?" * len(ids))
    print(f"\n=== Latest 5 snaps per machine from {snap_table} ===")
    # column names vary
    cols = [c[1] for c in cur.execute(f"PRAGMA table_info({snap_table})")]
    time_col = "SampledAtUtc" if "SampledAtUtc" in cols else ("SampledUtc" if "SampledUtc" in cols else None)
    mid_col = "MachineId" if "MachineId" in cols else None
    print("time", time_col, "mid", mid_col)
    if time_col and mid_col:
        for mid in ids:
            rows = cur.execute(
                f"""SELECT * FROM {snap_table}
                    WHERE {mid_col}=?
                    ORDER BY {time_col} DESC LIMIT 5""",
                (mid,),
            ).fetchall()
            print(f"\nmachine {mid}:")
            for r in rows:
                d = dict(r)
                keys = [k for k in d if k in (time_col, "GpuPercent", "ProcessGpuPercent", "CpuPercent", "TuflowRunning", "RamUsedMb")]
                print({k: d[k] for k in keys})

        # count snaps after 21:15 local = 11:15 UTC on 2026-08-20
        cutoff = "2026-08-20T11:15:00+00:00"
        for mid in ids:
            n = cur.execute(
                f"SELECT COUNT(*) FROM {snap_table} WHERE {mid_col}=? AND {time_col} >= ?",
                (mid, cutoff),
            ).fetchone()[0]
            last = cur.execute(
                f"SELECT {time_col} FROM {snap_table} WHERE {mid_col}=? ORDER BY {time_col} DESC LIMIT 1",
                (mid,),
            ).fetchone()[0]
            print(f"id={mid} snaps_since_11:15Z={n} last={last}")

# Tuflow behaviour / detected runs
for t in tabs:
    if "Behaviour" in t or "Detected" in t or t == "TuflowBehaviours":
        print("\nbehaviour table", t, [c[1] for c in cur.execute(f"PRAGMA table_info({t})")])
