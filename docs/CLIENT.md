# Heimdall client (agent) — how it works

This document explains the **Heimdall Agent** (`HeimdallAgent` Windows service): what it collects, how it talks to the API, and how installs/updates work. For step-by-step installation, see **[INSTALL.md](../INSTALL.md)**. For packing and copying the portable folder, see **[docs/portable-client/README.md](portable-client/README.md)**.

---

## What the client is

| Piece | Role |
|-------|------|
| **`HeimdallAgent` service** | Runs on each workstation; collects usage data and posts it to the API |
| **`Heimdall.Api` service** | Central ingest + Razor dashboard (not installed on every PC) |
| **SQLite on the API host** | Stores sessions, process runs, fleet snapshots, config |

The client is **self-contained** in the portable pack (`dist\Heimdall-Client\payload\`) — target PCs do not need the .NET SDK.

---

## Install entry points

| Method | When to use |
|--------|-------------|
| **`Install.lnk`** in `dist\Heimdall-Client\` | **Default** — guided wizard on each target PC |
| **Heimdall Setup → Push client pack to PC…** | Copy pack to `\\HOST\C$\Temp\Heimdall-Client` from a build PC |
| **`Install-WorkstationCollector.cmd -ApiUrl …`** | Silent/scripted install (advanced) |
| **`scripts\Install-Agent.cmd`** | Full-repo publish on a PC that already has the clone + SDK |

After install:

- Service name: **`HeimdallAgent`**
- Binaries: `%ProgramFiles%\Heimdall\Agent\`
- Config: `%ProgramFiles%\Heimdall\Agent\appsettings.json` (`ApiBaseUrl`, `ApiKey`, `MachineGroup`)
- Logs: `%ProgramData%\Heimdall\logs\`
- Offline queue: `%ProgramData%\Heimdall\queue.db`

---

## Agent lifecycle (1-second tick)

The agent runs a single loop (`Worker.cs`) with several independent timers:

| Loop | Cadence | Purpose |
|------|---------|---------|
| **Config refresh** | ~5 min (configurable) | `GET /api/config/{hostname}` — process allowlists, pauses, thresholds, pending commands, TUFLOW start requests, client update requests |
| **Session + process sample** | ~30 s default | WTS sessions + allowlisted process runs → buffered for upload |
| **Upload batch** | ~60 s default | `POST /api/ingest` — heartbeats, sessions, process runs, hardware, inventory |
| **Staff live sampling** | 10 s while active | Viewer-gated (`GET /api/resource-sampling/.../status`); used by Staff Access live metrics |
| **Fleet sampling** | **30 s always-on** | `POST /api/fleet/snapshot` — CPU/GPU/RAM/disk/network + TUFLOW flags for every known machine |
| **TUFLOW poll** | ~20 s | Picks up queued TUFLOW start/stop from config |
| **Disk usage scan poll** | ~20 s | On-demand folder scan when queued from machine detail |

If the API is unreachable, ingest batches are **queued** in `queue.db` and retried. Live/fleet samples are **dropped** on failure (stale points are worse than gaps).

---

## What gets collected

### Always (heartbeat + ingest)

- Hostname, last IP, agent version, machine group
- **Sessions** — local vs RDP, start/end, active vs disconnected seconds
- **Process runs** — only processes on applied **App lists** (plus legacy Tracking Config includes)
- **Hardware inventory** — brand, model, CPU, GPU, RAM, disk (WMI); refreshed on config cadence
- **Identity signals** — MachineGuid, SMBIOS UUID (reimage detection)

### Fleet snapshot (every 30 s, all heartbeating machines)

Append-only rows in `FleetMetricSnapshots`:

- System CPU %, GPU %, GPU memory MB (best-effort), RAM MB
- Disk read/write MB/s, network in/out MB/s
- Primary interactive username (if any)
- **TUFLOW running** — process name contains `tuflow` (configurable via `FleetProcessNames`)
- **Active / Idle** — while TUFLOW runs: Active if TUFLOW process GPU > 5%, CPU > 10%, or disk R/W > 5 MB/s
- Top-5 process lists (CPU/GPU/disk) for drill-down on Fleet → Computers

Retention: raw 30 s samples purged after ~**90 days** (`Heimdall:FleetSnapshotRetention`).

### Staff live sampling (optional, viewer-gated)

When a Staff Access or session drilldown viewer is active, the agent reports richer **10 s** samples to `/api/resource-sampling/report` (calibration burst + top processes + favourites). This is separate from fleet snapshots.

### TUFLOW automation (Flood-enrolled machines only)

Machines on the **Flood allowlist** (`FleetDashboardMachines`, enrolled under **Flood → Enrollment**) can receive:

- Queued **TUFLOW start** (exe + `.tcf` or `.cmd` batch)
- **Graceful stop** via process-group break
- Progress from `.tsf` / errors from `.tlf`

Requires `%ProgramFiles%\Heimdall\Agent\TuflowLauncher\TuflowLauncher.exe` (included in client pack / `Install-Agent`).

**Note:** Flood enrollment gates TUFLOW **control** and Flood hub analytics views. **Util sampling does not require enrollment** — every agent with a heartbeat gets fleet snapshots.

---

## Config resolution on the agent

On each config refresh the API merges, for that hostname:

1. **Tracking Config** scopes (All → Region → Office → Group → Machine) — intervals, excludes, pauses
2. **App list assignments** — effective include list (team track/ignore + machine overrides)
3. **Metric thresholds** — RAM/GPU/disk policies
4. **Pending one-shots** — inventory request, Restart RDS, TUFLOW start, disk scan, **UpdateClient**

The agent compares `ConfigVersion` and applies changes without restart (except client self-update, which restarts the service).

---

## Client version and silent deploy

Dashboard → **Fleet → Client version**:

| Concept | Detail |
|---------|--------|
| **Published version** | Integer baseline **3+** = first build that supports `UpdateClient` |
| **Pack client** | Rebuilds `dist\Heimdall-Client\`, bumps version (N+1) |
| **Deploy Client** | Queues silent download + service restart when pack is Ready and host is online |
| **Deferral** | Update waits while an interactive session is **Active** (no forced logoff) |
| **Bootstrap** | Agents below version **3** (or legacy SemVer → counted as **1**) need one manual **Install.lnk** or Setup push before silent Deploy works |

Agent reports `AgentVersion` on every heartbeat; dashboard compares to published version.

---

## API authentication

All agent endpoints require header:

```http
X-Heimdall-Key: heimdall-poc-key
```

(Change from POC default for anything beyond trusted LAN.)

Key endpoints used by the agent:

| Endpoint | Method | Purpose |
|----------|--------|---------|
| `/api/ingest` | POST | Sessions, processes, heartbeat |
| `/api/config/{hostname}` | GET | Merged tracking config + pending commands |
| `/api/fleet/snapshot` | POST | 30 s fleet metric row |
| `/api/resource-sampling/{hostname}/status` | GET | Staff live sampling on/off |
| `/api/resource-sampling/report` | POST | Staff live sample |
| `/api/health` | GET | Connectivity check (no key required) |

---

## Troubleshooting (client-side)

| Symptom | Check |
|---------|--------|
| Machine never appears | Service running? `ApiBaseUrl` reachable? API key match? Firewall on port 5080? |
| No util columns on Computers | Wait ~1–2 min after first heartbeat for 30 s snapshots; agent must be updated build |
| TUFLOW Runs ignored | Host enrolled on **Flood → Enrollment**? Agent has TuflowLauncher? |
| Deploy Client stuck Failed | Pre-3 agent? Pack not Ready? Session Active deferring update? |
| Mojibake usernames | Restart agent after encoding fix; run `scripts\Repair-SessionMojibake.ps1` on API host |

Collect logs: `scripts\Collect-Diagnostics.cmd` or `%ProgramData%\Heimdall\logs\install-agent-*.log`.

---

## Maintainer map

```
src/Heimdall.Agent/
  Worker.cs                    Main loop, fleet + live + TUFLOW ticks
  Collectors/                  Sessions, processes, hardware, resources
  Services/HeimdallApiClient.cs HTTP to API
scripts/Pack-WorkstationCollector.cmd
scripts/Install-WorkstationCollector.cmd
dist/Heimdall-Client/          Output pack (gitignored)
```

See also **[HANDOVER.md](../HANDOVER.md)** for product context and **[INSTALL.md](../INSTALL.md)** for server + client install procedures.
