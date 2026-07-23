# Portable workstation collector

Use this when you want to install the **Heimdall Agent** on other PCs (vanilla SOE / golden image / modelling boxes) **without** copying the full git repo or installing the .NET SDK on those machines.

## Two-step workflow

### 1. Pack once (build PC — needs .NET 10 SDK + this repo)

From an elevated or normal prompt in the Heimdall clone:

```text
scripts\Pack-WorkstationCollector.cmd
```

Produces:

```text
dist\workstation-collector\
  Install-WorkstationCollector.cmd
  README.md
  FILES.md
  PACKED.txt
  payload\                  ← published agent binaries
dist\heimdall-workstation-collector.zip   ← if `tar` is available
```

### 2. Install on each target PC (admin; no SDK)

Copy the whole `workstation-collector` folder (or unzip the zip) to the target machine, then **Run as administrator**:

```text
Install-WorkstationCollector.cmd -ApiUrl http://YOUR-API-HOST:5080 -MachineGroup SOE
```

Optional:

```text
Install-WorkstationCollector.cmd -ApiUrl http://SERVER:5080 -ApiKey heimdall-poc-key -MachineGroup SOE
```

The API key **must match** the Heimdall API. Default POC key: `heimdall-poc-key`.

After the first successful heartbeat, the hostname appears on the dashboard **Machines** page.

---

## What goes with the script

| Item | Role |
|------|------|
| `Install-WorkstationCollector.cmd` | Installer (CMD only — no PowerShell) |
| `payload\` | **Required.** Entire published agent output (`Heimdall.Agent.exe` + deps). Created by the pack script. |
| `README.md` / `FILES.md` | This documentation (optional on targets) |
| `PACKED.txt` | Build stamp from pack (optional) |

You **must** copy the **whole folder**, not just the `.cmd`. Without `payload\Heimdall.Agent.exe`, install fails.

Do **not** need on the target:

- Full Heimdall git clone
- `src\` sources
- .NET 10 SDK
- PowerShell execution policy changes

---

## Dependencies

### Pack machine (once)

| Requirement | Notes |
|-------------|--------|
| Windows | To produce `win-x64` payload |
| **.NET 10 SDK** | `dotnet publish` |
| Full repo | `src\Heimdall.Agent` + `src\Heimdall.Shared` |
| Network (optional) | NuGet restore on first publish |

### Target workstation (each PC)

| Requirement | Notes |
|-------------|--------|
| Windows x64 | Service + WMI inventory |
| **Local Administrator** | `sc.exe` create/start |
| **No .NET install** | Payload is **self-contained** `win-x64` |
| Reachable Heimdall API | Default port **5080**; key must match |
| Built-in tools | `sc.exe`, `robocopy`, `curl.exe` (health probe is best-effort) |

### Related (not in this pack)

| Script | When to use |
|--------|-------------|
| `scripts\Install-Agent.cmd` | Install from full repo (publishes with framework-dependent build; needs SDK on that PC) |
| `scripts\Inspect-SoeInstalledPrograms.cmd` | One-shot **installed-programs CSV** on a golden image for SOE exclude review — separate from the live collector |
| `scripts\Install-Api.cmd` | Server / dashboard only |

---

## Vanilla SOE tips

1. Point `-ApiUrl` at your real API host (**not** `localhost` unless the API runs on the same box).
2. Use a clear `-MachineGroup` such as `SOE` or `APAC/Sydney` so Machines / Stats scoping is useful.
3. For **program-list** SOE excludes (Uninstall registry dump), also run `Inspect-SoeInstalledPrograms` on a clean image — that is inventory for Config excludes, not the continuous collector.
4. Install log: `%ProgramData%\Heimdall\logs\install-workstation-collector-*.log`
5. Offline queue: `%ProgramData%\Heimdall\queue.db`

---

## Uninstall (manual)

Elevated CMD:

```text
sc stop HeimdallAgent
sc delete HeimdallAgent
rmdir /S /Q "%ProgramFiles%\Heimdall\Agent"
```

Leave `%ProgramData%\Heimdall\` if you may reinstall and want the queue kept.
