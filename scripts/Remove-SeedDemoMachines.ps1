#Requires -Version 5.1
<#
.SYNOPSIS
  Remove seed/demo machines from the Heimdall API SQLite database.

.DESCRIPTION
  Deletes Machines where AgentVersion = 'seed' or Hostname matches the demo
  placeholders from SeedData (DEMO-SYD-01, DEMO-SYD-02, DEMO-LON-01, DEMO-POC-01),
  plus related Sessions, ProcessRuns, and MachineIdentityEvents rows.

  Sets SystemFlags DemoMachinesOffered=1 so HeimdallApi restart does not re-seed demos.

  Uses Heimdall.Tools.RemoveSeedDemos (Microsoft.Data.Sqlite) - no sqlite3 CLI required.
  Stop HeimdallApi before running against a live database (sqlite lock).

.EXAMPLE
  .\Remove-SeedDemoMachines.ps1
  .\Remove-SeedDemoMachines.ps1 -DatabasePath \\APIHOST\C$\ProgramData\Heimdall\heimdall.db
#>
[CmdletBinding(SupportsShouldProcess = $true)]
param(
    [string]$DatabasePath = (Join-Path $env:ProgramData "Heimdall\heimdall.db")
)

$ErrorActionPreference = "Stop"

# Keep in sync with SeedData.DemoHostnames in IngestService.cs
$DemoHostnames = @(
    "DEMO-SYD-01",
    "DEMO-SYD-02",
    "DEMO-LON-01",
    "DEMO-POC-01"
)

function Get-RemoveSeedDemosToolSpec {
    $repoRoot = Split-Path $PSScriptRoot -Parent
    $toolName = "Heimdall.Tools.RemoveSeedDemos.exe"
    $projectRel = "src\Heimdall.Tools.RemoveSeedDemos\Heimdall.Tools.RemoveSeedDemos.csproj"
    $projectPath = Join-Path $repoRoot $projectRel

    $exeCandidates = @(
        (Join-Path $PSScriptRoot "tools\RemoveSeedDemos\$toolName"),
        (Join-Path $repoRoot "src\Heimdall.Tools.RemoveSeedDemos\bin\Release\net10.0\$toolName"),
        (Join-Path $repoRoot "src\Heimdall.Tools.RemoveSeedDemos\bin\Debug\net10.0\$toolName"),
        (Join-Path ${env:ProgramFiles} "Heimdall\Tools\RemoveSeedDemos\$toolName")
    )

    foreach ($exe in $exeCandidates) {
        if (Test-Path -LiteralPath $exe) {
            return @{
                Mode    = "exe"
                ExePath = $exe
            }
        }
    }

    $dotnet = Get-Command dotnet -ErrorAction SilentlyContinue
    if ($dotnet -and (Test-Path -LiteralPath $projectPath)) {
        return @{
            Mode        = "dotnet"
            DotnetPath  = $dotnet.Source
            ProjectPath = $projectPath
        }
    }

    throw @"
Heimdall.Tools.RemoveSeedDemos not found.

Build the tool from the repo root:
  dotnet build src\Heimdall.Tools.RemoveSeedDemos\Heimdall.Tools.RemoveSeedDemos.csproj -c Release

Or install the .NET SDK so this script can run:
  dotnet run --project src\Heimdall.Tools.RemoveSeedDemos\Heimdall.Tools.RemoveSeedDemos.csproj -c Release -- --db `"<path>`"

Demo hostnames: $($DemoHostnames -join ', ')
"@
}

function Invoke-RemoveSeedDemosTool {
    param(
        [Parameter(Mandatory)][string]$DbPath,
        [switch]$Delete
    )

    $spec = Get-RemoveSeedDemosToolSpec
    $toolArgs = @("--db", $DbPath)
    if ($Delete) { $toolArgs += "--delete" }

    if ($spec.Mode -eq "exe") {
        $output = & $spec.ExePath @toolArgs 2>&1
    }
    else {
        $output = & $spec.DotnetPath run --project $spec.ProjectPath -c Release -- @toolArgs 2>&1
    }

    if ($LASTEXITCODE -ne 0) {
        $detail = ($output | ForEach-Object { [string]$_ }) -join " "
        if ($LASTEXITCODE -eq 4) {
            throw "Database is locked. Stop the HeimdallApi Windows service on the API PC and retry.`r`n$detail"
        }
        throw "RemoveSeedDemos tool failed (exit $LASTEXITCODE): $detail"
    }

    return @($output | ForEach-Object { [string]$_ } | Where-Object { -not [string]::IsNullOrWhiteSpace($_) })
}

if (-not (Test-Path -LiteralPath $DatabasePath)) {
    throw "Database not found: $DatabasePath"
}

Write-Host "Database: $DatabasePath"
Write-Host "Scanning for seed/demo machines..."

$rows = @(Invoke-RemoveSeedDemosTool -DbPath $DatabasePath)
if ($rows.Count -eq 0) {
    Write-Host "No seed/demo machines found. Nothing to delete."
    exit 0
}

Write-Host ""
Write-Host "Machines to delete:"
foreach ($line in $rows) {
    $parts = $line -split "\|", 3
    $hostname = if ($parts.Count -ge 2) { $parts[1] } else { $line }
    Write-Host "  - $hostname"
}

if (-not $PSCmdlet.ShouldProcess($DatabasePath, "Delete $($rows.Count) seed/demo machine(s) and related rows")) {
    Write-Host "Cancelled (-WhatIf)."
    exit 0
}

Invoke-RemoveSeedDemosTool -DbPath $DatabasePath -Delete | Out-Null

Write-Host ""
Write-Host "Deleted $($rows.Count) seed/demo machine(s). DemoMachinesOffered flag set (API restart will not re-seed)."
Write-Host "Hostnames removed:"
foreach ($line in $rows) {
    $parts = $line -split "\|", 3
    if ($parts.Count -ge 2) { Write-Host "  $($parts[1])" }
}
