# Workstation collector — files & dependencies

## Portable package contents (after pack)

```text
workstation-collector/
├── Install-WorkstationCollector.cmd   # Run elevated on each PC
├── README.md                          # How to pack / install
├── FILES.md                           # This file
├── PACKED.txt                         # When/where it was built
└── payload/                           # REQUIRED — do not omit
    ├── Heimdall.Agent.exe
    └── (other self-contained publish files)
```

## Source files in the repo (for maintainers)

| Path | Purpose |
|------|---------|
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
| Windows Service Control (`sc.exe`) | OS | Create/start `HeimdallAgent` |
| WMI / `System.Management` | OS + bundled assemblies | Hardware inventory |
| SQLite (offline queue) | Bundled via publish | `%ProgramData%\Heimdall\queue.db` |
| Network to API | Env | HTTP to `ApiBaseUrl` (POC often `:5080`) |

## Pack-time dependencies (build PC)

| Dependency | Notes |
|------------|--------|
| .NET 10 SDK | Required |
| NuGet packages from Agent csproj | Restored during `dotnet publish` |
| Windows | `win-x64` RID |

## Not included / separate tools

| Tool | Location |
|------|----------|
| API / dashboard installer | `scripts/Install-Api.cmd` |
| Repo-based agent installer (needs SDK) | `scripts/Install-Agent.cmd` |
| SOE installed-programs inspector (CSV) | `scripts/Inspect-SoeInstalledPrograms.cmd` |
| Diagnostics zip | `scripts/Collect-Diagnostics.cmd` |
