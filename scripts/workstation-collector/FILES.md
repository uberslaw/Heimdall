# Workstation collector — files & dependencies

## Portable package contents (after pack)

```text
workstation-collector/
├── Install.lnk                          # PRIMARY — double-click on target PCs (helmet icon)
├── Install.cmd                        # Guided install launcher (same as Install.lnk)
├── Install-Client.ps1                 # Guided install wizard (WinForms)
├── Heimdall-VersionCompare.ps1        # Shared version compare helper
├── Heimdall-CollectorInstall.ps1      # Shared install launch + default ApiUrl
├── Install-WorkstationCollector.cmd   # Scripted/silent install (no args -> Install.cmd)
├── Heimdall-LaunchControl.lnk         # Advanced / build PC (helmet icon)
├── Heimdall-LaunchControl.cmd         # Same as .lnk (generic CMD icon)
├── Heimdall-LaunchControl.ps1
├── heimdall.ico                       # Helmet icon for shortcuts
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
| `scripts/Install.cmd` / `Install-Client.ps1` | Client-facing guided install |
| `scripts/Install.lnk` | Helmet-icon shortcut to Install.cmd (client pack + repo) |
| `scripts/Heimdall-VersionCompare.ps1` | Core SemVer compare (strips `+` build metadata) |
| `scripts/Heimdall-CollectorInstall.ps1` | Elevated CMD install wrapper; default ApiUrl `http://BNELT5CG5152D8R:5080` |
| `scripts/Heimdall-LaunchControl.lnk` / `.cmd` / `.ps1` | Advanced setup UI (prefer `.lnk` for helmet icon) |
| `scripts/New-HeimdallShortcut.ps1` | Creates `.lnk` with custom icon (used by pack + maintainers) |
| `assets/heimdall.ico` / `heimdall-icon.png` | Launch Control / Install shortcut icon |
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
| Windows PowerShell 5.1+ | OS | Install wizard WinForms UI |
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
