# Portable workstation collector

Use this when you want to install the **Heimdall Agent** on other PCs (vanilla SOE / golden image / modelling boxes) **without** copying the full git repo or installing the .NET SDK on those machines.

## Preferred: Launch Control

After packing, on each target PC open:

```text
Heimdall-LaunchControl.cmd
```

It checks prerequisites (admin + `payload\`), asks for API URL / key / machine group, probes the server (`/api/health` + version), installs the service, then verifies (including **ApiBaseUrl on disk** matches what you entered). Logs: `%ProgramData%\Heimdall\logs\`.

After install, use **Client health check** to re-run service/settings/API probes. That writes `client-check-*.log` locally and, when the API host is reachable via admin share, copies to `\\API-HOST\C$\ProgramData\Heimdall\logs\clients\<this-pc>\` so server-side **Open remote logs** can open `logs\clients\`.

## Two-step workflow

### 1. Pack once (build PC — needs .NET 10 SDK + this repo)

From the Heimdall clone, prefer Launch Control → **Pack collector**, or:

```text
scripts\Pack-WorkstationCollector.cmd
```

Produces:

```text
dist\workstation-collector\
  Heimdall-LaunchControl.cmd / .ps1
  Install-WorkstationCollector.cmd
  README.md, FILES.md, VERSION.json, PACKED.txt
  payload\                  ← published agent binaries
dist\heimdall-workstation-collector.zip   ← if `tar` is available
```

### 2. Install on each target PC (admin; no SDK)

Copy the whole `workstation-collector` folder (or unzip the zip) to the target machine, then:

```text
Heimdall-LaunchControl.cmd
```

Or elevated direct install:

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
| `Heimdall-LaunchControl.cmd` | **Preferred** guided UI (install, client health check, logs) |
| `Install-WorkstationCollector.cmd` | Direct installer (CMD); opens Launch Control if double-clicked with no args |
| `payload\` | **Required.** Entire published agent output (`Heimdall.Agent.exe` + deps). Created by the pack script. |
| `VERSION.json` | Pack product version — compared to API `/api/health` |
| `README.md` / `FILES.md` | This documentation (optional on targets) |
| `PACKED.txt` | Build stamp from pack (optional) |
**Do not confuse folders:**

| Path | What it is |
|------|------------|
| `scripts\workstation-collector\` | Docs only in the git repo (`README.md` + `FILES.md`) — **not** installable |
| `dist\workstation-collector\` | Created by pack — **this** is what you copy to other PCs |

If your folder only has README/FILES and no `Install-WorkstationCollector.cmd` / `payload\`, pack has not succeeded yet.

You **must** copy the **whole** `dist\workstation-collector` folder, not just the `.cmd`. Without `payload\Heimdall.Agent.exe`, install fails.

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
| **NuGet access** | First publish must restore packages. Repo `NuGet.config` uses **nuget.org**. If you only have offline VS feeds, pack fails with **NU1101** — allow `https://api.nuget.org` or point NuGet at a corporate mirror that has those packages. |

**NU1101 / offline feeds:** If publish lists only `library-packs` and `Microsoft Visual Studio Offline Packages`, NuGet never saw nuget.org. Sync this branch (includes `NuGet.config`), check network/proxy, then `dotnet nuget list source` and re-run the pack script.

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
