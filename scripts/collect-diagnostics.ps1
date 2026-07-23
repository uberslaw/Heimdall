<#
.SYNOPSIS
  Collect Heimdall install/runtime diagnostics for support or Cursor AI analysis.
.NOTES
  Prefer scripts\Collect-Diagnostics.cmd. Output under %LOCALAPPDATA%\Heimdall\diagnostics-*.
  API keys in appsettings are redacted (last 4 chars only).
#>
param(
    [string]$ApiUrl = "http://localhost:5080",
    [string]$OutRoot = "$env:LOCALAPPDATA\Heimdall"
)

$ErrorActionPreference = "Continue"
$script:BundleDir = $null

function Write-Step([string]$Message) {
    Write-Host "[*] $Message" -ForegroundColor Cyan
}

function Write-Note([string]$Message) {
    Write-Host "    $Message"
}

function Redact-AppSettingsJson {
    param([string]$Path, [string]$Dest)
    if (-not (Test-Path $Path)) {
        Set-Content -Path $Dest -Value "(file not found: $Path)" -Encoding UTF8
        return
    }
    try {
        $raw = Get-Content -Path $Path -Raw -Encoding UTF8
        $obj = $raw | ConvertFrom-Json
        if ($obj.Heimdall -and $obj.Heimdall.ApiKey) {
            $key = [string]$obj.Heimdall.ApiKey
            $tail = if ($key.Length -ge 4) { $key.Substring($key.Length - 4) } else { $key }
            $obj.Heimdall.ApiKey = "****$tail"
        }
        ($obj | ConvertTo-Json -Depth 8) | Set-Content -Path $Dest -Encoding UTF8
    }
    catch {
        # Fallback: simple string redact of "ApiKey": "..."
        $redacted = [regex]::Replace($raw, '("ApiKey"\s*:\s*")([^"]*)(")', {
            param($m)
            $v = $m.Groups[2].Value
            $tail = if ($v.Length -ge 4) { $v.Substring($v.Length - 4) } else { $v }
            "$($m.Groups[1].Value)****$tail$($m.Groups[3].Value)"
        })
        Set-Content -Path $Dest -Value $redacted -Encoding UTF8
    }
}

