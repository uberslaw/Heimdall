# Workstation collector — files & dependencies

## Portable package contents (after pack)

```text
workstation-collector/
├── Heimdall-LaunchControl.cmd         # PREFERRED — guided setup UI
├── Heimdall-LaunchControl.ps1
├── Install-WorkstationCollector.cmd   # Direct/elevated install (or opens Launch Control if no args)
├── README.md
├── FILES.md
├── VERSION.json                       # productVersion for server match
├── PACKED.txt                         # When/where it was built
└── payload/                           # REQUIRED — do not omit
    ├── Heimdall.Agent.exe
    └── (other self-contained publish files)
```

## Source files in the repo (for maintainers)

| Path | Purpose |
|------|---------|
| `scripts/Heimdall-LaunchControl.cmd` / `.ps1` | Guided setup UI (API / pack / collector / logs / remote logs / verify) |
| `scripts/Pack-WorkstationCollector.cmd` | Builds `dist/workstation-collector` + optional zip |
| `scripts/Install-WorkstationCollector.cmd` | Copied into the package; installs the service |
| `scripts/workstation-collector/README.md` | Copied into the package |
| `scripts/workstation-collector/FILES.md` | Copied into the package |
| `src/Heimdall.Agent/**` | Agent project (published into `payload/`) |
| `src/Heimdall.Shared/**` | Shared contracts (pulled in by publish) |

## Runtime dependencies (target PC)

| Dependency | Bundled? | Notes |
|------------|----------|--------|
| .NET 10 runtime | **Yes** (self-contained publish) | No separate runtime install |
| Windows PowerShell 5.1+ | OS | Launch Control WinForms UI |
| Windows Service Control (`sc.exe`) | OS | Create/start `HeimdallAgent` |
| WMI / `System.Management` | OS + bundled assemblies | Hardware inventory |
| SQLite (offline queue) | Bundled via publish | `%ProgramData%\Heimdall\queue.db` |
| Network to API | Env | HTTP to `ApiBaseUrl` (POC often `:5080`) |

## Pack-time dependencies (build PC)

| Dependency | Notes |
|------------|--------|
| .NET 10 SDK | Required |
| NuGet packages from Agent csproj | Restored during `dotnet publish` |
| `NuGet.config` (repo root) | Adds nuget.org — needed when the machine only has offline VS package sources |
| Network to nuget.org (or mirror) | Without it: **NU1101** Unable to find package… |
| Windows | `win-x64` RID |

## Not included / separate tools

| Tool | Location |
|------|----------|
| API / dashboard installer | `scripts/Install-Api.cmd` (also via Launch Control) |
| Repo-based agent installer (needs SDK) | `scripts/Install-Agent.cmd` |
| SOE installed-programs inspector (CSV) | `scripts/Inspect-SoeInstalledPrograms.cmd` |
| Diagnostics zip | `scripts/Collect-Diagnostics.cmd` (also via Launch Control) |
