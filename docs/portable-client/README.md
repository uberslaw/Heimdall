# Heimdall Client (portable agent install)

One folder. One install. No SDK on target PCs.

**Full agent architecture:** see **[docs/CLIENT.md](../CLIENT.md)** (collection loops, API endpoints, fleet snapshots, TUFLOW, silent deploy).

## What you copy to each client PC

After packing on a build PC, copy **this one folder**:

```text
dist\Heimdall-Client\
```

(or unzip `dist\heimdall-client.zip`)

On the client, double-click:

```text
Install.lnk
```

That is the guided install (API URL → test → install service → verify).

## Build PC (create the folder once)

From a Heimdall repo clone with .NET 10 SDK, double-click:

```text
scripts\Heimdall-Setup.lnk
```

Then choose **Create client pack**. Or run:

```text
scripts\Pack-WorkstationCollector.cmd
```

Pack again when the **agent** changes (or if `dist\Heimdall-Client` is missing). Reuse the same folder on every PC until then.

**Push from Setup:** **Push client pack to PC…** asks for a hostname, copies the pack to `\\HOST\C$\Temp\Heimdall-Client-v{version}` (from pack `VERSION.json`), and opens that share in Explorer. On the target, run `Install.lnk` (admin). Needs your account to reach C$ on that PC.

## What is in the pack

| Item | Role |
|------|------|
| `Install.lnk` | **Only entry you need on clients** |
| `Install.cmd` / `Install-Client.ps1` | Guided wizard (launched by Install.lnk) |
| `payload\` | Required agent binaries (self-contained) |
| `payload\TuflowLauncher\` | TUFLOW start/stop helper (Flood-enrolled hosts) |
| `Install-WorkstationCollector.cmd` | Silent/scripted install (advanced) |
| `Heimdall-Setup.lnk` | Advanced tools (health check, logs) — optional on clients |
| `VERSION.json` / `PACKED.txt` | Pack metadata |

There is **no separate “workstation collector” folder to combine** with a “client install” folder. Those names meant the same pack.

## What the installed agent does (short)

- Windows service **`HeimdallAgent`**
- Posts sessions + allowlisted app usage to **`POST /api/ingest`** (~60s)
- Refreshes config from **`GET /api/config/{hostname}`** (~5 min)
- **30s fleet snapshots** to **`POST /api/fleet/snapshot`** (all heartbeating machines — CPU/GPU/RAM/disk/network + TUFLOW flags)
- Optional **Staff live sampling** when a viewer is active (separate from fleet snapshots)
- Offline queue: `%ProgramData%\Heimdall\queue.db` when API is down

See **[docs/CLIENT.md](../CLIENT.md)** for detail.

## Silent update (version 3+)

After bootstrap install, use dashboard **Fleet → Client version → Deploy Client** for silent service restart updates. Agents below version **3** need one manual `Install.lnk` first.

## Do not use

| Path | Why |
|------|-----|
| `docs\portable-client\` (this folder in git) | Documentation only — not installable |
| A folder with README/FILES but **no** `payload\` | Pack did not succeed |

## Silent install (optional)

```text
Install-WorkstationCollector.cmd -ApiUrl http://YOUR-API-HOST:5080 -MachineGroup SOE
```

Optional self-heal watchdog (default off):

```text
Install-WorkstationCollector.cmd -ApiUrl http://YOUR-API-HOST:5080 -MachineGroup SOE -EnableHealWatchdog
```

Or check **Self-heal watchdog (HeimdallAgentHeal)** in the Install.lnk wizard. Silent UpdateClient does not enable it; an existing task is preserved. To remove: `-UnregisterHealWatchdog`.

## Full-repo install (optional, needs SDK)

On a PC that already has the Heimdall clone and .NET 10 SDK:

```text
scripts\Install-Agent.cmd
```

Prefer the portable pack for vanilla SOE / other PCs.

Install guide: **[INSTALL.md](../../INSTALL.md)**
