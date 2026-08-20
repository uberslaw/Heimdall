#Requires -RunAsAdministrator
<#
.SYNOPSIS
  Point the installed HeimdallAgent at a new API base URL and restart the service.
.DESCRIPTION
  Edits %ProgramFiles%\Heimdall\Agent\appsettings.json (Heimdall:ApiBaseUrl), then restarts HeimdallAgent.

  Prefer Set-HeimdallAgentApiBaseUrl.cmd (same folder) - it has the API IP at the top and works from a network share.
.EXAMPLE
  .\Set-HeimdallAgentApiBaseUrl.ps1 -IpAddress 172.17.40.191
#>
param(
    # Default matches scripts\Set-HeimdallAgentApiBaseUrl.cmd - change both when the API host moves.
    [string] $IpAddress = '172.17.40.191',

    [string] $ApiBaseUrl,

    [int] $Port = 5080,

    [string] $InstallDir = $(Join-Path $env:ProgramFiles 'Heimdall\Agent'),

    [string] $ServiceName = 'HeimdallAgent'
)

$ErrorActionPreference = 'Stop'

function Test-HeimdallIpv4([string] $ip) {
    return $ip -match '^(?:(?:25[0-5]|2[0-4]\d|[01]?\d\d?)\.){3}(?:25[0-5]|2[0-4]\d|[01]?\d\d?)$'
}

function Resolve-ApiBaseUrl {
    if (-not [string]::IsNullOrWhiteSpace($ApiBaseUrl)) {
        $u = $ApiBaseUrl.Trim().TrimEnd('/')
        if ($u -notmatch '^https?://') {
            throw "ApiBaseUrl must start with http:// or https:// (got: $ApiBaseUrl)"
        }
        return $u
    }

    $ip = $IpAddress.Trim()
    if ([string]::IsNullOrWhiteSpace($ip)) {
        throw 'IpAddress is required.'
    }

    if ($ip -match '^https?://') {
        return $ip.TrimEnd('/')
    }

    $ip = $ip -replace '^https?://', ''
    $ip = ($ip -split '/')[0]
    $port = $Port
    if ($ip -match '^(.+):(\d+)$') {
        $ip = $Matches[1]
        $port = [int]$Matches[2]
    }

    if (-not (Test-HeimdallIpv4 $ip)) {
        throw "Expected an IPv4 address (e.g. 172.17.40.191). Got: $ip"
    }

    return "http://${ip}:${port}"
}

$url = Resolve-ApiBaseUrl

$settingsPath = Join-Path $InstallDir 'appsettings.json'
if (-not (Test-Path -LiteralPath $settingsPath)) {
    throw "Not found: $settingsPath - is Heimdall Agent installed?"
}

$config = Get-Content -LiteralPath $settingsPath -Raw -Encoding UTF8 | ConvertFrom-Json
if (-not $config.Heimdall) {
    $config | Add-Member -NotePropertyName Heimdall -NotePropertyValue ([pscustomobject]@{})
}

$old = $null
try { $old = [string]$config.Heimdall.ApiBaseUrl } catch { $old = $null }

$config.Heimdall | Add-Member -NotePropertyName ApiBaseUrl -NotePropertyValue $url -Force
$json = $config | ConvertTo-Json -Depth 10
Set-Content -LiteralPath $settingsPath -Value $json -Encoding UTF8

Write-Host "ApiBaseUrl: $(if ($old) { $old } else { '(unset)' })  ->  $url"
Write-Host "Wrote: $settingsPath"

$svc = Get-Service -Name $ServiceName -ErrorAction SilentlyContinue
if (-not $svc) {
    Write-Warning "Service $ServiceName not found - appsettings updated; start the agent manually."
    exit 0
}

Write-Host "Restarting $ServiceName..."
Restart-Service -Name $ServiceName -Force
Start-Sleep -Seconds 2
$after = Get-Service -Name $ServiceName
Write-Host "Service status: $($after.Status)"
if ($after.Status -ne 'Running') {
    throw "$ServiceName is $($after.Status) after restart - check Event Log / agent logs."
}

Write-Host "Done."
