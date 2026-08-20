#Requires -Version 5.1
<#
.SYNOPSIS
  Bake optional API base URL metadata into a Heimdall-Client pack folder.

.DESCRIPTION
  Writes pack-api.json and (when a URL is set) patches:
    - Set-HeimdallAgentApiBaseUrl.cmd  (API_IP / API_PORT)
    - Install-WorkstationCollector.cmd (default -ApiUrl)
    - payload\appsettings.json         (Heimdall:ApiBaseUrl)

  Blank / omitted URL = leave pack without pack-api.json (install/update preserve
  existing agent URL; Set-ApiUrl.lnk still available).

  forceOnUpdate=true is the only path that overwrites VPN agents on silent Deploy.

  Env (used by Pack-WorkstationCollector.cmd):
    HEIMDALL_PACK_API_URL       e.g. http://172.17.40.191:5080
    HEIMDALL_PACK_FORCE_API_URL 1 to set forceOnUpdate

  ASCII-only; PS 5.1; network-drive safe (LiteralPath + UTF8 no BOM for JSON).
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$PackFolder,
    [string]$ApiBaseUrl = "",
    [switch]$ForceOnUpdate
)

$ErrorActionPreference = "Stop"

if ([string]::IsNullOrWhiteSpace($ApiBaseUrl) -and -not [string]::IsNullOrWhiteSpace($env:HEIMDALL_PACK_API_URL)) {
    $ApiBaseUrl = $env:HEIMDALL_PACK_API_URL
}
if (-not $ForceOnUpdate -and $env:HEIMDALL_PACK_FORCE_API_URL -eq "1") {
    $ForceOnUpdate = $true
}

$PackFolder = [IO.Path]::GetFullPath($PackFolder)
if (-not (Test-Path -LiteralPath $PackFolder -PathType Container)) {
    throw "Pack folder not found: $PackFolder"
}

$utf8NoBom = New-Object System.Text.UTF8Encoding $false
$packApiPath = Join-Path $PackFolder "pack-api.json"

function Normalize-PackApiUrl([string]$raw) {
    if ([string]::IsNullOrWhiteSpace($raw)) { return $null }
    $u = $raw.Trim().TrimEnd("/")
    if ($u -notmatch '^https?://') {
        throw "ApiBaseUrl must start with http:// or https:// (got: $raw)"
    }
    return $u
}

function Get-HostAndPortFromApiUrl([string]$url) {
    $uri = [Uri]$url
    $hostName = $uri.Host
    $port = $uri.Port
    if ($port -le 0) {
        $port = if ($uri.Scheme -eq "https") { 443 } else { 80 }
    }
    return [pscustomobject]@{ Host = $hostName; Port = $port }
}

$url = Normalize-PackApiUrl $ApiBaseUrl
if (-not $url) {
    if (Test-Path -LiteralPath $packApiPath) {
        Remove-Item -LiteralPath $packApiPath -Force
    }
    Write-Host "[OK] No pack API URL baked (blank) - updates preserve existing agent ApiBaseUrl"
    return
}

$meta = [ordered]@{
    apiBaseUrl    = $url
    forceOnUpdate = [bool]$ForceOnUpdate
    bakedAtUtc    = (Get-Date).ToUniversalTime().ToString("o")
}
$json = ($meta | ConvertTo-Json -Depth 4) + "`n"
[IO.File]::WriteAllText($packApiPath, $json, $utf8NoBom)
Write-Host ("[OK] Wrote pack-api.json apiBaseUrl={0} forceOnUpdate={1}" -f $url, $ForceOnUpdate)

$hp = Get-HostAndPortFromApiUrl $url

# Patch Set-HeimdallAgentApiBaseUrl.cmd (IP + port used by Set-ApiUrl.lnk)
$setCmd = Join-Path $PackFolder "Set-HeimdallAgentApiBaseUrl.cmd"
if (Test-Path -LiteralPath $setCmd) {
    $raw = [IO.File]::ReadAllText($setCmd)
    $raw = [regex]::Replace($raw, 'set "API_IP=[^"]*"', ('set "API_IP={0}"' -f $hp.Host))
    $raw = [regex]::Replace($raw, 'set "API_PORT=[^"]*"', ('set "API_PORT={0}"' -f $hp.Port))
    [IO.File]::WriteAllText($setCmd, $raw, $utf8NoBom)
    Write-Host ("[OK] Patched Set-HeimdallAgentApiBaseUrl.cmd API_IP={0} API_PORT={1}" -f $hp.Host, $hp.Port)
}

# Patch silent installer default so unattended install without -ApiUrl uses pack URL.
# Only rewrite the first default http(s) assignment — never the ":arg_apiurl" set "APIURL=%~2" line.
$installCmd = Join-Path $PackFolder "Install-WorkstationCollector.cmd"
if (Test-Path -LiteralPath $installCmd) {
    $raw = [IO.File]::ReadAllText($installCmd)
    $m = [regex]::Match($raw, '(?m)^set "APIURL=https?://[^"]*"')
    if ($m.Success) {
        $newRaw = $raw.Substring(0, $m.Index) + ('set "APIURL={0}"' -f $url) + $raw.Substring($m.Index + $m.Length)
        [IO.File]::WriteAllText($installCmd, $newRaw, $utf8NoBom)
        Write-Host "[OK] Patched Install-WorkstationCollector.cmd default APIURL"
    }
    else {
        Write-Warning "Install-WorkstationCollector.cmd: no default APIURL=http(s) line to patch"
    }
}

# Bake into published agent appsettings (installer still rewrites on install)
$payloadSettings = Join-Path $PackFolder "payload\appsettings.json"
if (Test-Path -LiteralPath $payloadSettings) {
    try {
        $cfg = Get-Content -LiteralPath $payloadSettings -Raw -Encoding UTF8 | ConvertFrom-Json
        if (-not $cfg.Heimdall) {
            $cfg | Add-Member -NotePropertyName Heimdall -NotePropertyValue ([pscustomobject]@{}) -Force
        }
        $cfg.Heimdall | Add-Member -NotePropertyName ApiBaseUrl -NotePropertyValue $url -Force
        $out = ($cfg | ConvertTo-Json -Depth 8) + "`n"
        [IO.File]::WriteAllText($payloadSettings, $out, $utf8NoBom)
        Write-Host "[OK] Patched payload\appsettings.json ApiBaseUrl"
    }
    catch {
        Write-Warning ("Could not patch payload appsettings.json: {0}" -f $_.Exception.Message)
    }
}

if ($ForceOnUpdate) {
    Write-Host "[WARN] forceOnUpdate=true - silent Deploy will overwrite agent ApiBaseUrl with pack URL"
}
else {
    Write-Host "[OK] forceOnUpdate=false - silent Deploy keeps existing agent ApiBaseUrl (VPN-safe)"
}
