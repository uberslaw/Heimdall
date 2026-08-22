#Requires -Version 5.1
<#
.SYNOPSIS
  HeimdallApiHeal watchdog: restart HeimdallApi when /api/health is unreachable.

.DESCRIPTION
  Runs as SYSTEM via scheduled task HeimdallApiHeal (enabled by install-api.ps1 by default).
  - Health OK -> exit
  - Service Stopped -> sc start
  - Service Running but health fails -> sc stop / sc start (wedged listener)
  Logs: %ProgramData%\Heimdall\logs\api-heal\api-heal-yyyyMMdd.log
#>
[CmdletBinding()]
param(
    [int]$Port = 5080,
    [int]$HealthTimeoutSec = 8
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

$script:ServiceName = "HeimdallApi"
$script:TaskName = "HeimdallApiHeal"
$script:LogRoot = Join-Path $env:ProgramData "Heimdall\logs\api-heal"
$script:HealRoot = Join-Path $env:ProgramData "Heimdall\heal"
$script:HealScriptName = "Heimdall-ApiHeal.ps1"
$script:ApiAppsettings = Join-Path ${env:ProgramFiles} "Heimdall\Api\appsettings.json"
$script:LogPath = $null

function Write-HealLog {
    param(
        [Parameter(Mandatory)][string]$Message,
        [ValidateSet("INFO", "WARN", "ERROR", "OK", "STEP")]
        [string]$Level = "INFO"
    )
    $ts = Get-Date -Format "yyyy-MM-dd HH:mm:ss"
    $line = "[$ts] [$Level] $Message"
    Write-Host $line
    if ($script:LogPath) {
        Add-Content -LiteralPath $script:LogPath -Value $line -Encoding UTF8
    }
}

function Ensure-Dir([string]$Path) {
    if (-not (Test-Path -LiteralPath $Path)) {
        New-Item -ItemType Directory -Path $Path -Force | Out-Null
    }
}

function Get-ApiHealthUrl {
    return "http://127.0.0.1:$Port/api/health"
}

function Test-ApiHealthReachable {
    $uri = Get-ApiHealthUrl
    try {
        $resp = Invoke-RestMethod -Uri $uri -Method Get -TimeoutSec $HealthTimeoutSec
        if ($null -eq $resp) { return $false }
        if ($resp.status -and "$($resp.status)" -ne "ok") { return $false }
        return $true
    }
    catch {
        return $false
    }
}

function Get-ApiKeyFromAppsettings {
    if (-not (Test-Path -LiteralPath $script:ApiAppsettings)) { return $null }
    try {
        $json = Get-Content -LiteralPath $script:ApiAppsettings -Raw -Encoding UTF8 | ConvertFrom-Json
        $key = $json.Heimdall.ApiKey
        if ([string]::IsNullOrWhiteSpace($key)) { return $null }
        return [string]$key.Trim()
    }
    catch {
        return $null
    }
}

function Send-HealEvent {
    param(
        [Parameter(Mandatory)][string]$Action,
        [string]$Detail = $null
    )
    $key = Get-ApiKeyFromAppsettings
    if (-not $key) { return }
    $uri = "http://127.0.0.1:$Port/api/ops/heal-event"
    $body = @{
        action = $Action
        detail = $Detail
        source = "HeimdallApiHeal"
        utc    = (Get-Date).ToUniversalTime().ToString("o")
    } | ConvertTo-Json -Compress
    try {
        $headers = @{ "X-Heimdall-Key" = $key; "Content-Type" = "application/json" }
        Invoke-RestMethod -Uri $uri -Method Post -Headers $headers -Body $body -TimeoutSec 10 | Out-Null
        Write-HealLog "Reported heal event to API ($Action)" -Level OK
    }
    catch {
        Write-HealLog "Could not report heal event (API may still be starting): $($_.Exception.Message)" -Level WARN
    }
}

function Wait-ServiceStatus {
    param(
        [Parameter(Mandatory)][string]$Name,
        [Parameter(Mandatory)][ValidateSet("Running", "Stopped")]
        [string]$Desired,
        [int]$TimeoutSec = 90
    )
    $deadline = (Get-Date).AddSeconds($TimeoutSec)
    while ((Get-Date) -lt $deadline) {
        $svc = Get-Service -Name $Name -ErrorAction SilentlyContinue
        if ($svc -and $svc.Status -eq $Desired) { return $true }
        Start-Sleep -Seconds 2
    }
    return $false
}

function Restart-HeimdallApi {
    param([string]$Reason)
    Write-HealLog "Restarting $script:ServiceName ($Reason)" -Level STEP
    Send-HealEvent -Action "restart-begin" -Detail $Reason
    $null = & sc.exe stop $script:ServiceName 2>&1
    if (-not (Wait-ServiceStatus -Name $script:ServiceName -Desired "Stopped" -TimeoutSec 60)) {
        Write-HealLog "Service did not stop cleanly — attempting start anyway" -Level WARN
    }
    Start-Sleep -Seconds 2
    $null = & sc.exe start $script:ServiceName 2>&1
    if (-not (Wait-ServiceStatus -Name $script:ServiceName -Desired "Running" -TimeoutSec 90)) {
        Write-HealLog "Service did not reach Running after heal restart" -Level ERROR
        return $false
    }
    Start-Sleep -Seconds 3
    if (-not (Test-ApiHealthReachable)) {
        Write-HealLog "Service Running but /api/health still failing after restart" -Level ERROR
        return $false
    }
    Write-HealLog "Heal restart succeeded; /api/health OK" -Level OK
    Send-HealEvent -Action "restart-ok" -Detail $Reason
    return $true
}

function Start-HeimdallApi {
    Write-HealLog "Starting stopped $script:ServiceName" -Level STEP
    Send-HealEvent -Action "start-begin" -Detail "service was stopped"
    $null = & sc.exe start $script:ServiceName 2>&1
    if (-not (Wait-ServiceStatus -Name $script:ServiceName -Desired "Running" -TimeoutSec 90)) {
        Write-HealLog "sc start did not reach Running" -Level ERROR
        return $false
    }
    Start-Sleep -Seconds 3
    if (-not (Test-ApiHealthReachable)) {
        return Restart-HeimdallApi -Reason "started but health still failing"
    }
    Write-HealLog "Service started; /api/health OK" -Level OK
    Send-HealEvent -Action "start-ok" -Detail "service was stopped"
    return $true
}

Ensure-Dir $script:LogRoot
$script:LogPath = Join-Path $script:LogRoot ("api-heal-{0:yyyyMMdd}.log" -f (Get-Date))

if (Test-ApiHealthReachable) {
    Write-HealLog "Health OK $(Get-ApiHealthUrl)" -Level OK
    exit 0
}

Write-HealLog "Health probe failed $(Get-ApiHealthUrl)" -Level WARN

$svc = Get-Service -Name $script:ServiceName -ErrorAction SilentlyContinue
if (-not $svc) {
    Write-HealLog "$script:ServiceName is not installed — cannot heal" -Level ERROR
    exit 2
}

if ($svc.Status -eq "Stopped") {
    if (Start-HeimdallApi) { exit 0 }
    exit 1
}

if ($svc.Status -eq "Running") {
    if (Restart-HeimdallApi -Reason "running but health unreachable") { exit 0 }
    exit 1
}

Write-HealLog "Service status is $($svc.Status) — no heal action" -Level WARN
exit 1