try {
    Write-Host ""
    Write-Host "============================================================" -ForegroundColor Cyan
    Write-Host "  Heimdall diagnostics collector" -ForegroundColor Cyan
    Write-Host "============================================================" -ForegroundColor Cyan
    Write-Host ""

    New-Item -ItemType Directory -Force -Path $OutRoot | Out-Null
    $stamp = Get-Date -Format "yyyyMMdd-HHmmss"
    $script:BundleDir = Join-Path $OutRoot "diagnostics-$stamp"
    New-Item -ItemType Directory -Force -Path $script:BundleDir | Out-Null
    Write-Step "Bundle folder: $script:BundleDir"

    # --- Environment summary ---
    Write-Step "Writing environment summary"
    $summary = @()
    $summary += "CollectedAt: $(Get-Date -Format o)"
    $summary += "Hostname: $env:COMPUTERNAME"
    $summary += "User: $env:USERNAME"
    $summary += "OS: $([System.Environment]::OSVersion.VersionString)"
    $summary += "PSVersion: $($PSVersionTable.PSVersion)"
    try {
        $os = Get-CimInstance Win32_OperatingSystem -ErrorAction Stop
        $summary += "Caption: $($os.Caption)"
        $summary += "Version: $($os.Version)"
        $summary += "Build: $($os.BuildNumber)"
    }
    catch { $summary += "WMI OS query failed: $($_.Exception.Message)" }
    try {
        $summary += "dotnet SDKs:"
        & dotnet --list-sdks 2>&1 | ForEach-Object { $summary += "  $_" }
        $summary += "dotnet runtimes:"
        & dotnet --list-runtimes 2>&1 | ForEach-Object { $summary += "  $_" }
    }
    catch { $summary += "dotnet not available: $($_.Exception.Message)" }
    $summary | Set-Content -Path (Join-Path $script:BundleDir "environment.txt") -Encoding UTF8

    # --- Service status ---
    Write-Step "Service status (HeimdallApi, HeimdallAgent)"
    $svcLines = @()
    foreach ($name in @("HeimdallApi", "HeimdallAgent")) {
        $svc = Get-Service -Name $name -ErrorAction SilentlyContinue
        if ($svc) {
            $line = "$name : Status=$($svc.Status) StartType=$($svc.StartType) DisplayName=$($svc.DisplayName)"
            $svcLines += $line
            Write-Note $line
            try {
                $cfg = Get-CimInstance Win32_Service -Filter "Name='$name'" -ErrorAction Stop
                $svcLines += "  PathName=$($cfg.PathName)"
                $svcLines += "  State=$($cfg.State) ExitCode=$($cfg.ExitCode) StartMode=$($cfg.StartMode)"
            }
            catch { $svcLines += "  WMI detail failed: $($_.Exception.Message)" }
        }
        else {
            $svcLines += "$name : NOT INSTALLED"
            Write-Note "$name : NOT INSTALLED"
        }
    }
    $svcLines | Set-Content -Path (Join-Path $script:BundleDir "services.txt") -Encoding UTF8

    # --- Health ---
    Write-Step "GET /api/health"
    $healthUrl = $ApiUrl.TrimEnd("/") + "/api/health"
    $healthOut = @("URL: $healthUrl", "Time: $(Get-Date -Format o)")
    try {
        $r = Invoke-WebRequest -Uri $healthUrl -UseBasicParsing -TimeoutSec 10
        $healthOut += "StatusCode: $($r.StatusCode)"
        $healthOut += "Content: $($r.Content)"
        Write-Note "HTTP $($r.StatusCode) — $($r.Content)"
    }
    catch {
        $healthOut += "ERROR: $($_.Exception.Message)"
        Write-Note "Unreachable: $($_.Exception.Message)"
    }
    $healthOut | Set-Content -Path (Join-Path $script:BundleDir "health.txt") -Encoding UTF8

    # --- Install logs ---
    Write-Step "Copying install logs"
    $logSrc = Join-Path $env:ProgramData "Heimdall\logs"
    $logDest = Join-Path $script:BundleDir "install-logs"
    New-Item -ItemType Directory -Force -Path $logDest | Out-Null
    if (Test-Path $logSrc) {
        Get-ChildItem $logSrc -File -ErrorAction SilentlyContinue |
            Sort-Object LastWriteTime -Descending |
            Select-Object -First 30 |
            ForEach-Object {
                Copy-Item $_.FullName -Destination $logDest -Force
                Write-Note "Copied $($_.Name)"
            }
    }
    else {
        "(no folder: $logSrc)" | Set-Content -Path (Join-Path $logDest "README.txt") -Encoding UTF8
        Write-Note "No install log folder at $logSrc"
    }

    # --- Redacted appsettings ---
    Write-Step "Redacting appsettings (API key → last 4 only)"
    $cfgDir = Join-Path $script:BundleDir "appsettings-redacted"
    New-Item -ItemType Directory -Force -Path $cfgDir | Out-Null
    Redact-AppSettingsJson -Path "$env:ProgramFiles\Heimdall\Api\appsettings.json" -Dest (Join-Path $cfgDir "api-appsettings.json")
    Redact-AppSettingsJson -Path "$env:ProgramFiles\Heimdall\Agent\appsettings.json" -Dest (Join-Path $cfgDir "agent-appsettings.json")
    Write-Note "Wrote api-appsettings.json / agent-appsettings.json"

    # --- Recent Event Log / service-ish output ---
    Write-Step "Recent Application log entries mentioning Heimdall / .NET"
    $evtPath = Join-Path $script:BundleDir "eventlog-recent.txt"
    try {
        $events = Get-WinEvent -FilterHashtable @{ LogName = "Application"; StartTime = (Get-Date).AddDays(-3) } -MaxEvents 200 -ErrorAction Stop |
            Where-Object {
                $_.ProviderName -match 'Heimdall|\.NET|VSS|Service Control Manager' -or
                $_.Message -match 'Heimdall'
            } |
            Select-Object -First 80
        if ($events) {
            $events | ForEach-Object {
                "--- $($_.TimeCreated) [$($_.LevelDisplayName)] $($_.ProviderName) ---"
                $_.Message
                ""
            } | Set-Content -Path $evtPath -Encoding UTF8
            Write-Note "Wrote $($events.Count) event entries"
        }
        else {
            "No matching recent Application events." | Set-Content -Path $evtPath -Encoding UTF8
            Write-Note "No matching events"
        }
    }
    catch {
        "Event log query failed: $($_.Exception.Message)" | Set-Content -Path $evtPath -Encoding UTF8
        Write-Note "Event log query failed: $($_.Exception.Message)"
    }

    # --- sc.exe query raw ---
    Write-Step "sc.exe query output"
    $scOut = @()
    foreach ($name in @("HeimdallApi", "HeimdallAgent")) {
        $scOut += "===== sc.exe query $name ====="
        $scOut += (& sc.exe query $name 2>&1 | Out-String)
        $scOut += "===== sc.exe qc $name ====="
        $scOut += (& sc.exe qc $name 2>&1 | Out-String)
    }
    $scOut | Set-Content -Path (Join-Path $script:BundleDir "sc-query.txt") -Encoding UTF8

    # --- Zip ---
    Write-Step "Creating zip"
    $zipPath = "$script:BundleDir.zip"
    if (Test-Path $zipPath) { Remove-Item $zipPath -Force }
    Compress-Archive -Path $script:BundleDir -DestinationPath $zipPath -Force
    Write-Note "Zip: $zipPath"

    Write-Host ""
    Write-Host "============================================================" -ForegroundColor Green
    Write-Host "  SUCCESS — diagnostics ready" -ForegroundColor Green
    Write-Host "============================================================" -ForegroundColor Green
    Write-Host ""
    Write-Host "Upload / paste in Cursor chat:" -ForegroundColor Yellow
    Write-Host "  ZIP:    $zipPath" -ForegroundColor Yellow
    Write-Host "  Folder: $script:BundleDir" -ForegroundColor Yellow
    Write-Host ""
    Write-Host "API keys were redacted (last 4 chars only)." -ForegroundColor DarkGray
}
catch {
    Write-Host ""
    Write-Host "============================================================" -ForegroundColor Red
    Write-Host "  FAILURE — diagnostics collection hit an error" -ForegroundColor Red
    Write-Host "============================================================" -ForegroundColor Red
    Write-Host $_.Exception.Message -ForegroundColor Red
    if ($script:BundleDir) {
        Write-Host "Partial bundle (if any): $script:BundleDir" -ForegroundColor Yellow
    }
}
finally {
    Write-Host ""
    Read-Host "Press Enter to close"
}
