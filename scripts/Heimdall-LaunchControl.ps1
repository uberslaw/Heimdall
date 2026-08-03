#Requires -Version 5.1
<#
.SYNOPSIS
  Heimdall Setup - guided API install, client pack, and agent install.

.DESCRIPTION
  Single entry point for install/configure actions. Shows steps, collects input,
  logs everything under %ProgramData%\Heimdall\logs\, and verifies at the end.
  Prefer scripts\Heimdall-Setup.lnk (helmet icon) or scripts\Heimdall-Setup.cmd.
  Heimdall-LaunchControl.* are compatibility wrappers to this UI.

.NOTES
  Works from a full repo clone OR from a packed dist\Heimdall-Client folder
  (agent install + verify + logs only when payload\ is present).
#>
param(
    [ValidateSet("Menu", "InstallApi", "PackCollector", "InstallCollector", "PushClientPack", "ClientCheck", "OpenLogs", "OpenRemoteLogs", "BackupApiDatabase", "RemoveSeedDemos", "Diagnostics")]
    [string]$Mode = "Menu"
)

$ErrorActionPreference = "Stop"
Add-Type -AssemblyName System.Windows.Forms
Add-Type -AssemblyName System.Drawing
[System.Windows.Forms.Application]::EnableVisualStyles()

$script:ScriptDirEarly = Split-Path -Parent $MyInvocation.MyCommand.Path
$versionHelperPath = Join-Path $script:ScriptDirEarly "Heimdall-VersionCompare.ps1"
if (Test-Path -LiteralPath $versionHelperPath) {
    . $versionHelperPath
}
$collectorHelperPath = Join-Path $script:ScriptDirEarly "Heimdall-CollectorInstall.ps1"
if (Test-Path -LiteralPath $collectorHelperPath) {
    . $collectorHelperPath
}
$installApiTimingPath = Join-Path $script:ScriptDirEarly "Heimdall-InstallApiTiming.ps1"
if (Test-Path -LiteralPath $installApiTimingPath) {
    . $installApiTimingPath
}
Import-HeimdallVersionCompare -ScriptDir $script:ScriptDirEarly

$script:ProductVersionExpected = "0.1.0"
$script:ScriptDir = Split-Path -Parent $MyInvocation.MyCommand.Path
$script:IsPackedLayout = Test-Path (Join-Path $script:ScriptDir "payload\Heimdall.Agent.exe")
$script:RepoRoot = if ($script:IsPackedLayout) {
    $null
} else {
    # scripts\ -> repo root
    Split-Path -Parent $script:ScriptDir
}

$script:LogRoot = Join-Path $env:ProgramData "Heimdall\logs"
$script:DataRoot = Join-Path $env:ProgramData "Heimdall"
$script:RemoteLogTargetsFile = Join-Path $env:LOCALAPPDATA "Heimdall\remote-log-targets.json"
$script:LastInstallSettingsFile = Join-Path $env:LOCALAPPDATA "Heimdall\last-install-settings.json"
$script:DiagnosticsDropFile = Join-Path $env:LOCALAPPDATA "Heimdall\diagnostics-drop.json"
$script:RemoteLogTargetsMax = 15
# Push client pack: remembered hosts + named groups (separate from RemoteLogTargets, which is
# keyed by UNC log path). See Get-PushHosts / Get-PushGroups.
$script:PushHostsFile = Join-Path $env:LOCALAPPDATA "Heimdall\push-hosts.json"
$script:PushGroupsFile = Join-Path $env:LOCALAPPDATA "Heimdall\push-groups.json"
$script:PushHostsMax = 300
$script:AgentInstallDir = Join-Path ${env:ProgramFiles} "Heimdall\Agent"
$script:AgentAppSettingsPath = Join-Path $script:AgentInstallDir "appsettings.json"
$script:LogPath = $null
$script:UiLogBox = $null
$script:UiStatus = $null
$script:UiSteps = $null
$script:UiGuideList = $null
$script:UiGuideDetail = $null
$script:UiGuideBranch = "Client"
$script:GuideStepsByBranch = $null
$script:LaunchControlBusy = $false
$script:LaunchControlActionButtons = @()
$script:LaunchControlPreviousStatus = $null
$script:HeimdallServiceStatusRefresh = $null

# ---------------------------------------------------------------------------
# Logging
# ---------------------------------------------------------------------------

function Initialize-HeimdallLogging {
    param([string]$Prefix = "launch-control")
    if (-not (Test-Path $script:LogRoot)) {
        New-Item -ItemType Directory -Path $script:LogRoot -Force | Out-Null
    }
    if (-not (Test-Path $script:DataRoot)) {
        New-Item -ItemType Directory -Path $script:DataRoot -Force | Out-Null
    }
    $stamp = Get-Date -Format "yyyyMMdd-HHmmss"
    $script:LogPath = Join-Path $script:LogRoot "$Prefix-$stamp.log"
    $header = @"

================================================================
  Heimdall Setup
================================================================
Log: $($script:LogPath)
User: $env:USERNAME | Machine: $env:COMPUTERNAME
OS: $([Environment]::OSVersion.VersionString)
ScriptDir: $($script:ScriptDir)
PackedLayout: $($script:IsPackedLayout)
RepoRoot: $($script:RepoRoot)
Started: $(Get-Date -Format "yyyy-MM-dd HH:mm:ss")

"@
    Add-Content -Path $script:LogPath -Value $header -Encoding UTF8
    return $script:LogPath
}

function Write-HeimdallLog {
    param(
        [Parameter(Mandatory)][string]$Message,
        [ValidateSet("INFO", "WARN", "ERROR", "STEP", "OK", "ASK")]
        [string]$Level = "INFO"
    )
    $ts = Get-Date -Format "yyyy-MM-dd HH:mm:ss"
    $line = "[$ts] [$Level] $Message"
    if ($script:LogPath) {
        Add-Content -Path $script:LogPath -Value $line -Encoding UTF8
    }
    if ($script:UiLogBox -and -not $script:UiLogBox.IsDisposed) {
        $color = switch ($Level) {
            "OK"    { [System.Drawing.Color]::DarkGreen }
            "WARN"  { [System.Drawing.Color]::DarkGoldenrod }
            "ERROR" { [System.Drawing.Color]::Firebrick }
            "STEP"  { [System.Drawing.Color]::DarkBlue }
            "ASK"   { [System.Drawing.Color]::DarkSlateBlue }
            default { [System.Drawing.Color]::Black }
        }
        $script:UiLogBox.SelectionStart = $script:UiLogBox.TextLength
        $script:UiLogBox.SelectionColor = $color
        $script:UiLogBox.AppendText("$line`r`n")
        $script:UiLogBox.SelectionStart = $script:UiLogBox.TextLength
        $script:UiLogBox.ScrollToCaret()
        [System.Windows.Forms.Application]::DoEvents()
    }
    else {
        $fc = switch ($Level) {
            "OK"    { "Green" }
            "WARN"  { "Yellow" }
            "ERROR" { "Red" }
            "STEP"  { "Cyan" }
            default { "White" }
        }
        Write-Host $line -ForegroundColor $fc
    }
}

function Set-UiStatus {
    param([string]$Text)
    if ($script:UiStatus -and -not $script:UiStatus.IsDisposed) {
        $script:UiStatus.Text = $Text
        [System.Windows.Forms.Application]::DoEvents()
    }
}

function Set-UiSteps {
    param([string[]]$Lines)
    if ($script:UiSteps -and -not $script:UiSteps.IsDisposed) {
        $script:UiSteps.Items.Clear()
        foreach ($l in $Lines) { [void]$script:UiSteps.Items.Add($l) }
        [System.Windows.Forms.Application]::DoEvents()
    }
}

function Update-UiStep {
    param([int]$Index, [string]$Text)
    if ($script:UiSteps -and -not $script:UiSteps.IsDisposed -and $Index -ge 0 -and $Index -lt $script:UiSteps.Items.Count) {
        $script:UiSteps.Items[$Index] = $Text
        [System.Windows.Forms.Application]::DoEvents()
    }
}

function Register-LaunchControlActionButton {
    param([System.Windows.Forms.Button]$Button)
    if ($Button -and $script:LaunchControlActionButtons -notcontains $Button) {
        $script:LaunchControlActionButtons += $Button
    }
}

function Set-LaunchControlBusy {
    param([bool]$Busy)
    $script:LaunchControlBusy = $Busy
    foreach ($btn in $script:LaunchControlActionButtons) {
        if ($btn -and -not $btn.IsDisposed) {
            $btn.Enabled = -not $Busy
        }
    }
    if ($script:UiStatus -and -not $script:UiStatus.IsDisposed) {
        if ($Busy) {
            if (-not $script:LaunchControlPreviousStatus) {
                $script:LaunchControlPreviousStatus = $script:UiStatus.Text
            }
        }
        elseif ($script:LaunchControlPreviousStatus) {
            $script:UiStatus.Text = $script:LaunchControlPreviousStatus
            $script:LaunchControlPreviousStatus = $null
        }
    }
    if ($script:HeimdallServiceStatusRefresh) {
        & $script:HeimdallServiceStatusRefresh
    }
    [System.Windows.Forms.Application]::DoEvents()
}

function Get-HeimdallServiceInfo {
    param(
        [Parameter(Mandatory = $true)][string]$ServiceName
    )
    $svc = Get-Service -Name $ServiceName -ErrorAction SilentlyContinue
    if (-not $svc) {
        return [PSCustomObject]@{
            Name       = $ServiceName
            Installed  = $false
            StatusText = "Missing"
        }
    }
    $statusText = switch ($svc.Status) {
        ([System.ServiceProcess.ServiceControllerStatus]::Running) { "Running" }
        ([System.ServiceProcess.ServiceControllerStatus]::Stopped) { "Stopped" }
        default { $svc.Status.ToString() }
    }
    return [PSCustomObject]@{
        Name       = $ServiceName
        Installed  = $true
        Status     = $svc.Status
        StatusText = $statusText
    }
}

function Set-HeimdallServiceStatusLabel {
    param(
        [System.Windows.Forms.Label]$Label,
        [string]$ServiceName
    )
    if (-not $Label -or $Label.IsDisposed) { return }
    $info = Get-HeimdallServiceInfo -ServiceName $ServiceName
    if (-not $info.Installed) {
        $Label.Text = "Not installed"
        $Label.ForeColor = [System.Drawing.Color]::Gray
        return
    }
    $Label.Text = $info.StatusText
    $Label.ForeColor = switch ($info.StatusText) {
        "Running" { [System.Drawing.Color]::DarkGreen }
        "Stopped" { [System.Drawing.Color]::Firebrick }
        default { [System.Drawing.Color]::DarkGoldenrod }
    }
}

function Set-HeimdallServiceControlButtons {
    param(
        [System.Windows.Forms.Button[]]$Buttons,
        [string]$ServiceName
    )
    $info = Get-HeimdallServiceInfo -ServiceName $ServiceName
    foreach ($btn in $Buttons) {
        if (-not $btn -or $btn.IsDisposed) { continue }
        if (-not $info.Installed) {
            $btn.Enabled = $false
            continue
        }
        if ($script:LaunchControlBusy) {
            $btn.Enabled = $false
            continue
        }
        switch ($btn.Tag) {
            "Start" { $btn.Enabled = $info.StatusText -ne "Running" }
            "Stop" { $btn.Enabled = $info.StatusText -eq "Running" }
            "Restart" { $btn.Enabled = $info.StatusText -eq "Running" }
            default { $btn.Enabled = $true }
        }
    }
}

function Update-HeimdallServiceStatusUi {
    if ($script:HeimdallServiceUi) {
        $ui = $script:HeimdallServiceUi
        Set-HeimdallServiceStatusLabel -Label $ui.ApiStatus -ServiceName "HeimdallApi"
        Set-HeimdallServiceStatusLabel -Label $ui.AgentStatus -ServiceName "HeimdallAgent"
        Set-HeimdallServiceControlButtons -Buttons $ui.ApiButtons -ServiceName "HeimdallApi"
        Set-HeimdallServiceControlButtons -Buttons $ui.AgentButtons -ServiceName "HeimdallAgent"
    }
}

function Invoke-HeimdallServiceControl {
    param(
        [Parameter(Mandatory = $true)][string]$ServiceName,
        [Parameter(Mandatory = $true)][ValidateSet("Start", "Stop", "Restart")][string]$Action
    )
    $info = Get-HeimdallServiceInfo -ServiceName $ServiceName
    if (-not $info.Installed) {
        throw "$ServiceName is not installed on this PC."
    }
    Write-HeimdallLog "Service ${Action}: $ServiceName (was $($info.StatusText))" -Level STEP
    Set-UiStatus "${Action} ${ServiceName}..."
    switch ($Action) {
        "Start" {
            Start-Service -Name $ServiceName -ErrorAction Stop
            $deadline = (Get-Date).AddSeconds(30)
            do {
                Start-Sleep -Milliseconds 200
                [System.Windows.Forms.Application]::DoEvents()
                $info = Get-HeimdallServiceInfo -ServiceName $ServiceName
            } while ($info.StatusText -ne "Running" -and (Get-Date) -lt $deadline)
            if ($info.StatusText -ne "Running") {
                throw "Timed out waiting for $ServiceName to start (status=$($info.StatusText))."
            }
        }
        "Stop" {
            Stop-Service -Name $ServiceName -Force -ErrorAction Stop
            $deadline = (Get-Date).AddSeconds(30)
            do {
                Start-Sleep -Milliseconds 200
                [System.Windows.Forms.Application]::DoEvents()
                $info = Get-HeimdallServiceInfo -ServiceName $ServiceName
            } while ($info.StatusText -ne "Stopped" -and (Get-Date) -lt $deadline)
            if ($info.StatusText -ne "Stopped") {
                throw "Timed out waiting for $ServiceName to stop (status=$($info.StatusText))."
            }
        }
        "Restart" {
            Restart-Service -Name $ServiceName -Force -ErrorAction Stop
            $deadline = (Get-Date).AddSeconds(45)
            do {
                Start-Sleep -Milliseconds 200
                [System.Windows.Forms.Application]::DoEvents()
                $info = Get-HeimdallServiceInfo -ServiceName $ServiceName
            } while ($info.StatusText -ne "Running" -and (Get-Date) -lt $deadline)
            if ($info.StatusText -ne "Running") {
                throw "Timed out waiting for $ServiceName to restart (status=$($info.StatusText))."
            }
        }
    }
    $final = Get-HeimdallServiceInfo -ServiceName $ServiceName
    Write-HeimdallLog "$ServiceName is now $($final.StatusText)" -Level OK
    Set-UiStatus "$ServiceName`: $($final.StatusText)"
    Update-HeimdallServiceStatusUi
}

function Wait-ProcessWithUiPump {
    param(
        [Parameter(Mandatory = $true)][System.Diagnostics.Process]$Process,
        [string]$StatusText = ""
    )
    if ($StatusText) { Set-UiStatus $StatusText }
    while ($Process -and -not $Process.HasExited) {
        [System.Windows.Forms.Application]::DoEvents()
        Start-Sleep -Milliseconds 150
    }
    if ($Process) {
        $Process.Refresh()
        return $Process.ExitCode
    }
    return -1
}

function Invoke-LaunchControlAction {
    param([Parameter(Mandatory = $true)][scriptblock]$Action)
    if ($script:LaunchControlBusy) { return }
    Set-LaunchControlBusy -Busy $true
    try {
        & $Action
    }
    catch {
        Write-HeimdallLog "Setup action failed: $($_.Exception.Message)" -Level ERROR
        [System.Windows.Forms.MessageBox]::Show(
            "An error occurred:`r`n$($_.Exception.Message)`r`n`r`nLog: $($script:LogPath)",
            "Heimdall Setup",
            "OK",
            "Error") | Out-Null
    }
    finally {
        Set-LaunchControlBusy -Busy $false
    }
}

# ---------------------------------------------------------------------------
# Helpers
# ---------------------------------------------------------------------------

function Test-IsAdministrator {
    $id = [Security.Principal.WindowsIdentity]::GetCurrent()
    $principal = New-Object Security.Principal.WindowsPrincipal($id)
    return $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
}

function Request-Elevation {
    param([string]$Reason)
    Write-HeimdallLog $Reason -Level ASK
    $r = [System.Windows.Forms.MessageBox]::Show(
        "$Reason`r`n`r`nRelaunch Heimdall Setup as Administrator now?",
        "Administrator required",
        [System.Windows.Forms.MessageBoxButtons]::YesNo,
        [System.Windows.Forms.MessageBoxIcon]::Warning)
    if ($r -ne [System.Windows.Forms.DialogResult]::Yes) { return $false }
    $args = "-NoProfile -ExecutionPolicy Bypass -STA -File `"$($MyInvocation.ScriptName)`""
    Start-Process -FilePath "powershell.exe" -Verb RunAs -ArgumentList $args | Out-Null
    return $true
}

function Get-DotNetSdks {
    try {
        & dotnet --list-sdks 2>$null
    } catch {
        return @()
    }
}

function Test-HasDotNet10Sdk {
    $sdks = Get-DotNetSdks
    return [bool]($sdks | Where-Object { $_ -match '^\s*10\.' })
}

function Read-LocalPackVersion {
    $candidates = @(
        (Join-Path $script:ScriptDir "VERSION.json"),
        (Join-Path $script:ScriptDir "PACKED.txt")
    )
    if ($script:RepoRoot) {
        $candidates += (Join-Path $script:RepoRoot "dist\Heimdall-Client\VERSION.json")
        $candidates += (Join-Path $script:RepoRoot "dist\Heimdall-Client\PACKED.txt")
        $candidates += (Join-Path $script:RepoRoot "dist\workstation-collector\VERSION.json")
        $candidates += (Join-Path $script:RepoRoot "dist\workstation-collector\PACKED.txt")
    }
    foreach ($c in $candidates) {
        if (-not (Test-Path $c)) { continue }
        if ($c -like "*.json") {
            try {
                return Get-Content -Raw -Path $c | ConvertFrom-Json
            } catch {
                Write-HeimdallLog "Could not parse VERSION.json: $c - $($_.Exception.Message)" -Level WARN
            }
        }
        else {
            return [pscustomobject]@{
                productVersion = $script:ProductVersionExpected
                packedAt       = (Get-Content -Path $c -TotalCount 5 | Out-String).Trim()
                source         = $c
            }
        }
    }
    return $null
}

function Test-ApiHealth {
    param(
        [Parameter(Mandatory)][string]$ApiUrl,
        [int]$TimeoutSec = 10
    )
    $base = $ApiUrl.TrimEnd("/")
    $uri = "$base/api/health"
    try {
        $resp = Invoke-RestMethod -Uri $uri -Method Get -TimeoutSec $TimeoutSec
        return [pscustomobject]@{
            Ok      = $true
            Uri     = $uri
            Payload = $resp
            Error   = $null
        }
    }
    catch {
        return [pscustomobject]@{
            Ok      = $false
            Uri     = $uri
            Payload = $null
            Error   = $_.Exception.Message
        }
    }
}

function Test-ApiConfigAuth {
    param(
        [Parameter(Mandatory)][string]$ApiUrl,
        [Parameter(Mandatory)][string]$ApiKey,
        [string]$Hostname = $env:COMPUTERNAME
    )
    $base = $ApiUrl.TrimEnd("/")
    $uri = "$base/api/config/$([uri]::EscapeDataString($Hostname))"
    try {
        $headers = @{ "X-Heimdall-Key" = $ApiKey }
        $null = Invoke-RestMethod -Uri $uri -Headers $headers -Method Get -TimeoutSec 15
        return [pscustomobject]@{ Ok = $true; Uri = $uri; Status = 200; Error = $null }
    }
    catch {
        $status = $null
        if ($_.Exception.Response) {
            try { $status = [int]$_.Exception.Response.StatusCode } catch { }
        }
        return [pscustomobject]@{
            Ok     = $false
            Uri    = $uri
            Status = $status
            Error  = $_.Exception.Message
        }
    }
}

function Normalize-ApiUrl {
    param([string]$Url)
    if ([string]::IsNullOrWhiteSpace($Url)) { return "" }
    return $Url.Trim().TrimEnd("/")
}

function Test-ApiUrlLooksLocalhost {
    param([Parameter(Mandatory)][string]$ApiUrl)
    try {
        $uri = [uri]$ApiUrl
        $h = $uri.Host
        return ($h -eq "localhost" -or $h -eq "127.0.0.1" -or $h -eq "::1")
    }
    catch {
        return ($ApiUrl -match "localhost|127\.0\.0\.1")
    }
}

function Get-ApiHostFromUrl {
    param([string]$ApiUrl)
    if ([string]::IsNullOrWhiteSpace($ApiUrl)) { return $null }
    try {
        return ([uri]$ApiUrl).Host
    }
    catch {
        return $null
    }
}

function Mask-SecretValue {
    param([string]$Value)
    if ([string]::IsNullOrWhiteSpace($Value)) { return "(empty)" }
    if ($Value.Length -le 4) { return "****" }
    return ("****" + $Value.Substring($Value.Length - 4))
}

function Get-LastInstallSettings {
    $path = $script:LastInstallSettingsFile
    if (-not (Test-Path $path)) { return $null }
    try {
        $raw = Get-Content -Raw -Path $path -Encoding UTF8
        if ([string]::IsNullOrWhiteSpace($raw)) { return $null }
        return ($raw | ConvertFrom-Json)
    }
    catch {
        Write-HeimdallLog "Could not read last install settings: $($_.Exception.Message)" -Level WARN
        return $null
    }
}

function Save-LastInstallSettings {
    param(
        [Parameter(Mandatory)][string]$ApiUrl,
        [Parameter(Mandatory)][string]$MachineGroup
    )
    $path = $script:LastInstallSettingsFile
    $dir = Split-Path -Parent $path
    if (-not (Test-Path $dir)) {
        New-Item -ItemType Directory -Path $dir -Force | Out-Null
    }
    $entry = [pscustomobject]@{
        apiUrl       = (Normalize-ApiUrl $ApiUrl)
        machineGroup = $MachineGroup.Trim()
        savedAt      = (Get-Date -Format "yyyy-MM-ddTHH:mm:ss")
        savedFrom    = $env:COMPUTERNAME
    }
    $json = $entry | ConvertTo-Json -Depth 3
    $utf8Bom = New-Object System.Text.UTF8Encoding $true
    [System.IO.File]::WriteAllText($path, $json, $utf8Bom)
    Write-HeimdallLog "Saved last install settings for future prefills: $($entry.apiUrl)" -Level INFO
}

function Get-DefaultCollectorApiUrl {
    return Resolve-HeimdallDefaultCollectorApiUrl -LastInstallSettingsFile $script:LastInstallSettingsFile -Log {
        param([string]$Message, [string]$Level)
        Write-HeimdallLog $Message -Level $Level
    }
}

function Get-DefaultCollectorMachineGroup {
    $last = Get-LastInstallSettings
    if ($last -and $last.machineGroup) {
        return [string]$last.machineGroup
    }
    return "POC"
}

function Read-AgentAppSettingsFromDisk {
    $path = $script:AgentAppSettingsPath
    if (-not (Test-Path $path)) {
        return [pscustomobject]@{
            Ok      = $false
            Path    = $path
            Error   = "appsettings.json not found"
            ApiBaseUrl = $null
            ApiKey  = $null
            MachineGroup = $null
            QueuePath = $null
        }
    }
    try {
        $json = Get-Content -Raw -Path $path -Encoding UTF8 | ConvertFrom-Json
        $h = $json.Heimdall
        return [pscustomobject]@{
            Ok           = $true
            Path         = $path
            Error        = $null
            ApiBaseUrl   = if ($h) { [string]$h.ApiBaseUrl } else { $null }
            ApiKey       = if ($h) { [string]$h.ApiKey } else { $null }
            MachineGroup = if ($h) { [string]$h.MachineGroup } else { $null }
            QueuePath    = if ($h) { [string]$h.QueuePath } else { $null }
        }
    }
    catch {
        return [pscustomobject]@{
            Ok      = $false
            Path    = $path
            Error   = $_.Exception.Message
            ApiBaseUrl = $null
            ApiKey  = $null
            MachineGroup = $null
            QueuePath = $null
        }
    }
}

function Get-DiagnosticsDropUncRoot {
    $path = $script:DiagnosticsDropFile
    if (-not (Test-Path $path)) { return $null }
    try {
        $raw = Get-Content -Raw -Path $path -Encoding UTF8
        $cfg = $raw | ConvertFrom-Json
        if ($cfg -and $cfg.uncRoot) {
            return [string]$cfg.uncRoot
        }
    }
    catch {
        Write-HeimdallLog "Could not read diagnostics-drop.json: $($_.Exception.Message)" -Level WARN
    }
    return $null
}

function Resolve-ClientCheckNetworkDropDir {
    param([string]$ApiBaseUrl)
    $hostname = $env:COMPUTERNAME
    $customRoot = Get-DiagnosticsDropUncRoot
    if ($customRoot) {
        return (Join-Path $customRoot.TrimEnd("\") $hostname)
    }
    $apiHost = Get-ApiHostFromUrl -ApiUrl $ApiBaseUrl
    if (-not $apiHost) { return $null }
    if (Test-ApiUrlLooksLocalhost -ApiUrl $ApiBaseUrl) { return $null }
    return "\\$apiHost\C$\ProgramData\Heimdall\logs\clients\$hostname"
}

function Copy-ClientCheckLogsToNetwork {
    param(
        [Parameter(Mandatory)][string[]]$LocalFiles,
        [string]$ApiBaseUrl
    )
    $dropDir = Resolve-ClientCheckNetworkDropDir -ApiBaseUrl $ApiBaseUrl
    if (-not $dropDir) {
        if ($ApiBaseUrl -and (Test-ApiUrlLooksLocalhost -ApiUrl $ApiBaseUrl)) {
            Write-HeimdallLog "Network drop skipped: ApiBaseUrl is localhost (no remote API host for C$ share)." -Level WARN
        }
        else {
            Write-HeimdallLog "Network drop skipped: could not derive UNC path from ApiBaseUrl." -Level WARN
        }
        return $false
    }
    Write-HeimdallLog "Attempting network drop: $dropDir" -Level INFO
    try {
        if (-not (Test-Path -LiteralPath $dropDir)) {
            New-Item -ItemType Directory -Path $dropDir -Force | Out-Null
        }
        foreach ($f in $LocalFiles) {
            if (-not (Test-Path -LiteralPath $f)) { continue }
            $dest = Join-Path $dropDir (Split-Path -Leaf $f)
            Copy-Item -LiteralPath $f -Destination $dest -Force
            Write-HeimdallLog "Copied to network drop: $dest" -Level OK
        }
        return $true
    }
    catch {
        Write-HeimdallLog "Network drop failed ($dropDir): $($_.Exception.Message)" -Level WARN
        Write-HeimdallLog "Local client-check logs are still saved under $($script:LogRoot)." -Level INFO
        return $false
    }
}

function Invoke-ClientHealthCheck {
    param(
        [string]$LogPrefix = "client-check"
    )

    if (-not (Test-Path $script:LogRoot)) {
        New-Item -ItemType Directory -Path $script:LogRoot -Force | Out-Null
    }
    $stamp = Get-Date -Format "yyyyMMdd-HHmmss"
    $checkLogPath = Join-Path $script:LogRoot "$LogPrefix-$stamp.log"
    $summaryPath = Join-Path $script:LogRoot "$LogPrefix-$stamp.txt"

    $summaryLines = New-Object System.Collections.Generic.List[string]
    $checkStats = [pscustomobject]@{ Warn = 0; Error = 0 }

    function Write-CheckLine {
        param(
            [Parameter(Mandatory)][string]$Message,
            [ValidateSet("INFO", "WARN", "ERROR", "OK", "STEP")]
            [string]$Level = "INFO"
        )
        $ts = Get-Date -Format "yyyy-MM-dd HH:mm:ss"
        $line = "[$ts] [$Level] $Message"
        Add-Content -Path $checkLogPath -Value $line -Encoding UTF8
        $summaryLines.Add("[$Level] $Message")
        if ($Level -eq "WARN") { $checkStats.Warn++ }
        if ($Level -eq "ERROR") { $checkStats.Error++ }
        Write-HeimdallLog $Message -Level $Level
    }

    $header = @"

================================================================
  Heimdall client health / connect check
================================================================
Machine: $env:COMPUTERNAME
User: $env:USERNAME
Started: $(Get-Date -Format "yyyy-MM-dd HH:mm:ss")
Log: $checkLogPath

"@
    Set-Content -Path $checkLogPath -Value $header -Encoding UTF8
    Write-HeimdallLog "Client health check started. Dedicated log: $checkLogPath" -Level STEP

    # 1. Service status
    Write-CheckLine "1. HeimdallAgent service" -Level STEP
    $svc = Get-Service -Name HeimdallAgent -ErrorAction SilentlyContinue
    if (-not $svc) {
        Write-CheckLine "HeimdallAgent service: NOT INSTALLED" -Level ERROR
    }
    elseif ($svc.Status -eq "Running") {
        Write-CheckLine "HeimdallAgent service: Running (StartType=$($svc.StartType))" -Level OK
    }
    else {
        Write-CheckLine "HeimdallAgent service: $($svc.Status) (StartType=$($svc.StartType))" -Level WARN
    }

    # 2. appsettings.json
    Write-CheckLine "2. Agent appsettings.json" -Level STEP
    $settings = Read-AgentAppSettingsFromDisk
    if (-not $settings.Ok) {
        Write-CheckLine "Could not read $($settings.Path): $($settings.Error)" -Level ERROR
        $apiBaseUrl = $null
        $apiKey = $null
        $queuePath = Join-Path $env:ProgramData "Heimdall\queue.db"
    }
    else {
        $apiBaseUrl = $settings.ApiBaseUrl
        $apiKey = $settings.ApiKey
        $queuePath = if ($settings.QueuePath) { $settings.QueuePath } else { Join-Path $env:ProgramData "Heimdall\queue.db" }
        Write-CheckLine "Path: $($settings.Path)" -Level INFO
        Write-CheckLine "ApiBaseUrl: $apiBaseUrl" -Level INFO
        Write-CheckLine "ApiKey: $(Mask-SecretValue $apiKey)" -Level INFO
        Write-CheckLine "MachineGroup: $($settings.MachineGroup)" -Level INFO
        Write-CheckLine "QueuePath: $queuePath" -Level INFO
    }

    # 3. localhost warning
    if ($apiBaseUrl -and (Test-ApiUrlLooksLocalhost -ApiUrl $apiBaseUrl)) {
        Write-CheckLine "WARN: ApiBaseUrl looks like localhost/127.0.0.1. Agents on remote PCs must use the API server hostname or IP, not localhost." -Level WARN
    }

    # 4. GET /api/health
    Write-CheckLine "3. API health probe" -Level STEP
    if ($apiBaseUrl) {
        $health = Test-ApiHealth -ApiUrl $apiBaseUrl
        if ($health.Ok) {
            $pv = $health.Payload.productVersion
            $mn = $health.Payload.machineName
            Write-CheckLine "GET $($health.Uri) OK - productVersion=$pv serverMachine=$mn" -Level OK
        }
        else {
            Write-CheckLine "GET $($health.Uri) FAILED: $($health.Error)" -Level ERROR
        }
    }
    else {
        Write-CheckLine "Skipped health probe (no ApiBaseUrl)" -Level WARN
        $health = [pscustomobject]@{ Ok = $false }
    }

    # 5. GET /api/config/{hostname}
    Write-CheckLine "4. API config auth probe" -Level STEP
    if ($apiBaseUrl -and $apiKey) {
        $auth = Test-ApiConfigAuth -ApiUrl $apiBaseUrl -ApiKey $apiKey
        if ($auth.Ok) {
            Write-CheckLine "GET $($auth.Uri) OK (X-Heimdall-Key accepted)" -Level OK
        }
        else {
            Write-CheckLine "GET $($auth.Uri) FAILED status=$($auth.Status): $($auth.Error)" -Level ERROR
        }
    }
    else {
        Write-CheckLine "Skipped config auth probe (missing ApiBaseUrl or ApiKey)" -Level WARN
        $auth = [pscustomobject]@{ Ok = $false }
    }

    # 6. Queue.db
    Write-CheckLine "5. Offline queue (queue.db)" -Level STEP
    if ($queuePath -and (Test-Path -LiteralPath $queuePath)) {
        $fi = Get-Item -LiteralPath $queuePath
        $sizeKb = [Math]::Round($fi.Length / 1KB, 1)
        Write-CheckLine "Queue.db present: $queuePath size=${sizeKb} KB lastWrite=$($fi.LastWriteTime)" -Level INFO
    }
    else {
        Write-CheckLine "Queue.db not found at: $queuePath" -Level INFO
    }

    # 7. Application log (Heimdall lines)
    Write-CheckLine "6. Recent Application log (Heimdall)" -Level STEP
    $eventLines = 0
    try {
        $since = (Get-Date).AddHours(-48)
        $events = Get-WinEvent -FilterHashtable @{
            LogName   = "Application"
            StartTime = $since
        } -MaxEvents 300 -ErrorAction Stop
        $matches = @($events | Where-Object { $_.Message -match "Heimdall" } | Select-Object -First 8)
        if ($matches.Count -eq 0) {
            Write-CheckLine "No Application log entries containing 'Heimdall' in the last 48 hours." -Level INFO
        }
        else {
            foreach ($ev in $matches) {
                $msg = ($ev.Message -replace "`r?`n", " ").Trim()
                if ($msg.Length -gt 180) { $msg = $msg.Substring(0, 180) + "..." }
                Write-CheckLine "  [$($ev.TimeCreated.ToString('yyyy-MM-dd HH:mm'))] Id=$($ev.Id) $msg" -Level INFO
                $eventLines++
            }
        }
    }
    catch {
        try {
            $legacy = Get-EventLog -LogName Application -After $since -ErrorAction Stop |
                Where-Object { $_.Message -match "Heimdall" } |
                Select-Object -First 8
            if (-not $legacy -or $legacy.Count -eq 0) {
                Write-CheckLine "No Heimdall Application log entries (legacy query)." -Level INFO
            }
            else {
                foreach ($ev in $legacy) {
                    $msg = ($ev.Message -replace "`r?`n", " ").Trim()
                    if ($msg.Length -gt 180) { $msg = $msg.Substring(0, 180) + "..." }
                    Write-CheckLine "  [$($ev.TimeGenerated.ToString('yyyy-MM-dd HH:mm'))] Id=$($ev.EventID) $msg" -Level INFO
                    $eventLines++
                }
            }
        }
        catch {
            Write-CheckLine "Could not read Application log: $($_.Exception.Message)" -Level WARN
        }
    }

    $overallOk = $svc -and $svc.Status -eq "Running" -and $settings.Ok -and $health.Ok -and $auth.Ok
    $resultText = if ($overallOk) { "PASS" } else { "ISSUES FOUND" }
    Write-CheckLine "Client check complete: $resultText (errors=$($checkStats.Error) warnings=$($checkStats.Warn))" -Level $(if ($overallOk) { "OK" } else { "WARN" })

    $summaryBody = @(
        "Heimdall client health check - $env:COMPUTERNAME",
        "Time: $(Get-Date -Format 'yyyy-MM-dd HH:mm:ss')",
        "Result: $resultText",
        "",
        ($summaryLines -join [Environment]::NewLine),
        "",
        "Full log: $checkLogPath"
    ) -join [Environment]::NewLine
    $utf8Bom = New-Object System.Text.UTF8Encoding $true
    [System.IO.File]::WriteAllText($summaryPath, $summaryBody, $utf8Bom)

    Copy-ClientCheckLogsToNetwork -LocalFiles @($checkLogPath, $summaryPath) -ApiBaseUrl $apiBaseUrl

    return [pscustomobject]@{
        Ok          = $overallOk
        LogPath     = $checkLogPath
        SummaryPath = $summaryPath
        ErrorCount  = $checkStats.Error
        WarnCount   = $checkStats.Warn
    }
}

function Show-InputForm {
    param(
        [string]$Title,
        [string]$Prompt,
        [System.Collections.IDictionary]$Fields,   # name -> default (use [ordered]@{})
        [string]$AcceptLabel = "Continue"
    )

    $form = New-Object System.Windows.Forms.Form
    $form.Text = $Title
    $form.StartPosition = "CenterParent"
    $form.FormBorderStyle = "FixedDialog"
    $form.MaximizeBox = $false
    $form.MinimizeBox = $false
    $form.Width = 560
    $form.Height = 160 + (40 * $Fields.Count)
    $form.Font = New-Object System.Drawing.Font("Segoe UI", 9)

    $lbl = New-Object System.Windows.Forms.Label
    $lbl.Text = $Prompt
    $lbl.Left = 16
    $lbl.Top = 12
    $lbl.Width = 520
    $lbl.Height = 48
    $form.Controls.Add($lbl)

    $boxes = @{}
    $y = 68
    foreach ($key in $Fields.Keys) {
        $fl = New-Object System.Windows.Forms.Label
        $fl.Text = $key
        $fl.Left = 16
        $fl.Top = $y + 3
        $fl.Width = 120
        $form.Controls.Add($fl)

        $tb = New-Object System.Windows.Forms.TextBox
        $tb.Left = 140
        $tb.Top = $y
        $tb.Width = 380
        $tb.Text = [string]$Fields[$key]
        if ($key -match "Key|Password|Secret") { $tb.UseSystemPasswordChar = $false }
        $form.Controls.Add($tb)
        $boxes[$key] = $tb
        $y += 36
    }

    $ok = New-Object System.Windows.Forms.Button
    $ok.Text = $AcceptLabel
    $ok.Left = 320
    $ok.Top = $y + 12
    $ok.Width = 100
    $ok.DialogResult = [System.Windows.Forms.DialogResult]::OK
    $form.AcceptButton = $ok
    $form.Controls.Add($ok)

    $cancel = New-Object System.Windows.Forms.Button
    $cancel.Text = "Cancel"
    $cancel.Left = 430
    $cancel.Top = $y + 12
    $cancel.Width = 90
    $cancel.DialogResult = [System.Windows.Forms.DialogResult]::Cancel
    $form.CancelButton = $cancel
    $form.Controls.Add($cancel)

    $result = $form.ShowDialog()
    if ($result -ne [System.Windows.Forms.DialogResult]::OK) { return $null }

    $out = @{}
    foreach ($key in $boxes.Keys) {
        $out[$key] = $boxes[$key].Text.Trim()
    }
    return $out
}

function Invoke-ExternalLogged {
    param(
        [Parameter(Mandatory)][string]$FilePath,
        [string]$ArgumentList = "",
        [string]$WorkingDirectory = $script:ScriptDir,
        [int]$TimeoutMs = 0
    )
    Write-HeimdallLog "Running: $FilePath $ArgumentList" -Level INFO
    $psi = New-Object System.Diagnostics.ProcessStartInfo
    $psi.FileName = $FilePath
    $psi.Arguments = $ArgumentList
    $psi.WorkingDirectory = $WorkingDirectory
    $psi.UseShellExecute = $false
    $psi.RedirectStandardOutput = $true
    $psi.RedirectStandardError = $true
    $psi.CreateNoWindow = $true

    $p = New-Object System.Diagnostics.Process
    $p.StartInfo = $psi
    $null = $p.Start()
    $stdout = $p.StandardOutput.ReadToEnd()
    $stderr = $p.StandardError.ReadToEnd()
    $p.WaitForExit()

    if ($stdout) {
        foreach ($line in ($stdout -split "`r?`n")) {
            if ($line) { Write-HeimdallLog $line -Level INFO }
        }
    }
    if ($stderr) {
        foreach ($line in ($stderr -split "`r?`n")) {
            if ($line) { Write-HeimdallLog $line -Level WARN }
        }
    }
    Write-HeimdallLog "Exit code: $($p.ExitCode)" -Level $(if ($p.ExitCode -eq 0) { "OK" } else { "ERROR" })
    return $p.ExitCode
}

function Open-LogsFolder {
    if (-not (Test-Path $script:LogRoot)) {
        New-Item -ItemType Directory -Path $script:LogRoot -Force | Out-Null
    }
    Start-Process explorer.exe $script:LogRoot
    Write-HeimdallLog "Opened logs folder: $($script:LogRoot)" -Level OK
}

function Get-RemoteLogTargets {
    $path = $script:RemoteLogTargetsFile
    if (-not (Test-Path $path)) { return @() }
    try {
        $raw = Get-Content -Raw -Path $path -Encoding UTF8
        if ([string]::IsNullOrWhiteSpace($raw)) { return @() }
        $items = $raw | ConvertFrom-Json
        if ($null -eq $items) { return @() }
        if ($items -is [System.Array]) { return @($items) }
        return @($items)
    }
    catch {
        Write-HeimdallLog "Could not read remote log targets: $($_.Exception.Message)" -Level WARN
        return @()
    }
}

function Save-RemoteLogTargets {
    param([array]$Targets)
    $path = $script:RemoteLogTargetsFile
    $dir = Split-Path -Parent $path
    if (-not (Test-Path $dir)) {
        New-Item -ItemType Directory -Path $dir -Force | Out-Null
    }
    $json = if ($Targets.Count -eq 0) { "[]" } else { $Targets | ConvertTo-Json -Depth 3 }
    $utf8Bom = New-Object System.Text.UTF8Encoding $true
    [System.IO.File]::WriteAllText($path, $json, $utf8Bom)
}

function Add-RemoteLogTarget {
    param(
        [Parameter(Mandatory)][string]$TargetHost,
        [Parameter(Mandatory)][string]$UncPath
    )
    $hostNorm = $TargetHost.Trim()
    $uncNorm = $UncPath.Trim()
    $existing = @(Get-RemoteLogTargets)
    $filtered = @($existing | Where-Object {
        $_.host -ne $hostNorm -and $_.uncPath -ne $uncNorm
    })
    $entry = [pscustomobject]@{
        host     = $hostNorm
        uncPath  = $uncNorm
        lastUsed = (Get-Date -Format "yyyy-MM-ddTHH:mm:ss")
    }
    $updated = @($entry) + $filtered
    if ($updated.Count -gt $script:RemoteLogTargetsMax) {
        $updated = $updated[0..($script:RemoteLogTargetsMax - 1)]
    }
    Save-RemoteLogTargets -Targets $updated
    Write-HeimdallLog "Saved remote log target: $hostNorm -> $uncNorm" -Level INFO
}

function Remove-RemoteLogTarget {
    param([Parameter(Mandatory)][string]$TargetHost)
    $hostNorm = $TargetHost.Trim()
    $existing = @(Get-RemoteLogTargets)
    $updated = @($existing | Where-Object { $_.host -ne $hostNorm })
    Save-RemoteLogTargets -Targets $updated
    Write-HeimdallLog "Removed remote log target: $hostNorm" -Level INFO
}

# --- Push client pack: remembered hosts + named groups ---
# Two small JSON stores under %LOCALAPPDATA%\Heimdall (per-user, matches RemoteLogTargets pattern):
#   push-hosts.json  -> [ { host, lastUsed, lastResult } ]                 (flat, de-duplicated by host)
#   push-groups.json -> [ { name, hosts: [hostA, hostB, ...] } ]           (hosts reference push-hosts entries)
# Groups only store hostnames (strings); a host can belong to zero, one, or many groups.

function ConvertTo-PushHostList {
    <# Parses a comma / semicolon / newline separated block of text into a distinct, trimmed host list. #>
    param([string]$RawText)
    if ([string]::IsNullOrWhiteSpace($RawText)) { return @() }
    $parts = $RawText -split '[,;\r\n]+'
    $result = New-Object System.Collections.Generic.List[string]
    $seen = New-Object System.Collections.Generic.HashSet[string]([System.StringComparer]::OrdinalIgnoreCase)
    foreach ($p in $parts) {
        $h = $p.Trim().TrimEnd('\')
        if ([string]::IsNullOrWhiteSpace($h)) { continue }
        if ($h -match '^\\\\') {
            $h = $h -replace '^\\\\([^\\]+).*$', '$1'
        }
        if ($seen.Add($h)) {
            [void]$result.Add($h)
        }
    }
    return @($result)
}

function Get-PushHosts {
    $path = $script:PushHostsFile
    if (-not (Test-Path $path)) { return @() }
    try {
        $raw = Get-Content -Raw -Path $path -Encoding UTF8
        if ([string]::IsNullOrWhiteSpace($raw)) { return @() }
        $items = $raw | ConvertFrom-Json
        if ($null -eq $items) { return @() }
        return @($items)
    }
    catch {
        Write-HeimdallLog "Could not read push hosts: $($_.Exception.Message)" -Level WARN
        return @()
    }
}

function Save-PushHosts {
    param([array]$Hosts)
    $path = $script:PushHostsFile
    $dir = Split-Path -Parent $path
    if (-not (Test-Path $dir)) {
        New-Item -ItemType Directory -Path $dir -Force | Out-Null
    }
    $json = if ($Hosts.Count -eq 0) { "[]" } else { $Hosts | ConvertTo-Json -Depth 3 }
    $utf8Bom = New-Object System.Text.UTF8Encoding $true
    [System.IO.File]::WriteAllText($path, $json, $utf8Bom)
}

function Add-PushHost {
    <# Remembers a host (and optional last push result) for reuse across sessions — does not touch group membership. #>
    param(
        [Parameter(Mandatory)][string]$TargetHost,
        [string]$LastResult
    )
    $hostNorm = $TargetHost.Trim().TrimEnd('\')
    if ([string]::IsNullOrWhiteSpace($hostNorm)) { return }
    $existing = @(Get-PushHosts)
    $filtered = @($existing | Where-Object { $_.host -ne $hostNorm })
    $entry = [pscustomobject]@{
        host       = $hostNorm
        lastUsed   = (Get-Date -Format "yyyy-MM-ddTHH:mm:ss")
        lastResult = if ($LastResult) { $LastResult } else { "" }
    }
    $updated = @($entry) + $filtered
    if ($updated.Count -gt $script:PushHostsMax) {
        $updated = $updated[0..($script:PushHostsMax - 1)]
    }
    Save-PushHosts -Hosts $updated
}

function Remove-PushHost {
    param([Parameter(Mandatory)][string]$TargetHost)
    $hostNorm = $TargetHost.Trim()
    $existing = @(Get-PushHosts)
    $updated = @($existing | Where-Object { $_.host -ne $hostNorm })
    Save-PushHosts -Hosts $updated
    # Also drop from any group membership so groups don't reference a forgotten host.
    foreach ($g in (Get-PushGroups)) {
        Remove-HostsFromPushGroup -GroupName $g.name -HostsToRemove @($hostNorm) | Out-Null
    }
    Write-HeimdallLog "Forgot push host: $hostNorm" -Level INFO
}

function Get-PushGroups {
    $path = $script:PushGroupsFile
    if (-not (Test-Path $path)) { return @() }
    try {
        $raw = Get-Content -Raw -Path $path -Encoding UTF8
        if ([string]::IsNullOrWhiteSpace($raw)) { return @() }
        $items = $raw | ConvertFrom-Json
        if ($null -eq $items) { return @() }
        $normalized = @()
        foreach ($g in @($items)) {
            $normalized += [pscustomobject]@{
                name  = [string]$g.name
                hosts = @($g.hosts)
            }
        }
        return @($normalized | Sort-Object -Property name)
    }
    catch {
        Write-HeimdallLog "Could not read push groups: $($_.Exception.Message)" -Level WARN
        return @()
    }
}

function Save-PushGroups {
    param([array]$Groups)
    $path = $script:PushGroupsFile
    $dir = Split-Path -Parent $path
    if (-not (Test-Path $dir)) {
        New-Item -ItemType Directory -Path $dir -Force | Out-Null
    }
    $json = if ($Groups.Count -eq 0) { "[]" } else { $Groups | ConvertTo-Json -Depth 4 }
    $utf8Bom = New-Object System.Text.UTF8Encoding $true
    [System.IO.File]::WriteAllText($path, $json, $utf8Bom)
}

function Add-PushGroup {
    param([Parameter(Mandatory)][string]$Name)
    $nameNorm = $Name.Trim()
    if ([string]::IsNullOrWhiteSpace($nameNorm)) { return $false }
    $existing = @(Get-PushGroups)
    if ($existing | Where-Object { $_.name -eq $nameNorm }) {
        return $false
    }
    $updated = $existing + [pscustomobject]@{ name = $nameNorm; hosts = @() }
    Save-PushGroups -Groups $updated
    Write-HeimdallLog "Created push group: $nameNorm" -Level INFO
    return $true
}

function Remove-PushGroup {
    param([Parameter(Mandatory)][string]$Name)
    $nameNorm = $Name.Trim()
    $existing = @(Get-PushGroups)
    $updated = @($existing | Where-Object { $_.name -ne $nameNorm })
    Save-PushGroups -Groups $updated
    Write-HeimdallLog "Deleted push group: $nameNorm" -Level INFO
}

function Set-PushGroupMembers {
    <# Replaces a group's member host list wholesale (used by the Manage groups dialog). #>
    param(
        [Parameter(Mandatory)][string]$GroupName,
        [string[]]$Hosts
    )
    $nameNorm = $GroupName.Trim()
    $existing = @(Get-PushGroups)
    $updated = @()
    foreach ($g in $existing) {
        if ($g.name -eq $nameNorm) {
            $updated += [pscustomobject]@{ name = $g.name; hosts = @($Hosts | Select-Object -Unique) }
        }
        else {
            $updated += $g
        }
    }
    Save-PushGroups -Groups $updated
}

function Remove-HostsFromPushGroup {
    param(
        [Parameter(Mandatory)][string]$GroupName,
        [Parameter(Mandatory)][string[]]$HostsToRemove
    )
    $existing = @(Get-PushGroups)
    $group = $existing | Where-Object { $_.name -eq $GroupName }
    if (-not $group) { return }
    $remaining = @($group.hosts | Where-Object { $HostsToRemove -notcontains $_ })
    Set-PushGroupMembers -GroupName $GroupName -Hosts $remaining
}

function Get-PushGroupsForHost {
    param([Parameter(Mandatory)][string]$TargetHost)
    $names = @()
    foreach ($g in (Get-PushGroups)) {
        if (@($g.hosts) -contains $TargetHost) { $names += $g.name }
    }
    return $names
}

function Resolve-RemoteLogsUncPath {
    param([Parameter(Mandatory)][string]$HostOrIp)
    $inputText = $HostOrIp.Trim().TrimEnd('\')
    if ([string]::IsNullOrWhiteSpace($inputText)) { return $null }

    if ($inputText -match '^\\\\') {
        if ($inputText -match '\\logs\s*$') { return $inputText }
        if ($inputText -match 'ProgramData\\Heimdall\s*$') { return "$inputText\logs" }
        return $inputText
    }

    $saved = Get-RemoteLogTargets | Where-Object { $_.host -eq $inputText } | Select-Object -First 1
    if ($saved) { return $saved.uncPath }

    return "\\$inputText\C$\ProgramData\Heimdall\logs"
}

function Open-RemoteLogsFolder {
    Write-HeimdallLog "Open remote logs folder" -Level STEP

    $form = New-Object System.Windows.Forms.Form
    $form.Text = "Open remote logs folder"
    $form.StartPosition = "CenterParent"
    $form.FormBorderStyle = "FixedDialog"
    $form.MaximizeBox = $false
    $form.MinimizeBox = $false
    $form.Width = 560
    $form.Height = 220
    $form.Font = New-Object System.Drawing.Font("Segoe UI", 9)

    $lbl = New-Object System.Windows.Forms.Label
    $lbl.Text = "Machine name or IP (admin share C$ required):"
    $lbl.Left = 16
    $lbl.Top = 16
    $lbl.Width = 520
    $lbl.Height = 20
    $form.Controls.Add($lbl)

    $combo = New-Object System.Windows.Forms.ComboBox
    $combo.Left = 16
    $combo.Top = 42
    $combo.Width = 520
    $combo.DropDownStyle = "DropDown"
    $combo.AutoCompleteMode = "SuggestAppend"
    $combo.AutoCompleteSource = "ListItems"
    foreach ($t in (Get-RemoteLogTargets)) {
        [void]$combo.Items.Add($t.host)
    }
    if ($combo.Items.Count -gt 0) { $combo.SelectedIndex = 0 }
    $form.Controls.Add($combo)

    $hint = New-Object System.Windows.Forms.Label
    $hint.Text = "Opens \\HOST\C$\ProgramData\Heimdall\logs in Explorer. Client health checks from agents land in logs\clients\<hostname>\. Recent targets saved per user."
    $hint.Left = 16
    $hint.Top = 72
    $hint.Width = 520
    $hint.Height = 32
    $form.Controls.Add($hint)

    $openBtn = New-Object System.Windows.Forms.Button
    $openBtn.Text = "Open"
    $openBtn.Left = 250
    $openBtn.Top = 118
    $openBtn.Width = 90
    $openBtn.DialogResult = [System.Windows.Forms.DialogResult]::OK
    $form.AcceptButton = $openBtn
    $form.Controls.Add($openBtn)

    $removeBtn = New-Object System.Windows.Forms.Button
    $removeBtn.Text = "Remove"
    $removeBtn.Left = 350
    $removeBtn.Top = 118
    $removeBtn.Width = 90
    $form.Controls.Add($removeBtn)

    $cancelBtn = New-Object System.Windows.Forms.Button
    $cancelBtn.Text = "Cancel"
    $cancelBtn.Left = 450
    $cancelBtn.Top = 118
    $cancelBtn.Width = 90
    $cancelBtn.DialogResult = [System.Windows.Forms.DialogResult]::Cancel
    $form.CancelButton = $cancelBtn
    $form.Controls.Add($cancelBtn)

    $removeBtn.Add_Click({
        $hostText = $combo.Text.Trim()
        if ([string]::IsNullOrWhiteSpace($hostText)) {
            [System.Windows.Forms.MessageBox]::Show(
                "Select or type a hostname to remove from the recent list.",
                "Remove target",
                "OK",
                "Information") | Out-Null
            return
        }
        Remove-RemoteLogTarget -TargetHost $hostText
        $combo.Items.Clear()
        foreach ($t in (Get-RemoteLogTargets)) {
            [void]$combo.Items.Add($t.host)
        }
        $combo.Text = ""
        if ($combo.Items.Count -gt 0) { $combo.SelectedIndex = 0 }
    })

    $result = $form.ShowDialog()
    if ($result -ne [System.Windows.Forms.DialogResult]::OK) {
        Write-HeimdallLog "Remote logs open cancelled." -Level WARN
        return
    }

    $hostInput = $combo.Text.Trim()
    if ([string]::IsNullOrWhiteSpace($hostInput)) {
        [System.Windows.Forms.MessageBox]::Show(
            "Enter a machine name or IP address.",
            "Missing input",
            "OK",
            "Warning") | Out-Null
        return
    }

    $uncPath = Resolve-RemoteLogsUncPath -HostOrIp $hostInput
    $dataRootUnc = $uncPath -replace '\\logs\s*$', ''

    Write-HeimdallLog "Remote logs attempt: host=$hostInput path=$uncPath" -Level INFO

    if (-not (Test-Path -LiteralPath $uncPath)) {
        Write-HeimdallLog "Remote logs path unreachable: $uncPath" -Level ERROR
        [System.Windows.Forms.MessageBox]::Show(
            "Cannot reach:`r`n$uncPath`r`n`r`nCheck:`r`n- Machine name or IP is correct`r`n- You have admin rights on the remote PC`r`n- File and Printer Sharing / SMB (port 445) is allowed`r`n- Windows Firewall on the target allows admin shares`r`n`r`nFallback folder (parent):`r`n$dataRootUnc",
            "Remote logs unreachable",
            "OK",
            "Error") | Out-Null
        return
    }

    try {
        Start-Process explorer.exe $uncPath
        Add-RemoteLogTarget -TargetHost $hostInput -UncPath $uncPath
        Write-HeimdallLog "Opened remote logs: $uncPath" -Level OK
        Set-UiStatus "Opened remote logs: $hostInput"
    }
    catch {
        Write-HeimdallLog "Failed to open Explorer for $uncPath : $($_.Exception.Message)" -Level ERROR
        [System.Windows.Forms.MessageBox]::Show(
            "Explorer failed to open:`r`n$uncPath`r`n`r`n$($_.Exception.Message)`r`n`r`nTry the parent folder:`r`n$dataRootUnc",
            "Remote logs error",
            "OK",
            "Error") | Out-Null
    }
}

function Get-LocalClientPackFolderForPush {
    if ($script:IsPackedLayout) {
        $exe = Join-Path $script:ScriptDir "payload\Heimdall.Agent.exe"
        if (Test-Path -LiteralPath $exe) { return $script:ScriptDir }
    }
    $pack = Get-ClientPackFolder
    if ($pack) {
        $exe = Join-Path $pack "payload\Heimdall.Agent.exe"
        if (Test-Path -LiteralPath $exe) { return $pack }
    }
    return $null
}

function Push-ClientPackToSingleHost {
    <#
    Copies (or opens, if no local pack) the client pack to one remote admin share. Pure logic — no
    MessageBox/dialogs — so both the single- and multi-host paths in Push-ClientPackToMachine share it.
    Returns: Host, Reachable, Copied, OpenPath, DropFolder, Message.
    #>
    param(
        [Parameter(Mandatory)][string]$HostInput,
        [string]$LocalPack
    )
    $hostNorm = $HostInput.Trim().TrimEnd('\')
    if ($hostNorm -match '^\\\\') {
        $hostNorm = $hostNorm -replace '^\\\\([^\\]+).*$', '$1'
    }

    $adminRoot = "\\$hostNorm\C$"
    $dropRoot = "\\$hostNorm\C$\Temp"
    $dropFolder = "\\$hostNorm\C$\Temp\Heimdall-Client"

    Write-HeimdallLog "Push target: $hostNorm adminRoot=$adminRoot" -Level INFO
    if (-not (Test-Path -LiteralPath $adminRoot)) {
        Write-HeimdallLog "Admin share unreachable: $adminRoot" -Level ERROR
        return [pscustomobject]@{
            Host       = $hostNorm
            Reachable  = $false
            Copied     = $false
            OpenPath   = $null
            DropFolder = $dropFolder
            Message    = "Cannot reach $adminRoot (check hostname/IP, admin rights on that PC, SMB/port 445, firewall allows C$)."
        }
    }

    if ($LocalPack) {
        try {
            if (-not (Test-Path -LiteralPath $dropRoot)) {
                New-Item -ItemType Directory -Path $dropRoot -Force | Out-Null
            }
            if (-not (Test-Path -LiteralPath $dropFolder)) {
                New-Item -ItemType Directory -Path $dropFolder -Force | Out-Null
            }
            Write-HeimdallLog "Copying pack from $LocalPack to $dropFolder" -Level STEP
            Set-UiStatus "Copying Heimdall-Client to $hostNorm ..."
            $robolog = Join-Path $env:TEMP ("heimdall-push-" + ($hostNorm -replace '[\\/:*?"<>|]', '-') + "-" + (Get-Date -Format "yyyyMMdd-HHmmss") + ".log")
            $p = Start-Process -FilePath "robocopy.exe" -ArgumentList @(
                $LocalPack,
                $dropFolder,
                "/E", "/R:1", "/W:2", "/NFL", "/NDL", "/NJH", "/NJS", "/NP",
                "/LOG:$robolog"
            ) -Wait -PassThru -NoNewWindow
            # robocopy: exit codes 0-7 are success / with differences
            if ($p.ExitCode -ge 8) {
                throw "robocopy exit $($p.ExitCode). Log: $robolog"
            }
            $remoteExe = Join-Path $dropFolder "payload\Heimdall.Agent.exe"
            if (-not (Test-Path -LiteralPath $remoteExe)) {
                throw "Copy finished but payload\Heimdall.Agent.exe missing at $remoteExe"
            }
            Write-HeimdallLog "Push copy OK for $hostNorm (robocopy exit $($p.ExitCode)). Log: $robolog" -Level OK
            Add-RemoteLogTarget -TargetHost $hostNorm -UncPath ("\\$hostNorm\C$\ProgramData\Heimdall\logs")
            return [pscustomobject]@{
                Host       = $hostNorm
                Reachable  = $true
                Copied     = $true
                OpenPath   = $dropFolder
                DropFolder = $dropFolder
                Message    = "Copied to $dropFolder — run Install.lnk on $hostNorm."
            }
        }
        catch {
            Write-HeimdallLog "Push copy failed for $hostNorm : $($_.Exception.Message)" -Level ERROR
            $openFallback = if (Test-Path -LiteralPath $dropRoot) { $dropRoot } else { $adminRoot }
            return [pscustomobject]@{
                Host       = $hostNorm
                Reachable  = $true
                Copied     = $false
                OpenPath   = $openFallback
                DropFolder = $dropFolder
                Message    = "Copy failed: $($_.Exception.Message)"
            }
        }
    }
    else {
        $openPath = $adminRoot
        try {
            if (-not (Test-Path -LiteralPath $dropRoot)) {
                New-Item -ItemType Directory -Path $dropRoot -Force | Out-Null
            }
            $openPath = $dropRoot
        }
        catch {
            $openPath = $adminRoot
        }
        Add-RemoteLogTarget -TargetHost $hostNorm -UncPath ("\\$hostNorm\C$\ProgramData\Heimdall\logs")
        return [pscustomobject]@{
            Host       = $hostNorm
            Reachable  = $true
            Copied     = $false
            OpenPath   = $openPath
            DropFolder = $dropFolder
            Message    = "No local pack — opened $openPath for manual copy."
        }
    }
}

function Show-ManagePushGroupsDialog {
    <# Create/rename/delete push groups and edit their member host list (freeform, newline/comma/semicolon). #>
    $form = New-Object System.Windows.Forms.Form
    $form.Text = "Manage push groups"
    $form.StartPosition = "CenterParent"
    $form.FormBorderStyle = "FixedDialog"
    $form.MaximizeBox = $false
    $form.MinimizeBox = $false
    $form.Width = 640
    $form.Height = 460
    $form.Font = New-Object System.Drawing.Font("Segoe UI", 9)

    $lblGroups = New-Object System.Windows.Forms.Label
    $lblGroups.Text = "Groups:"
    $lblGroups.Left = 16; $lblGroups.Top = 12; $lblGroups.Width = 180; $lblGroups.Height = 18
    $form.Controls.Add($lblGroups)

    $groupsBox = New-Object System.Windows.Forms.ListBox
    $groupsBox.Left = 16; $groupsBox.Top = 32; $groupsBox.Width = 200; $groupsBox.Height = 300
    $form.Controls.Add($groupsBox)

    $newBtn = New-Object System.Windows.Forms.Button
    $newBtn.Text = "New group..."
    $newBtn.Left = 16; $newBtn.Top = 340; $newBtn.Width = 95; $newBtn.Height = 26
    $form.Controls.Add($newBtn)

    $renameBtn = New-Object System.Windows.Forms.Button
    $renameBtn.Text = "Rename..."
    $renameBtn.Left = 121; $renameBtn.Top = 340; $renameBtn.Width = 95; $renameBtn.Height = 26
    $form.Controls.Add($renameBtn)

    $deleteBtn = New-Object System.Windows.Forms.Button
    $deleteBtn.Text = "Delete group"
    $deleteBtn.Left = 16; $deleteBtn.Top = 372; $deleteBtn.Width = 200; $deleteBtn.Height = 26
    $form.Controls.Add($deleteBtn)

    $lblMembers = New-Object System.Windows.Forms.Label
    $lblMembers.Text = "Members of selected group (one host per line — also accepts comma / semicolon separated):"
    $lblMembers.Left = 232; $lblMembers.Top = 12; $lblMembers.Width = 380; $lblMembers.Height = 32
    $form.Controls.Add($lblMembers)

    $membersBox = New-Object System.Windows.Forms.TextBox
    $membersBox.Left = 232; $membersBox.Top = 48; $membersBox.Width = 380; $membersBox.Height = 300
    $membersBox.Multiline = $true
    $membersBox.ScrollBars = "Vertical"
    $membersBox.Enabled = $false
    $form.Controls.Add($membersBox)

    $saveMembersBtn = New-Object System.Windows.Forms.Button
    $saveMembersBtn.Text = "Save members"
    $saveMembersBtn.Left = 232; $saveMembersBtn.Top = 356; $saveMembersBtn.Width = 130; $saveMembersBtn.Height = 26
    $saveMembersBtn.Enabled = $false
    $form.Controls.Add($saveMembersBtn)

    $closeBtn = New-Object System.Windows.Forms.Button
    $closeBtn.Text = "Close"
    $closeBtn.Left = 492; $closeBtn.Top = 380; $closeBtn.Width = 120; $closeBtn.Height = 30
    $closeBtn.DialogResult = [System.Windows.Forms.DialogResult]::OK
    $form.AcceptButton = $closeBtn
    $form.Controls.Add($closeBtn)

    function Update-PushGroupsBox {
        param([string]$SelectName)
        $groupsBox.Items.Clear()
        $names = @((Get-PushGroups) | ForEach-Object { $_.name })
        foreach ($n in $names) { [void]$groupsBox.Items.Add($n) }
        if ($SelectName -and ($names -contains $SelectName)) {
            $groupsBox.SelectedIndex = [array]::IndexOf($names, $SelectName)
        }
        elseif ($groupsBox.Items.Count -gt 0) {
            $groupsBox.SelectedIndex = 0
        }
        else {
            $membersBox.Text = ""
            $membersBox.Enabled = $false
            $saveMembersBtn.Enabled = $false
        }
    }
    Update-PushGroupsBox

    $groupsBox.Add_SelectedIndexChanged({
        if ($groupsBox.SelectedIndex -lt 0) {
            $membersBox.Text = ""
            $membersBox.Enabled = $false
            $saveMembersBtn.Enabled = $false
            return
        }
        $name = [string]$groupsBox.SelectedItem
        $g = (Get-PushGroups) | Where-Object { $_.name -eq $name }
        $membersBox.Text = if ($g) { (@($g.hosts) -join "`r`n") } else { "" }
        $membersBox.Enabled = $true
        $saveMembersBtn.Enabled = $true
    })

    $newBtn.Add_Click({
        $r = Show-InputForm -Title "New push group" -Prompt "Group name (e.g. Flood Modellers, Sydney lab):" -Fields ([ordered]@{ Name = "" }) -AcceptLabel "Create"
        if ($r -and $r.Name) {
            if (Add-PushGroup -Name $r.Name) {
                Update-PushGroupsBox -SelectName $r.Name.Trim()
            }
            else {
                [System.Windows.Forms.MessageBox]::Show("A group named `"$($r.Name.Trim())`" already exists.", "Group exists", "OK", "Warning") | Out-Null
            }
        }
    })

    $renameBtn.Add_Click({
        if ($groupsBox.SelectedIndex -lt 0) { return }
        $old = [string]$groupsBox.SelectedItem
        $r = Show-InputForm -Title "Rename push group" -Prompt "New name for `"$old`":" -Fields ([ordered]@{ Name = $old }) -AcceptLabel "Rename"
        if ($r -and $r.Name -and ($r.Name.Trim() -ne $old)) {
            $existing = @(Get-PushGroups)
            if ($existing | Where-Object { $_.name -eq $r.Name.Trim() }) {
                [System.Windows.Forms.MessageBox]::Show("A group with that name already exists.", "Group exists", "OK", "Warning") | Out-Null
                return
            }
            $updated = @($existing | ForEach-Object {
                    if ($_.name -eq $old) { [pscustomobject]@{ name = $r.Name.Trim(); hosts = $_.hosts } } else { $_ }
                })
            Save-PushGroups -Groups $updated
            Write-HeimdallLog "Renamed push group '$old' to '$($r.Name.Trim())'" -Level INFO
            Update-PushGroupsBox -SelectName $r.Name.Trim()
        }
    })

    $deleteBtn.Add_Click({
        if ($groupsBox.SelectedIndex -lt 0) { return }
        $name = [string]$groupsBox.SelectedItem
        $confirm = [System.Windows.Forms.MessageBox]::Show(
            "Delete group `"$name`"? Member hosts stay remembered individually - only the group is removed.",
            "Delete group", [System.Windows.Forms.MessageBoxButtons]::YesNo, [System.Windows.Forms.MessageBoxIcon]::Question)
        if ($confirm -eq [System.Windows.Forms.DialogResult]::Yes) {
            Remove-PushGroup -Name $name
            Update-PushGroupsBox
        }
    })

    $saveMembersBtn.Add_Click({
        if ($groupsBox.SelectedIndex -lt 0) { return }
        $name = [string]$groupsBox.SelectedItem
        $hostsForGroup = ConvertTo-PushHostList -RawText $membersBox.Text
        Set-PushGroupMembers -GroupName $name -Hosts $hostsForGroup
        $known = @(Get-PushHosts) | ForEach-Object { $_.host }
        foreach ($h in $hostsForGroup) {
            if ($known -notcontains $h) {
                Add-PushHost -TargetHost $h
            }
        }
        Write-HeimdallLog "Saved $($hostsForGroup.Count) member(s) for push group '$name'" -Level OK
        [System.Windows.Forms.MessageBox]::Show("Saved $($hostsForGroup.Count) member(s) for `"$name`".", "Group saved", "OK", "Information") | Out-Null
    })

    [void]$form.ShowDialog()
}

function Push-ClientPackToMachine {
    Write-HeimdallLog "Push client pack to remote PC(s)" -Level STEP
    Set-UiSteps @(
        "[ ] 1. Choose target(s) — ad-hoc list, remembered hosts, and/or groups",
        "[ ] 2. Reach \\HOST\C$ for each target",
        "[ ] 3. Copy Heimdall-Client (if pack ready)",
        "[ ] 4. Summarize results"
    )
    Set-UiStatus "Push client pack..."

    $localPack = Get-LocalClientPackFolderForPush
    if (-not $localPack) {
        if (-not $script:IsPackedLayout -and $script:RepoRoot) {
            $r = [System.Windows.Forms.MessageBox]::Show(
                "No local client pack found (dist\Heimdall-Client\payload).`r`n`r`nCreate the client pack now before pushing?",
                "Client pack required",
                [System.Windows.Forms.MessageBoxButtons]::YesNoCancel,
                [System.Windows.Forms.MessageBoxIcon]::Question)
            if ($r -eq [System.Windows.Forms.DialogResult]::Cancel) {
                Write-HeimdallLog "Push cancelled (no pack)." -Level WARN
                return
            }
            if ($r -eq [System.Windows.Forms.DialogResult]::Yes) {
                $packed = Start-GuidedPack -OfferInstallAfter:$false
                if (-not $packed) { return }
                $localPack = Get-LocalClientPackFolderForPush
            }
        }
        if (-not $localPack) {
            Write-HeimdallLog "Push will open C$ only (no local pack to copy)." -Level WARN
        }
    }

    $form = New-Object System.Windows.Forms.Form
    $form.Text = "Push client pack to PC(s)"
    $form.StartPosition = "CenterParent"
    $form.FormBorderStyle = "FixedDialog"
    $form.MaximizeBox = $false
    $form.MinimizeBox = $false
    $form.Width = 620
    $form.Height = 620
    $form.Font = New-Object System.Drawing.Font("Segoe UI", 9)

    $lblAdhoc = New-Object System.Windows.Forms.Label
    $lblAdhoc.Text = "Ad-hoc hosts (paste a list — comma, semicolon, or newline separated):"
    $lblAdhoc.Left = 16; $lblAdhoc.Top = 12; $lblAdhoc.Width = 580; $lblAdhoc.Height = 18
    $form.Controls.Add($lblAdhoc)

    $adhocBox = New-Object System.Windows.Forms.TextBox
    $adhocBox.Left = 16; $adhocBox.Top = 32; $adhocBox.Width = 580; $adhocBox.Height = 54
    $adhocBox.Multiline = $true
    $adhocBox.ScrollBars = "Vertical"
    $adhocBox.AcceptsReturn = $true
    $form.Controls.Add($adhocBox)

    $lblGroups = New-Object System.Windows.Forms.Label
    $lblGroups.Text = "Groups (check = push to ALL members of that group):"
    $lblGroups.Left = 16; $lblGroups.Top = 94; $lblGroups.Width = 420; $lblGroups.Height = 18
    $form.Controls.Add($lblGroups)

    $manageBtn = New-Object System.Windows.Forms.Button
    $manageBtn.Text = "Manage groups..."
    $manageBtn.Left = 460; $manageBtn.Top = 90; $manageBtn.Width = 136; $manageBtn.Height = 24
    $form.Controls.Add($manageBtn)

    $groupsList = New-Object System.Windows.Forms.CheckedListBox
    $groupsList.Left = 16; $groupsList.Top = 116; $groupsList.Width = 580; $groupsList.Height = 90
    $groupsList.CheckOnClick = $true
    $form.Controls.Add($groupsList)

    $lblHosts = New-Object System.Windows.Forms.Label
    $lblHosts.Text = "Remembered hosts (check individual targets — even within a checked group above):"
    $lblHosts.Left = 16; $lblHosts.Top = 214; $lblHosts.Width = 500; $lblHosts.Height = 18
    $form.Controls.Add($lblHosts)

    $hostsList = New-Object System.Windows.Forms.CheckedListBox
    $hostsList.Left = 16; $hostsList.Top = 236; $hostsList.Width = 580; $hostsList.Height = 190
    $hostsList.CheckOnClick = $true
    $form.Controls.Add($hostsList)

    $forgetBtn = New-Object System.Windows.Forms.Button
    $forgetBtn.Text = "Forget checked host(s)"
    $forgetBtn.Left = 16; $forgetBtn.Top = 432; $forgetBtn.Width = 170; $forgetBtn.Height = 26
    $form.Controls.Add($forgetBtn)

    $hint = New-Object System.Windows.Forms.Label
    $hint.Left = 16; $hint.Top = 464; $hint.Width = 580; $hint.Height = 48
    if ($localPack) {
        $hint.Text = "Copies the pack to \\HOST\C$\Temp\Heimdall-Client on every selected target, then opens the drop folder(s) (capped at 5 Explorer windows). On each remote PC run Install.lnk."
    }
    else {
        $hint.Text = "No local pack found yet — will open \\HOST\C$\Temp on each target so you can paste Heimdall-Client manually."
    }
    $form.Controls.Add($hint)

    function Update-PushDialogLists {
        $groupsList.Items.Clear()
        foreach ($g in (Get-PushGroups)) {
            $count = @($g.hosts).Count
            [void]$groupsList.Items.Add("$($g.name)  ($count host$(if ($count -ne 1) { 's' }))")
        }
        $hostsList.Items.Clear()
        foreach ($h in (Get-PushHosts)) {
            $groupNames = @(Get-PushGroupsForHost -TargetHost $h.host)
            $label = if ($groupNames.Count -gt 0) { "$($h.host)  [$($groupNames -join ', ')]" } else { [string]$h.host }
            [void]$hostsList.Items.Add($label)
        }
    }
    Update-PushDialogLists

    $manageBtn.Add_Click({
        Show-ManagePushGroupsDialog
        Update-PushDialogLists
    })

    $forgetBtn.Add_Click({
        $allHostsNow = @(Get-PushHosts)
        $toForget = @()
        foreach ($i in $hostsList.CheckedIndices) {
            if ($i -lt $allHostsNow.Count) { $toForget += $allHostsNow[$i].host }
        }
        if ($toForget.Count -eq 0) {
            [System.Windows.Forms.MessageBox]::Show("Check one or more remembered hosts first.", "Nothing selected", "OK", "Information") | Out-Null
            return
        }
        $confirm = [System.Windows.Forms.MessageBox]::Show(
            "Forget $($toForget.Count) remembered host(s)? (Ad-hoc pushes still work — this just removes them from the remembered list / groups.)`r`n`r`n" + ($toForget -join "`r`n"),
            "Forget hosts", [System.Windows.Forms.MessageBoxButtons]::YesNo, [System.Windows.Forms.MessageBoxIcon]::Question)
        if ($confirm -eq [System.Windows.Forms.DialogResult]::Yes) {
            foreach ($h in $toForget) { Remove-PushHost -TargetHost $h }
            Update-PushDialogLists
        }
    })

    $pushBtn = New-Object System.Windows.Forms.Button
    $pushBtn.Text = if ($localPack) { "Push" } else { "Open C$" }
    $pushBtn.Left = 410; $pushBtn.Top = 522; $pushBtn.Width = 90; $pushBtn.Height = 30
    $pushBtn.DialogResult = [System.Windows.Forms.DialogResult]::OK
    $form.AcceptButton = $pushBtn
    $form.Controls.Add($pushBtn)

    $cancelBtn = New-Object System.Windows.Forms.Button
    $cancelBtn.Text = "Cancel"
    $cancelBtn.Left = 506; $cancelBtn.Top = 522; $cancelBtn.Width = 90; $cancelBtn.Height = 30
    $cancelBtn.DialogResult = [System.Windows.Forms.DialogResult]::Cancel
    $form.CancelButton = $cancelBtn
    $form.Controls.Add($cancelBtn)

    $result = $form.ShowDialog()
    if ($result -ne [System.Windows.Forms.DialogResult]::OK) {
        Write-HeimdallLog "Push client pack cancelled." -Level WARN
        return
    }

    # Resolve final target list: ad-hoc text + checked group members + checked individual hosts, deduped.
    $targets = New-Object System.Collections.Generic.List[string]
    $seenTargets = New-Object System.Collections.Generic.HashSet[string]([System.StringComparer]::OrdinalIgnoreCase)
    function Add-PushTargetToList([string]$CandidateHost) {
        $norm = $CandidateHost.Trim().TrimEnd('\')
        if ([string]::IsNullOrWhiteSpace($norm)) { return }
        if ($seenTargets.Add($norm)) { [void]$targets.Add($norm) }
    }

    foreach ($h in (ConvertTo-PushHostList -RawText $adhocBox.Text)) { Add-PushTargetToList $h }

    $allGroupsNow = @(Get-PushGroups)
    foreach ($i in $groupsList.CheckedIndices) {
        if ($i -lt $allGroupsNow.Count) {
            foreach ($h in @($allGroupsNow[$i].hosts)) { Add-PushTargetToList $h }
        }
    }

    $allHostsNow = @(Get-PushHosts)
    foreach ($i in $hostsList.CheckedIndices) {
        if ($i -lt $allHostsNow.Count) { Add-PushTargetToList $allHostsNow[$i].host }
    }

    if ($targets.Count -eq 0) {
        Write-HeimdallLog "Push cancelled: no targets selected." -Level WARN
        [System.Windows.Forms.MessageBox]::Show(
            "Enter at least one ad-hoc host, or check a group / remembered host.",
            "No targets selected", "OK", "Warning") | Out-Null
        return
    }

    Update-UiStep 0 "[OK] 1. $($targets.Count) target(s) selected"

    if ($targets.Count -gt 1) {
        $preview = ($targets | Select-Object -First 15) -join "`r`n"
        if ($targets.Count -gt 15) { $preview += "`r`n... and $($targets.Count - 15) more" }
        $confirm = [System.Windows.Forms.MessageBox]::Show(
            "Push client pack to $($targets.Count) machine(s)?`r`n`r`n$preview",
            "Confirm multi-host push",
            [System.Windows.Forms.MessageBoxButtons]::YesNo,
            [System.Windows.Forms.MessageBoxIcon]::Question)
        if ($confirm -ne [System.Windows.Forms.DialogResult]::Yes) {
            Write-HeimdallLog "Multi-host push cancelled at confirmation ($($targets.Count) targets)." -Level WARN
            return
        }
    }

    Write-HeimdallLog "Pushing client pack to $($targets.Count) target(s): $($targets -join ', ')" -Level STEP
    $results = New-Object System.Collections.Generic.List[object]
    $idx = 0
    foreach ($t in $targets) {
        $idx++
        Set-UiStatus "Pushing to $t ($idx of $($targets.Count))..."
        $r = Push-ClientPackToSingleHost -HostInput $t -LocalPack $localPack
        [void]$results.Add($r)
        $lastResult = if (-not $r.Reachable) { "Unreachable" } elseif ($r.Copied) { "Copied OK" } else { "Reachable, not copied" }
        Add-PushHost -TargetHost $r.Host -LastResult $lastResult
    }
    Update-UiStep 1 "[OK] 2. Reach-checked $($targets.Count) target(s)"
    Update-UiStep 2 "[OK] 3. Copy attempted on $($targets.Count) target(s) — see summary"

    $copiedCount = @($results | Where-Object { $_.Copied }).Count
    $unreachable = @($results | Where-Object { -not $_.Reachable })
    $reachableNoCopy = @($results | Where-Object { $_.Reachable -and -not $_.Copied })

    $summaryLines = New-Object System.Collections.Generic.List[string]
    $summaryLines.Add("Pushed to $($targets.Count) target(s): $copiedCount copied OK, $($reachableNoCopy.Count) reachable only, $($unreachable.Count) unreachable.")
    foreach ($r in $results) {
        $tag = if ($r.Copied) { "OK" } elseif ($r.Reachable) { "OPEN" } else { "FAIL" }
        $summaryLines.Add("[$tag] $($r.Host): $($r.Message)")
    }
    Update-UiStep 3 "[OK] 4. $copiedCount copied / $($unreachable.Count) unreachable"
    Set-UiStatus "Push complete: $copiedCount copied, $($unreachable.Count) unreachable"

    $opened = 0
    foreach ($r in $results) {
        if ($opened -ge 5) { break }
        if ($r.OpenPath) {
            try {
                Start-Process explorer.exe $r.OpenPath
                $opened++
            }
            catch {
                Write-HeimdallLog "Failed to open Explorer for $($r.OpenPath): $($_.Exception.Message)" -Level WARN
            }
        }
    }
    if (@($results | Where-Object { $_.OpenPath }).Count -gt $opened) {
        Write-HeimdallLog "Opened Explorer for $opened target(s) (capped at 5) — see summary/log for the rest." -Level INFO
    }

    Write-HeimdallLog ($summaryLines -join " | ") -Level $(if ($unreachable.Count -eq 0) { "OK" } else { "WARN" })
    [System.Windows.Forms.MessageBox]::Show(
        ($summaryLines -join "`r`n") + "`r`n`r`nLog: $($script:LogPath)",
        "Push client pack — summary",
        "OK",
        $(if ($unreachable.Count -eq 0) { "Information" } else { "Warning" })) | Out-Null
}

function Get-DefaultRemoteApiHost {
    $last = Get-LastInstallSettings
    if ($last -and $last.apiUrl) {
        try {
            $uri = [Uri](Normalize-ApiUrl ([string]$last.apiUrl))
            if ($uri.Host) {
                return $uri.Host
            }
        }
        catch {
            Write-HeimdallLog "Could not parse apiUrl for host prefill: $($_.Exception.Message)" -Level WARN
        }
    }
    $targets = @(Get-RemoteLogTargets)
    if ($targets.Count -gt 0 -and $targets[0].host) {
        return [string]$targets[0].host
    }
    return $env:COMPUTERNAME
}

function Resolve-RemoteApiDatabaseUncPath {
    param([Parameter(Mandatory)][string]$HostOrIp)
    $hostNorm = $HostOrIp.Trim().TrimEnd('\')
    if ([string]::IsNullOrWhiteSpace($hostNorm)) { return $null }
    if ($hostNorm -match '^\\\\') {
        if ($hostNorm -match 'heimdall\.db\s*$') { return $hostNorm }
        if ($hostNorm -match 'ProgramData\\Heimdall\s*$') { return "$hostNorm\heimdall.db" }
    }
    return "\\$hostNorm\C$\ProgramData\Heimdall\heimdall.db"
}

function Resolve-RemoteApiDataRootUncPath {
    param([Parameter(Mandatory)][string]$HostOrIp)
    $dbUnc = Resolve-RemoteApiDatabaseUncPath -HostOrIp $HostOrIp
    if (-not $dbUnc) { return $null }
    return (Split-Path -Parent $dbUnc)
}

function Backup-ApiDatabase {
    Write-HeimdallLog "Backup API database" -Level STEP

    $defaultHost = Get-DefaultRemoteApiHost
    $form = New-Object System.Windows.Forms.Form
    $form.Text = "Backup API database"
    $form.StartPosition = "CenterParent"
    $form.FormBorderStyle = "FixedDialog"
    $form.MaximizeBox = $false
    $form.MinimizeBox = $false
    $form.Width = 560
    $form.Height = 240
    $form.Font = New-Object System.Drawing.Font("Segoe UI", 9)

    $lbl = New-Object System.Windows.Forms.Label
    $lbl.Text = "API host (machine name or IP; admin share C$ required):"
    $lbl.Left = 16
    $lbl.Top = 16
    $lbl.Width = 520
    $lbl.Height = 20
    $form.Controls.Add($lbl)

    $combo = New-Object System.Windows.Forms.ComboBox
    $combo.Left = 16
    $combo.Top = 42
    $combo.Width = 520
    $combo.DropDownStyle = "DropDown"
    $combo.AutoCompleteMode = "SuggestAppend"
    $combo.AutoCompleteSource = "ListItems"
    foreach ($t in (Get-RemoteLogTargets)) {
        [void]$combo.Items.Add($t.host)
    }
    if ($combo.Items.Count -gt 0) {
        $idx = [array]::IndexOf($combo.Items, $defaultHost)
        if ($idx -ge 0) { $combo.SelectedIndex = $idx } else { $combo.Text = $defaultHost }
    }
    else {
        $combo.Text = $defaultHost
    }
    $form.Controls.Add($combo)

    $hint = New-Object System.Windows.Forms.Label
    $hint.Text = "Copies \\HOST\C$\ProgramData\Heimdall\heimdall.db to %LOCALAPPDATA%\Heimdall\backups\ and tries \\HOST\...\backups\ on the API PC."
    $hint.Left = 16
    $hint.Top = 72
    $hint.Width = 520
    $hint.Height = 32
    $form.Controls.Add($hint)

    $okBtn = New-Object System.Windows.Forms.Button
    $okBtn.Text = "Backup"
    $okBtn.Left = 250
    $okBtn.Top = 118
    $okBtn.Width = 90
    $okBtn.DialogResult = [System.Windows.Forms.DialogResult]::OK
    $form.AcceptButton = $okBtn
    $form.Controls.Add($okBtn)

    $cancelBtn = New-Object System.Windows.Forms.Button
    $cancelBtn.Text = "Cancel"
    $cancelBtn.Left = 350
    $cancelBtn.Top = 118
    $cancelBtn.Width = 90
    $cancelBtn.DialogResult = [System.Windows.Forms.DialogResult]::Cancel
    $form.CancelButton = $cancelBtn
    $form.Controls.Add($cancelBtn)

    $result = $form.ShowDialog()
    if ($result -ne [System.Windows.Forms.DialogResult]::OK) {
        Write-HeimdallLog "API database backup cancelled." -Level WARN
        return
    }

    $hostInput = $combo.Text.Trim()
    if ([string]::IsNullOrWhiteSpace($hostInput)) {
        [System.Windows.Forms.MessageBox]::Show(
            "Enter the API machine name or IP address.",
            "Missing input",
            "OK",
            "Warning") | Out-Null
        return
    }

    $sourceDb = Resolve-RemoteApiDatabaseUncPath -HostOrIp $hostInput
    $dataRootUnc = Resolve-RemoteApiDataRootUncPath -HostOrIp $hostInput
    $hostSafe = ($hostInput -replace '[\\/:*?"<>|]', '-')
    $stamp = Get-Date -Format "yyyyMMdd-HHmmss"
    $fileName = "heimdall-$hostSafe-$stamp.db"

    $localBackupRoot = Join-Path $env:LOCALAPPDATA "Heimdall\backups"
    if (-not (Test-Path $localBackupRoot)) {
        New-Item -ItemType Directory -Path $localBackupRoot -Force | Out-Null
    }
    $localDest = Join-Path $localBackupRoot $fileName
    $remoteBackupDir = "$dataRootUnc\backups"
    $remoteDest = "$remoteBackupDir\$fileName"

    Write-HeimdallLog "API DB backup: host=$hostInput source=$sourceDb local=$localDest remote=$remoteDest" -Level INFO

    if (-not (Test-Path -LiteralPath $sourceDb)) {
        Write-HeimdallLog "API database unreachable: $sourceDb" -Level ERROR
        [System.Windows.Forms.MessageBox]::Show(
            "Cannot reach:`r`n$sourceDb`r`n`r`nCheck machine name, admin rights, and SMB (port 445).",
            "Backup failed",
            "OK",
            "Error") | Out-Null
        return
    }

    try {
        Copy-Item -LiteralPath $sourceDb -Destination $localDest -Force
        Write-HeimdallLog "Local backup saved: $localDest" -Level OK

        $remoteOk = $false
        $remoteNote = ""
        try {
            if (-not (Test-Path -LiteralPath $remoteBackupDir)) {
                New-Item -ItemType Directory -Path $remoteBackupDir -Force | Out-Null
            }
            Copy-Item -LiteralPath $sourceDb -Destination $remoteDest -Force
            $remoteOk = $true
            Write-HeimdallLog "Remote backup saved: $remoteDest" -Level OK
        }
        catch {
            $remoteNote = "Remote copy skipped (not writable): $($_.Exception.Message)"
            Write-HeimdallLog $remoteNote -Level WARN
        }

        $logsUnc = "$dataRootUnc\logs"
        Add-RemoteLogTarget -TargetHost $hostInput -UncPath $logsUnc

        $msg = "Backup saved locally:`r`n$localDest"
        if ($remoteOk) {
            $msg += "`r`n`r`nAlso copied on API PC:`r`n$remoteDest"
        }
        elseif ($remoteNote) {
            $msg += "`r`n`r`n$remoteNote"
        }
        [System.Windows.Forms.MessageBox]::Show($msg, "Backup complete", "OK", "Information") | Out-Null
        Set-UiStatus "API DB backed up: $hostInput"
    }
    catch {
        Write-HeimdallLog "API database backup failed: $($_.Exception.Message)" -Level ERROR
        [System.Windows.Forms.MessageBox]::Show(
            "Backup failed:`r`n$($_.Exception.Message)`r`n`r`nSource:`r`n$sourceDb",
            "Backup failed",
            "OK",
            "Error") | Out-Null
    }
}

function Invoke-RemoveSeedDemoMachines {
    Write-HeimdallLog "Remove seed/demo machines" -Level STEP

    if (-not $script:RepoRoot) {
        [System.Windows.Forms.MessageBox]::Show(
            "Remove seed/demo machines is available from the repo scripts folder (not packed collector-only layout).",
            "Not available",
            "OK",
            "Information") | Out-Null
        return
    }

    $scriptPath = Join-Path $script:RepoRoot "scripts\Remove-SeedDemoMachines.ps1"
    if (-not (Test-Path -LiteralPath $scriptPath)) {
        Write-HeimdallLog "Missing script: $scriptPath" -Level ERROR
        [System.Windows.Forms.MessageBox]::Show(
            "Script not found:`r`n$scriptPath",
            "Remove seed/demo machines",
            "OK",
            "Error") | Out-Null
        return
    }

    $defaultHost = Get-DefaultRemoteApiHost
    $form = New-Object System.Windows.Forms.Form
    $form.Text = "Remove seed/demo machines"
    $form.StartPosition = "CenterParent"
    $form.FormBorderStyle = "FixedDialog"
    $form.MaximizeBox = $false
    $form.MinimizeBox = $false
    $form.Width = 560
    $form.Height = 260
    $form.Font = New-Object System.Drawing.Font("Segoe UI", 9)

    $lbl = New-Object System.Windows.Forms.Label
    $lbl.Text = "API host (leave blank for this PC / local ProgramData DB):"
    $lbl.Left = 16
    $lbl.Top = 16
    $lbl.Width = 520
    $lbl.Height = 20
    $form.Controls.Add($lbl)

    $combo = New-Object System.Windows.Forms.ComboBox
    $combo.Left = 16
    $combo.Top = 42
    $combo.Width = 520
    $combo.DropDownStyle = "DropDown"
    $combo.Text = $defaultHost
    foreach ($t in (Get-RemoteLogTargets)) {
        [void]$combo.Items.Add($t.host)
    }
    $form.Controls.Add($combo)

    $hint = New-Object System.Windows.Forms.Label
    $hint.Text = "Deletes DEMO-* hosts and AgentVersion=seed only. Stop HeimdallApi first if the DB is locked. Sets DemoMachinesOffered so restart will not re-seed."
    $hint.Left = 16
    $hint.Top = 72
    $hint.Width = 520
    $hint.Height = 48
    $form.Controls.Add($hint)

    $okBtn = New-Object System.Windows.Forms.Button
    $okBtn.Text = "Remove"
    $okBtn.Left = 250
    $okBtn.Top = 138
    $okBtn.Width = 90
    $okBtn.DialogResult = [System.Windows.Forms.DialogResult]::OK
    $form.AcceptButton = $okBtn
    $form.Controls.Add($okBtn)

    $cancelBtn = New-Object System.Windows.Forms.Button
    $cancelBtn.Text = "Cancel"
    $cancelBtn.Left = 350
    $cancelBtn.Top = 138
    $cancelBtn.Width = 90
    $cancelBtn.DialogResult = [System.Windows.Forms.DialogResult]::Cancel
    $form.CancelButton = $cancelBtn
    $form.Controls.Add($cancelBtn)

    $result = $form.ShowDialog()
    if ($result -ne [System.Windows.Forms.DialogResult]::OK) {
        Write-HeimdallLog "Remove seed/demo machines cancelled." -Level WARN
        return
    }

    $hostInput = $combo.Text.Trim()
    $dbPath = if ([string]::IsNullOrWhiteSpace($hostInput)) {
        Join-Path $env:ProgramData "Heimdall\heimdall.db"
    }
    else {
        Resolve-RemoteApiDatabaseUncPath -HostOrIp $hostInput
    }

    $confirm = [System.Windows.Forms.MessageBox]::Show(
        "Delete seed/demo machines from:`r`n$dbPath`r`r`nLive agents (AgentVersion != seed) are not touched.`r`nStop HeimdallApi on the API PC if backup/removal fails due to file lock.",
        "Confirm removal",
        [System.Windows.Forms.MessageBoxButtons]::YesNo,
        [System.Windows.Forms.MessageBoxIcon]::Warning)
    if ($confirm -ne [System.Windows.Forms.DialogResult]::Yes) {
        Write-HeimdallLog "Remove seed/demo machines declined at confirm." -Level WARN
        return
    }

    Write-HeimdallLog "Running Remove-SeedDemoMachines.ps1 against $dbPath" -Level INFO
    try {
        $output = & powershell.exe -NoProfile -ExecutionPolicy Bypass -File $scriptPath -DatabasePath $dbPath 2>&1
        foreach ($line in @($output)) {
            if ($line) { Write-HeimdallLog ([string]$line) -Level INFO }
        }
        if ($LASTEXITCODE -ne 0) {
            $tail = ($output | Select-Object -Last 5 | ForEach-Object { [string]$_ }) -join " "
            if ($tail -match "locked|SQLITE_BUSY|exit 4") {
                throw "Database is locked. Stop HeimdallApi on the API PC, then retry.`r`n$tail"
            }
            if ($tail -match "RemoveSeedDemos tool not found|Heimdall\.Tools\.RemoveSeedDemos not found") {
                throw "Seed removal tool not built. From repo root run:`r`n  dotnet build src\Heimdall.Tools.RemoveSeedDemos\Heimdall.Tools.RemoveSeedDemos.csproj -c Release`r`nThen retry."
            }
            throw "Remove-SeedDemoMachines.ps1 exit code $LASTEXITCODE. $tail"
        }
        Write-HeimdallLog "Seed/demo machine removal finished." -Level OK
        [System.Windows.Forms.MessageBox]::Show(
            "Seed/demo machines removed.`r`n`r`nSee Setup log for hostnames deleted.`r`n`r`nLog: $($script:LogPath)",
            "Removal complete",
            "OK",
            "Information") | Out-Null
        Set-UiStatus "Seed/demo machines removed"
    }
    catch {
        $msg = $_.Exception.Message
        Write-HeimdallLog "Remove seed/demo machines failed: $msg" -Level ERROR
        [System.Windows.Forms.MessageBox]::Show(
            "Removal failed:`r`n$msg`r`n`r`nTip: stop HeimdallApi if the DB is locked; build the RemoveSeedDemos tool if missing.`r`n`r`nLog: $($script:LogPath)",
            "Removal failed",
            "OK",
            "Error") | Out-Null
    }
}

function Get-ClientPackRootCandidates {
    $list = New-Object System.Collections.Generic.List[string]
    if ($script:RepoRoot) {
        $list.Add((Join-Path $script:RepoRoot "dist\Heimdall-Client"))
        $list.Add((Join-Path $script:RepoRoot "dist\workstation-collector")) # legacy pack name
    }
    $list.Add((Join-Path $script:ScriptDir "..\dist\Heimdall-Client"))
    $list.Add((Join-Path $script:ScriptDir "..\dist\workstation-collector"))
    return $list
}

function Get-PayloadPath {
    $candidates = New-Object System.Collections.Generic.List[string]
    $candidates.Add((Join-Path $script:ScriptDir "payload"))
    foreach ($root in Get-ClientPackRootCandidates) {
        $candidates.Add((Join-Path $root "payload"))
    }
    foreach ($c in $candidates) {
        $exe = Join-Path $c "Heimdall.Agent.exe"
        if (Test-Path $exe) { return (Resolve-HeimdallFilesystemPath -Path $c) }
    }
    return $null
}

function Get-InstallerCmdPath {
    $names = New-Object System.Collections.Generic.List[string]
    $names.Add((Join-Path $script:ScriptDir "Install-WorkstationCollector.cmd"))
    if ($script:RepoRoot) {
        $names.Add((Join-Path $script:RepoRoot "scripts\Install-WorkstationCollector.cmd"))
    }
    foreach ($root in Get-ClientPackRootCandidates) {
        $names.Add((Join-Path $root "Install-WorkstationCollector.cmd"))
    }
    foreach ($n in $names) {
        if (Test-Path $n) { return (Resolve-HeimdallFilesystemPath -Path $n) }
    }
    return $null
}

function Get-ClientPackFolder {
    $payload = Get-PayloadPath
    if ($payload) {
        return (Split-Path -Parent $payload)
    }
    if ($script:RepoRoot) {
        $preferred = Join-Path $script:RepoRoot "dist\Heimdall-Client"
        if (Test-Path $preferred) { return $preferred }
    }
    return $null
}

# ---------------------------------------------------------------------------
# Prerequisite checks
# ---------------------------------------------------------------------------

function Invoke-PrerequisiteCheck {
    param(
        [ValidateSet("Api", "Pack", "Collector")]
        [string]$Scenario
    )

    Write-HeimdallLog "Prerequisite check ($Scenario)" -Level STEP
    $issues = New-Object System.Collections.Generic.List[string]
    $notes = New-Object System.Collections.Generic.List[string]

    $admin = Test-IsAdministrator
    if ($Scenario -in @("Api", "Collector")) {
        if ($admin) {
            Write-HeimdallLog "Administrator: yes" -Level OK
        } else {
            $issues.Add("Not running as Administrator (required to create Windows services).")
            Write-HeimdallLog "Administrator: NO" -Level ERROR
        }
    }
    else {
        Write-HeimdallLog "Administrator: $(if ($admin) { 'yes' } else { 'no (optional for pack)' })" -Level INFO
    }

    if ($Scenario -in @("Api", "Pack")) {
        $dotnet = Get-Command dotnet -ErrorAction SilentlyContinue
        if (-not $dotnet) {
            $issues.Add(".NET SDK not found on PATH. Install .NET 10 SDK: https://dotnet.microsoft.com/download/dotnet/10.0")
            Write-HeimdallLog "dotnet: NOT FOUND" -Level ERROR
        }
        else {
            Write-HeimdallLog "dotnet: $($dotnet.Source)" -Level OK
            $sdks = Get-DotNetSdks
            foreach ($s in $sdks) { Write-HeimdallLog "  SDK: $s" -Level INFO }
            if (-not (Test-HasDotNet10Sdk)) {
                $issues.Add("No .NET 10 SDK installed. Heimdall targets net10.0 - install SDK 10.x.")
                Write-HeimdallLog "NET 10 SDK: missing" -Level ERROR
            }
            else {
                Write-HeimdallLog "NET 10 SDK: present" -Level OK
            }
        }
        if (-not $script:RepoRoot -or -not (Test-Path (Join-Path $script:RepoRoot "src"))) {
            $issues.Add("Full Heimdall repo not found next to scripts. Sync/clone the repo for API install or pack.")
            Write-HeimdallLog "Repo sources: missing" -Level ERROR
        }
        else {
            Write-HeimdallLog "Repo root: $($script:RepoRoot)" -Level OK
        }
    }

    if ($Scenario -eq "Collector") {
        $payload = Get-PayloadPath
        if ($payload) {
            Write-HeimdallLog "Payload found: $payload" -Level OK
            $ver = Read-LocalPackVersion
            if ($ver) {
                $pv = $ver.productVersion
                if (-not $pv) { $pv = $script:ProductVersionExpected }
                Write-HeimdallLog "Local pack productVersion: $pv" -Level INFO
                if ($pv -and $pv -ne $script:ProductVersionExpected) {
                    $notes.Add("Pack productVersion ($pv) differs from Setup expected ($($script:ProductVersionExpected)). Continue only if intentional.")
                    Write-HeimdallLog $notes[-1] -Level WARN
                }
            }
            else {
                $notes.Add("No VERSION.json / PACKED.txt beside payload - pack may be incomplete or old.")
                Write-HeimdallLog $notes[-1] -Level WARN
            }
        }
        else {
            $issues.Add("payload\Heimdall.Agent.exe not found. On a build PC open Heimdall Setup -> Create client pack, then copy the whole dist\Heimdall-Client folder here.")
            Write-HeimdallLog "Payload: MISSING" -Level ERROR
        }
    }

    return [pscustomobject]@{
        Ok     = ($issues.Count -eq 0)
        Issues = $issues
        Notes  = $notes
    }
}

# ---------------------------------------------------------------------------
# Guided flows
# ---------------------------------------------------------------------------

function Start-GuidedApiInstall {
    Set-UiSteps @(
        "[ ] 1. Prerequisites (.NET 10 SDK, admin, repo)",
        "[ ] 2. Confirm port + API key",
        "[ ] 3. Install HeimdallApi service + firewall",
        "[ ] 4. Verify /api/health"
    )
    Set-UiStatus "Guided: Install API"

    $pre = Invoke-PrerequisiteCheck -Scenario Api
    if (-not $pre.Ok) {
        Update-UiStep 0 "[X] 1. Prerequisites FAILED"
        [System.Windows.Forms.MessageBox]::Show(
            ("Prerequisites failed:`r`n`r`n- " + ($pre.Issues -join "`r`n- ") + "`r`n`r`nFull log:`r`n$($script:LogPath)"),
            "Cannot install API",
            "OK",
            "Error") | Out-Null
        if (-not (Test-IsAdministrator)) {
            if (Request-Elevation -Reason "API install needs Administrator.") { return }
        }
        return
    }
    Update-UiStep 0 "[OK] 1. Prerequisites"

    $inputs = Show-InputForm -Title "Heimdall API settings" -Prompt "Confirm settings for this server. Agents will call this host on the chosen port." -Fields ([ordered]@{
        Port   = "5080"
        ApiKey = "heimdall-poc-key"
    }) -AcceptLabel "Install"
    if (-not $inputs) {
        Write-HeimdallLog "API install cancelled by user." -Level WARN
        return
    }
    Update-UiStep 1 "[OK] 2. Settings Port=$($inputs.Port)"

    $ps1 = Join-Path $script:ScriptDir "install-api.ps1"
    if (-not (Test-Path $ps1)) {
        Write-HeimdallLog "install-api.ps1 not found at $ps1" -Level ERROR
        return
    }

    Write-HeimdallLog "Launching install-api.ps1 (elevated; progress window + auto-close)..." -Level STEP
    Update-UiStep 2 "[...] 3. Install HeimdallApi"
    Set-UiStatus "Installing API - accept UAC; watch progress window"
    $installStartedAt = Get-Date
    $installEstimate = $null
    if (Get-Command Get-InstallApiTimingEstimate -ErrorAction SilentlyContinue) {
        $installEstimate = Get-InstallApiTimingEstimate -StartedAt $installStartedAt
        $estMmSs = Format-InstallApiDurationMmSs -TotalSec $installEstimate.EstimatedSec
        Write-HeimdallLog "Estimated API install: ~$estMmSs (done by $($installEstimate.FinishAt.ToString('HH:mm:ss')); baseline $($installEstimate.BaselineSec)s from $($installEstimate.Source))" -Level INFO
    }
    $arg = "-NoProfile -ExecutionPolicy Bypass -File `"$ps1`" -Port $($inputs.Port) -ApiKey `"$($inputs.ApiKey)`" -NoPrompt"
    $p = Start-Process -FilePath "powershell.exe" -Verb RunAs -ArgumentList $arg -PassThru
    $installExit = -1
    if ($installEstimate -and (Get-Command Wait-ProcessWithInstallCountdown -ErrorAction SilentlyContinue)) {
        $installExit = Wait-ProcessWithInstallCountdown -Process $p -FinishAt $installEstimate.FinishAt
    }
    else {
        $installExit = Wait-ProcessWithUiPump -Process $p -StatusText "Installing API (watch elevated console)..."
        if ($installExit -eq 0) {
            Set-UiStatus "API install finished successfully"
        }
        elseif ($installExit -gt 0) {
            Set-UiStatus "API install failed (exit $installExit)"
        }
    }

    if ($installExit -ne 0) {
        Update-UiStep 2 "[X] 3. Install failed (exit $installExit)"
        Write-HeimdallLog "install-api.ps1 exited with code $installExit" -Level ERROR
        [System.Windows.Forms.MessageBox]::Show(
            "API install did not complete successfully (exit $installExit).`r`n`r`nCheck the elevated install console and:`r`n$($script:LogPath)",
            "Install failed",
            "OK",
            "Error") | Out-Null
        return
    }

    Update-UiStep 2 "[OK] 3. Install finished"
    $health = Test-ApiHealth -ApiUrl "http://localhost:$($inputs.Port)"
    if ($health.Ok) {
        Update-UiStep 3 "[OK] 4. Health OK - productVersion=$($health.Payload.productVersion)"
        Write-HeimdallLog "API health OK: $($health.Uri) version=$($health.Payload.productVersion)" -Level OK
        Set-UiStatus "API install verified"
        [System.Windows.Forms.MessageBox]::Show(
            "API appears healthy.`r`n`r`n$($health.Uri)`r`nproductVersion: $($health.Payload.productVersion)`r`n`r`nDashboard: http://localhost:$($inputs.Port)`r`nLog: $($script:LogPath)",
            "Success", "OK", "Information") | Out-Null
    }
    else {
        Update-UiStep 3 "[X] 4. Health check failed"
        Write-HeimdallLog "Health failed: $($health.Error)" -Level ERROR
        [System.Windows.Forms.MessageBox]::Show(
            "Install may have finished, but health check failed:`r`n$($health.Error)`r`n`r`nCheck the install console and:`r`n$($script:LogPath)",
            "Verify failed", "OK", "Warning") | Out-Null
    }
}

function Get-BuiltAgentProductVersion {
    <#
    Reads the ProductVersion Win32 resource straight off the just-published Heimdall.Agent.exe — this is
    exactly what Worker.cs reports as AgentVersion on heartbeat (AssemblyInformationalVersionAttribute,
    e.g. "0.1.0+<gitsha>"), so it is the most accurate "what did we actually build" value. Falls back to
    VERSION.json's productVersion (Directory.Build.props core version only, no build metadata) if the exe
    can't be read.
    #>
    param(
        [Parameter(Mandatory)][string]$PackFolder
    )
    $exe = Join-Path $PackFolder "payload\Heimdall.Agent.exe"
    if (Test-Path -LiteralPath $exe) {
        try {
            $info = [System.Diagnostics.FileVersionInfo]::GetVersionInfo($exe)
            if (-not [string]::IsNullOrWhiteSpace($info.ProductVersion)) {
                return $info.ProductVersion.Trim()
            }
        }
        catch {
            Write-HeimdallLog "Could not read ProductVersion from $exe : $($_.Exception.Message)" -Level WARN
        }
    }
    $verFile = Join-Path $PackFolder "VERSION.json"
    if (Test-Path -LiteralPath $verFile) {
        try {
            $ver = (Get-Content -Raw -Path $verFile | ConvertFrom-Json).productVersion
            if (-not [string]::IsNullOrWhiteSpace($ver)) { return [string]$ver }
        }
        catch {
            Write-HeimdallLog "Could not parse $verFile : $($_.Exception.Message)" -Level WARN
        }
    }
    return $null
}

function Publish-ClientVersionToApi {
    <#
    Best-effort: tells the API "this is the current published client version" (see Clients page /
    PublishedVersionService). Never blocks or fails the pack — a skip here just means the Clients page
    keeps its previous baseline (or stays unset) until someone sets it manually.
    #>
    param(
        [Parameter(Mandatory)][string]$Version,
        [string]$ApiUrl,
        [string]$ApiKey
    )

    if (-not $ApiUrl) {
        $settings = Read-AgentAppSettingsFromDisk
        if ($settings.Ok -and -not [string]::IsNullOrWhiteSpace($settings.ApiBaseUrl)) {
            $ApiUrl = $settings.ApiBaseUrl
            if (-not $ApiKey) { $ApiKey = $settings.ApiKey }
        }
    }
    if (-not $ApiUrl) {
        $last = Get-LastInstallSettings
        if ($last -and $last.apiUrl) { $ApiUrl = [string]$last.apiUrl }
    }
    if (-not $ApiKey) { $ApiKey = "heimdall-poc-key" }

    if ([string]::IsNullOrWhiteSpace($ApiUrl)) {
        Write-HeimdallLog "Skipped publishing client version to API (no known API URL yet). Set it manually on the Clients page once the API is known." -Level WARN
        return [pscustomobject]@{ Ok = $false; Uri = $null; Error = "No API URL known" }
    }

    $base = Normalize-ApiUrl $ApiUrl
    $uri = "$base/api/admin/published-version"
    try {
        $headers = @{ "X-Heimdall-Key" = $ApiKey }
        $body = @{ version = $Version; setBy = "Launch Control @ $env:COMPUTERNAME" } | ConvertTo-Json
        Invoke-RestMethod -Uri $uri -Headers $headers -Method Post -Body $body -ContentType "application/json" -TimeoutSec 15 | Out-Null
        Write-HeimdallLog "Published client version '$Version' to API ($uri)" -Level OK
        return [pscustomobject]@{ Ok = $true; Uri = $uri; Error = $null }
    }
    catch {
        Write-HeimdallLog "Could not publish client version to API ($uri): $($_.Exception.Message)" -Level WARN
        return [pscustomobject]@{ Ok = $false; Uri = $uri; Error = $_.Exception.Message }
    }
}

function Start-GuidedPack {
    param(
        [switch]$OfferInstallAfter
    )

    Set-UiSteps @(
        "[ ] 1. Prerequisites (.NET 10 SDK, repo, NuGet)",
        "[ ] 2. Publish self-contained agent",
        "[ ] 3. Assemble Heimdall-Client folder",
        "[ ] 4. Confirm dist\Heimdall-Client"
    )
    Set-UiStatus "Guided: Create client pack"

    $pre = Invoke-PrerequisiteCheck -Scenario Pack
    if (-not $pre.Ok) {
        Update-UiStep 0 "[X] 1. Prerequisites FAILED"
        [System.Windows.Forms.MessageBox]::Show(
            ("Prerequisites failed:`r`n`r`n- " + ($pre.Issues -join "`r`n- ")),
            "Cannot create client pack", "OK", "Error") | Out-Null
        return $false
    }
    Update-UiStep 0 "[OK] 1. Prerequisites"

    $cmd = Join-Path $script:ScriptDir "Pack-WorkstationCollector.cmd"
    if (-not (Test-Path $cmd)) {
        Write-HeimdallLog "Pack-WorkstationCollector.cmd missing" -Level ERROR
        return $false
    }

    Write-HeimdallLog "Running pack (console window; no pause when launched from Setup)..." -Level STEP
    Update-UiStep 1 "[...] 2. Publishing..."
    $prevNoPause = $env:HEIMDALL_NOPAUSE
    $env:HEIMDALL_NOPAUSE = "1"
    try {
        $p = Start-Process -FilePath "cmd.exe" -ArgumentList "/c `"$cmd`"" -WorkingDirectory $script:ScriptDir -PassThru
        $exit = Wait-ProcessWithUiPump -Process $p -StatusText "Creating client pack (watch console window)..."
        Write-HeimdallLog "Pack process exit: $exit" -Level $(if ($exit -eq 0) { "OK" } else { "ERROR" })
    }
    finally {
        if ($null -eq $prevNoPause) {
            Remove-Item env:HEIMDALL_NOPAUSE -ErrorAction SilentlyContinue
        }
        else {
            $env:HEIMDALL_NOPAUSE = $prevNoPause
        }
    }

    $out = Join-Path $script:RepoRoot "dist\Heimdall-Client"
    $exe = Join-Path $out "payload\Heimdall.Agent.exe"
    $verFile = Join-Path $out "VERSION.json"
    if (Test-Path $exe) {
        Update-UiStep 1 "[OK] 2. Agent published"
        if (Test-Path $verFile) {
            Update-UiStep 2 "[OK] 3. Heimdall-Client assembled"
            Write-HeimdallLog (Get-Content -Raw $verFile) -Level INFO
        }
        else {
            Update-UiStep 2 "[!] 3. VERSION.json missing (pack script may be outdated)"
        }
        Update-UiStep 3 "[OK] 4. Pack ready: $out"
        Set-UiStatus "Client pack ready"

        $builtVersion = Get-BuiltAgentProductVersion -PackFolder $out
        $publishNote = ""
        if ($builtVersion) {
            $publishResult = Publish-ClientVersionToApi -Version $builtVersion
            $publishNote = if ($publishResult.Ok) {
                "`r`n`r`nPublished version $builtVersion to the API's Clients page ($($publishResult.Uri))."
            }
            else {
                "`r`n`r`nCould not auto-publish version $builtVersion to the API ($($publishResult.Error)). Set it manually on the Clients page once the API URL is known."
            }
        }
        else {
            Write-HeimdallLog "Could not determine built agent version — skipped auto-publish." -Level WARN
        }

        $installNow = [System.Windows.Forms.DialogResult]::No
        if ($OfferInstallAfter) {
            $installNow = [System.Windows.Forms.MessageBox]::Show(
                "Client pack ready.`r`n`r`n$out`r`n`r`nCopy that ONE folder to other PCs, then run Install.lnk there.$publishNote`r`n`r`nInstall the agent on THIS PC now?",
                "Client pack ready",
                [System.Windows.Forms.MessageBoxButtons]::YesNo,
                [System.Windows.Forms.MessageBoxIcon]::Question)
        }
        else {
            [System.Windows.Forms.MessageBox]::Show(
                "Client pack ready.`r`n`r`n$out`r`n`r`nCopy that ONE folder to other PCs, then double-click Install.lnk.$publishNote`r`n`r`nLog: $($script:LogPath)",
                "Client pack ready", "OK", "Information") | Out-Null
        }
        Start-Process explorer.exe $out
        if ($installNow -eq [System.Windows.Forms.DialogResult]::Yes) {
            Start-GuidedCollectorInstall
        }
        return $true
    }
    else {
        Update-UiStep 1 "[X] 2. Payload missing after pack"
        Update-UiStep 3 "[X] 4. Pack incomplete"
        Write-HeimdallLog "Expected $exe after pack" -Level ERROR
        [System.Windows.Forms.MessageBox]::Show(
            "Pack did not produce payload\Heimdall.Agent.exe.`r`nUsually: missing .NET 10 SDK or NuGet.org blocked.`r`n`r`nLog: $($script:LogPath)",
            "Pack failed", "OK", "Error") | Out-Null
        return $false
    }
}

function Start-GuidedCollectorInstall {
    Set-UiSteps @(
        "[ ] 1. Prerequisites (admin + client pack)",
        "[ ] 2. Enter API URL / key / group",
        "[ ] 3. Probe API health + version",
        "[ ] 4. Install HeimdallAgent service",
        "[ ] 5. Verify service + API auth"
    )
    Set-UiStatus "Guided: Install agent on this PC"

    # Packed folder: prefer the dedicated Install.cmd wizard (one install UX)
    if ($script:IsPackedLayout) {
        $installCmd = Join-Path $script:ScriptDir "Install.cmd"
        if (Test-Path -LiteralPath $installCmd) {
            Write-HeimdallLog "Opening Install.cmd guided wizard..." -Level STEP
            Set-UiStatus "Opening Install wizard..."
            Start-Process -FilePath $installCmd -WorkingDirectory $script:ScriptDir
            Set-UiSteps @(
                "Opened Install.lnk / Install.cmd wizard.",
                "Complete the prompts in that window.",
                "Then use Client health check here if needed."
            )
            return
        }
    }

    $pre = Invoke-PrerequisiteCheck -Scenario Collector
    if (-not $pre.Ok) {
        $missingPayload = ($pre.Issues | Where-Object { $_ -match "payload\\" }).Count -gt 0
        if ($missingPayload -and -not $script:IsPackedLayout -and $script:RepoRoot) {
            $r = [System.Windows.Forms.MessageBox]::Show(
                "No client pack found yet (dist\Heimdall-Client\payload).`r`n`r`nCreate the client pack now, then continue installing on this PC?",
                "Client pack required",
                [System.Windows.Forms.MessageBoxButtons]::YesNo,
                [System.Windows.Forms.MessageBoxIcon]::Question)
            if ($r -eq [System.Windows.Forms.DialogResult]::Yes) {
                $packed = Start-GuidedPack -OfferInstallAfter:$false
                if ($packed) {
                    Start-GuidedCollectorInstall
                }
                return
            }
        }
        Update-UiStep 0 "[X] 1. Prerequisites FAILED"
        $msg = "Prerequisites failed:`r`n`r`n- " + ($pre.Issues -join "`r`n- ")
        if ($pre.Notes.Count) { $msg += "`r`n`r`nNotes:`r`n- " + ($pre.Notes -join "`r`n- ") }
        $msg += "`r`n`r`nLog:`r`n$($script:LogPath)"
        [System.Windows.Forms.MessageBox]::Show($msg, "Cannot install agent", "OK", "Error") | Out-Null
        if (-not (Test-IsAdministrator)) {
            if (Request-Elevation -Reason "Agent install needs Administrator.") { return }
        }
        return
    }
    Update-UiStep 0 "[OK] 1. Prerequisites"

    $defaultUrl = Get-DefaultCollectorApiUrl
    $defaultGroup = Get-DefaultCollectorMachineGroup
    # Prefer non-localhost hint when this is clearly a remote target
    $inputs = Show-InputForm -Title "Agent connection settings" `
        -Prompt "Enter the Heimdall API this PC should report to.`r`nDo NOT use localhost unless the API runs on THIS machine.`r`nUse the server hostname or IP (e.g. http://YOUR-SERVER:5080)." `
        -Fields ([ordered]@{
            ApiUrl       = $defaultUrl
            ApiKey       = "heimdall-poc-key"
            MachineGroup = $defaultGroup
        }) -AcceptLabel "Next"
    if (-not $inputs) {
        Write-HeimdallLog "Collector install cancelled." -Level WARN
        return
    }
    if ([string]::IsNullOrWhiteSpace($inputs.ApiUrl)) {
        [System.Windows.Forms.MessageBox]::Show("ApiUrl is required.", "Missing input", "OK", "Warning") | Out-Null
        return
    }
    if ($inputs.ApiUrl -match "localhost|127\.0\.0\.1" ) {
        $r = [System.Windows.Forms.MessageBox]::Show(
            "ApiUrl is localhost. That only works if Heimdall API is installed on THIS PC.`r`n`r`nRemote collectors must use the API server hostname or IP (e.g. http://SERVER:5080).`r`n`r`nContinue anyway?",
            "Localhost check",
            [System.Windows.Forms.MessageBoxButtons]::YesNo,
            [System.Windows.Forms.MessageBoxIcon]::Warning)
        if ($r -ne [System.Windows.Forms.DialogResult]::Yes) { return }
    }
    Update-UiStep 1 "[OK] 2. ApiUrl=$($inputs.ApiUrl) Group=$($inputs.MachineGroup)"

    Write-HeimdallLog "Probing API..." -Level STEP
    $health = Test-ApiHealth -ApiUrl $inputs.ApiUrl
    $localVer = Read-LocalPackVersion
    $localPv = if ($localVer -and $localVer.productVersion) { $localVer.productVersion } else { $script:ProductVersionExpected }

    if ($health.Ok) {
        $serverPv = [string]$health.Payload.productVersion
        Write-HeimdallLog "API reachable. productVersion=$serverPv machine=$($health.Payload.machineName)" -Level OK
        $versionOk = Test-HeimdallProductVersionAccept -LocalVersion $localPv -ServerVersion $serverPv -Log {
            param([string]$Message, [string]$Level)
            Write-HeimdallLog $Message -Level $Level
        } -ConfirmMismatch {
            param([string]$PackPv, [string]$SrvPv)
            $r = [System.Windows.Forms.MessageBox]::Show(
                "API is reachable, but product versions differ (core SemVer).`r`n`r`nPack:   $PackPv`r`nServer: $SrvPv`r`n`r`nInstall anyway?",
                "Version mismatch",
                [System.Windows.Forms.MessageBoxButtons]::YesNo,
                [System.Windows.Forms.MessageBoxIcon]::Warning)
            return ($r -eq [System.Windows.Forms.DialogResult]::Yes)
        }
        if (-not $versionOk) { return }
        if (Test-HeimdallProductVersionMatch -VersionA $localPv -VersionB $serverPv) {
            $corePv = Get-HeimdallCoreProductVersion -Version $localPv
            Update-UiStep 2 "[OK] 3. Health OK - productVersion=$corePv"
        }
        else {
            Update-UiStep 2 "[!] 3. Health OK - version mismatch pack=$localPv server=$serverPv"
        }

        $auth = Test-ApiConfigAuth -ApiUrl $inputs.ApiUrl -ApiKey $inputs.ApiKey
        if ($auth.Ok) {
            Write-HeimdallLog "API key accepted by /api/config" -Level OK
        }
        else {
            Write-HeimdallLog "API key check failed (status=$($auth.Status)): $($auth.Error)" -Level WARN
            $r = [System.Windows.Forms.MessageBox]::Show(
                "Health OK but API key was rejected by /api/config (HTTP $($auth.Status)).`r`nKeys must match the server.`r`n`r`nContinue install anyway?",
                "API key check",
                [System.Windows.Forms.MessageBoxButtons]::YesNo,
                [System.Windows.Forms.MessageBoxIcon]::Warning)
            if ($r -ne [System.Windows.Forms.DialogResult]::Yes) { return }
        }
    }
    else {
        Write-HeimdallLog "API not reachable: $($health.Error)" -Level WARN
        $r = [System.Windows.Forms.MessageBox]::Show(
            "Cannot reach $($health.Uri)`r`n$($health.Error)`r`n`r`nInstall can continue (agent queues offline), but heartbeats will fail until the URL/firewall is fixed.`r`n`r`nContinue?",
            "API unreachable",
            [System.Windows.Forms.MessageBoxButtons]::YesNo,
            [System.Windows.Forms.MessageBoxIcon]::Warning)
        if ($r -ne [System.Windows.Forms.DialogResult]::Yes) { return }
        Update-UiStep 2 "[!] 3. API unreachable - continuing"
    }

    $installer = Get-InstallerCmdPath
    $payload = Get-PayloadPath
    if (-not $installer -or -not $payload) {
        Write-HeimdallLog "Installer or payload path missing." -Level ERROR
        Update-UiStep 3 "[X] 4. Missing installer/payload"
        return
    }

    $packDir = Split-Path -Parent $installer
    $installCmdWizard = Join-Path $packDir "Install.cmd"
    if (Test-Path -LiteralPath $installCmdWizard) {
        Write-HeimdallLog "Tip: Install.cmd in pack folder runs the guided Install-Client wizard." -Level INFO
    }

    Write-HeimdallLog "Starting installer: $installer" -Level STEP
    Write-HeimdallLog "Pack folder: $packDir" -Level INFO
    Write-HeimdallLog "Payload: $payload" -Level INFO
    Update-UiStep 3 "[...] 4. Installing service..."
    Set-UiStatus "Installing - accept UAC; installer window will pause at end"

    $exit = Invoke-HeimdallElevatedCollectorInstall `
        -InstallerCmdPath $installer `
        -ApiUrl (Normalize-ApiUrl $inputs.ApiUrl) `
        -ApiKey $inputs.ApiKey `
        -MachineGroup $inputs.MachineGroup `
        -PayloadPath $payload `
        -AlreadyElevated:(Test-IsAdministrator) `
        -PumpUi { [System.Windows.Forms.Application]::DoEvents() } `
        -Log { param($m, $l) Write-HeimdallLog $m -Level $l }

    Write-HeimdallLog "Installer process exit: $exit" -Level $(if ($exit -eq 0) { "OK" } else { "ERROR" })

    if ($exit -ne 0) {
        $installLog = Get-HeimdallInstallAgentLogTail -LineCount 30 -LogRoot $script:LogRoot
        if ($installLog) {
            Write-HeimdallLog "Latest service install log: $($installLog.Path)" -Level INFO
            foreach ($line in $installLog.Lines) {
                Write-HeimdallLog "  install> $line" -Level INFO
            }
        }
        else {
            Write-HeimdallLog "No install-agent-*.log found under $($script:LogRoot) (see install> lines above from console capture)" -Level WARN
        }
    }

    # Verify
    Write-HeimdallLog "Post-install verification..." -Level STEP
    $svc = Get-Service -Name HeimdallAgent -ErrorAction SilentlyContinue
    $svcOk = $svc -and $svc.Status -eq "Running"
    $exeOk = Test-Path (Join-Path $script:AgentInstallDir "Heimdall.Agent.exe")
    $diskSettings = Read-AgentAppSettingsFromDisk
    $settingsOk = $diskSettings.Ok
    $expectedUrl = Normalize-ApiUrl $inputs.ApiUrl
    $diskUrl = if ($diskSettings.ApiBaseUrl) { Normalize-ApiUrl $diskSettings.ApiBaseUrl } else { "" }
    $urlMatchOk = $settingsOk -and ($diskUrl -eq $expectedUrl)

    if ($svcOk) {
        Update-UiStep 3 "[OK] 4. HeimdallAgent RUNNING"
        Write-HeimdallLog "Service HeimdallAgent is Running" -Level OK
    }
    else {
        Update-UiStep 3 "[X] 4. Service not running (status=$($svc.Status))"
        Write-HeimdallLog "Service check failed. Status=$($svc.Status)" -Level ERROR
    }

    if (-not $settingsOk) {
        Write-HeimdallLog "appsettings.json read failed: $($diskSettings.Error)" -Level ERROR
    }
    elseif (-not $urlMatchOk) {
        Write-HeimdallLog "ApiBaseUrl MISMATCH on disk: expected='$expectedUrl' actual='$diskUrl'" -Level ERROR
    }
    else {
        Write-HeimdallLog "ApiBaseUrl on disk matches install input: $diskUrl" -Level OK
    }

    $diskUrlDisplay = if ([string]::IsNullOrWhiteSpace($diskUrl)) { "(missing)" } else { $diskUrl }
    $expectedUrlDisplay = if ([string]::IsNullOrWhiteSpace($expectedUrl)) { "(empty)" } else { $expectedUrl }

    $verifyBits = @()
    $verifyBits += "Service running: $svcOk"
    $verifyBits += "Agent exe present: $exeOk"
    $verifyBits += "appsettings present: $settingsOk"
    $verifyBits += "ApiBaseUrl match: $urlMatchOk | disk='$diskUrlDisplay' expected='$expectedUrlDisplay'"
    $health2 = Test-ApiHealth -ApiUrl $inputs.ApiUrl
    $verifyBits += "API health: $($health2.Ok)"
    if ($health2.Ok) {
        $auth2 = Test-ApiConfigAuth -ApiUrl $inputs.ApiUrl -ApiKey $inputs.ApiKey
        $verifyBits += "API key /config: $($auth2.Ok)"
    }

    foreach ($b in $verifyBits) { Write-HeimdallLog "Verify: $b" -Level INFO }

    $verifyOk = $svcOk -and $exeOk -and $settingsOk -and $urlMatchOk
    if ($verifyOk) {
        Save-LastInstallSettings -ApiUrl $inputs.ApiUrl -MachineGroup $inputs.MachineGroup
        Update-UiStep 4 "[OK] 5. Verify passed - host should appear on Machines after first heartbeat"
        Set-UiStatus "Collector install verified"
        [System.Windows.Forms.MessageBox]::Show(
            ("Install looks good.`r`n`r`n" + ($verifyBits -join "`r`n") + "`r`n`r`nHostname: $env:COMPUTERNAME`r`nDashboard Machines page after ~1 minute.`r`n`r`nLog: $($script:LogPath)"),
            "Success", "OK", "Information") | Out-Null
    }
    else {
        Update-UiStep 4 "[X] 5. Verify failed"
        Set-UiStatus "Verify failed - see log"
        $extra = ""
        if ($exit -ne 0) {
            $installLog = Get-HeimdallInstallAgentLogTail -LineCount 30 -LogRoot $script:LogRoot
            if ($installLog) {
                $extra = "`r`n`r`nService install exited $exit. Last lines from:`r`n$($installLog.Path)`r`n`r`n$($installLog.Text)"
            }
            else {
                $extra = "`r`n`r`nService install exited $exit. Check launch-control log for install> console capture and install-agent-*.log under $($script:LogRoot)."
            }
        }
        if ($settingsOk -and -not $urlMatchOk) {
            $extra += "`r`n`r`nApiBaseUrl on disk does not match what you entered. Fix appsettings.json or re-run install with the correct URL."
        }
        [System.Windows.Forms.MessageBox]::Show(
            ("Verification failed.`r`n`r`n" + ($verifyBits -join "`r`n") + $extra + "`r`n`r`nOpen the logs folder and send the latest install-client-*.log, install-agent-*.log, and launch-control-*.log.`r`n`r`n$($script:LogPath)"),
            "Verify failed", "OK", "Error") | Out-Null
        Open-LogsFolder
    }
}

function Start-ClientHealthCheck {
    Set-UiSteps @(
        "[ ] 1. HeimdallAgent service",
        "[ ] 2. appsettings.json (ApiBaseUrl, key, group, queue)",
        "[ ] 3. API /api/health",
        "[ ] 4. API /api/config auth",
        "[ ] 5. queue.db + Application log",
        "[ ] 6. Save logs (local + network drop)"
    )
    Set-UiStatus "Client health / connect check"

    $result = Invoke-ClientHealthCheck
    if ($result.Ok) {
        Update-UiStep 0 "[OK] 1. Service"
        Update-UiStep 1 "[OK] 2. Settings"
        Update-UiStep 2 "[OK] 3. Health"
        Update-UiStep 3 "[OK] 4. Config auth"
        Update-UiStep 4 "[OK] 5. Queue / events"
        Update-UiStep 5 "[OK] 6. Logs saved"
        Set-UiStatus "Client check passed"
        [System.Windows.Forms.MessageBox]::Show(
            "Client health check passed.`r`n`r`nLog:`r`n$($result.LogPath)`r`nSummary:`r`n$($result.SummaryPath)`r`n`r`nOn the API server, open remote logs and browse logs\clients\$env:COMPUTERNAME\ if network drop succeeded.",
            "Client check", "OK", "Information") | Out-Null
    }
    else {
        Update-UiStep 5 "[!] 6. Logs saved (see warnings)"
        Set-UiStatus "Client check found issues"
        [System.Windows.Forms.MessageBox]::Show(
            "Client health check completed with issues (errors=$($result.ErrorCount) warnings=$($result.WarnCount)).`r`n`r`nLog:`r`n$($result.LogPath)`r`n`r`nReview the progress log for details.",
            "Client check", "OK", "Warning") | Out-Null
    }
}

function Start-Diagnostics {
    Set-UiStatus "Collecting diagnostics..."
    $cmd = Join-Path $script:ScriptDir "Collect-Diagnostics.cmd"
    if (-not (Test-Path $cmd) -and $script:RepoRoot) {
        $cmd = Join-Path $script:RepoRoot "scripts\Collect-Diagnostics.cmd"
    }
    if (Test-Path $cmd) {
        $p = Start-Process -FilePath "cmd.exe" -ArgumentList "/c `"$cmd`"" -PassThru
        $null = Wait-ProcessWithUiPump -Process $p -StatusText "Collecting diagnostics..."
        Write-HeimdallLog "Diagnostics script finished." -Level OK
    }
    else {
        Write-HeimdallLog "Collect-Diagnostics.cmd not found in this layout." -Level ERROR
        [System.Windows.Forms.MessageBox]::Show("Diagnostics script not found. Use a full repo clone or copy Collect-Diagnostics into the pack.", "Missing", "OK", "Warning") | Out-Null
    }
}

# ---------------------------------------------------------------------------
# Guide branches (Steps panel)
# ---------------------------------------------------------------------------

function Get-HeimdallGuideBranches {
    $client = @(
        [pscustomobject]@{
            Title  = "1. Prepare (before you start)"
            Detail = @'
Client install = put the Heimdall Agent on workstations.

Prepare in advance:
- Heimdall API must already be running on your server (see Server install branch if not).
- Know the API URL agents will use, e.g. http://YOUR-SERVER:5080 (not localhost unless API is on the same PC).
- Default POC API key: heimdall-poc-key (must match the server).
- Build PC needs .NET 10 SDK + nuget.org once, to Create client pack.
- Target PCs need a local Administrator account. No .NET SDK on targets.
- For Push: your account must reach \\HOSTNAME\C$ (admin + SMB / File Sharing).

Where things live after pack:
- Build PC: dist\Heimdall-Client\  (or zip dist\heimdall-client.zip)
- After push: \\HOST\C$\Temp\Heimdall-Client\

'@
        },
        [pscustomobject]@{
            Title  = "2. Create client pack (build PC)"
            Detail = @'
On the build PC (full Heimdall repo):

1. Open scripts\Heimdall-Setup.lnk (this window).
2. Click left: Create client pack.
3. Wait for the console publish to finish (first run can take several minutes).
4. Success creates: dist\Heimdall-Client\
   - Install.lnk  ← what clients run
   - payload\Heimdall.Agent.exe  ← required
   - VERSION.json

Look for:
- Explorer may open the pack folder.
- Setup may ask: Install the agent on THIS PC now? (optional)

Pack again only when the agent changes (or if dist\Heimdall-Client is missing).
Reuse the same pack on every PC until then.

If pack fails (NU1101): nuget.org blocked or only offline VS feeds — fix NuGet, then retry.

'@
        },
        [pscustomobject]@{
            Title  = "3. Push pack to a PC (or copy folder)"
            Detail = @'
Preferred (from this Setup window):

1. Click left: Push client pack to PC...
2. Type the target hostname or IP (C$ required).
3. Click Push.
4. Setup copies to \\HOST\C$\Temp\Heimdall-Client and opens that folder in Explorer.

Manual alternative:
- Copy the whole dist\Heimdall-Client\ folder (or unzip heimdall-client.zip) to the target PC.
- Do not copy docs\portable-client\ from the repo — that is documentation only.

Look for on the share:
- Install.lnk
- payload\Heimdall.Agent.exe

If C$ is unreachable: check hostname, admin rights, SMB port 445, firewall admin shares.

'@
        },
        [pscustomobject]@{
            Title  = "4. Run Install.lnk on the target"
            Detail = @'
On the target PC (local Administrator):

1. Open C:\Temp\Heimdall-Client\ (after push) or your copied pack folder.
2. Double-click Install.lnk (helmet icon). Accept UAC if prompted.
3. Wizard steps:
   - Prerequisites (payload present, admin)
   - Connection: ApiUrl, ApiKey, MachineGroup (e.g. SOE)
   - Test connection (health + version + API key)
   - Install service HeimdallAgent
   - Verify

Look for:
- Success message at the end of the wizard.
- Service Running: Get-Service HeimdallAgent
- Files: %ProgramFiles%\Heimdall\Agent\
- Logs: %ProgramData%\Heimdall\logs\install-client-*.log

Do not use localhost for ApiUrl unless the API runs on that same PC.

'@
        },
        [pscustomobject]@{
            Title  = "5. Verify on dashboard"
            Detail = @'
After install:

1. On the server, open the dashboard (Setup → Open dashboard, or http://SERVER:5080).
2. Machines page: hostname should appear after the first heartbeat (usually 1–2 minutes).
3. Optional from Setup: Client health check (service, settings, API probes).
4. Logs: %ProgramData%\Heimdall\logs\ on the client; remote via Open remote logs folder...

If the machine never appears:
- ApiUrl / firewall / API key mismatch
- Check agent log under ProgramData\Heimdall\logs\

'@
        }
    )

    $server = @(
        [pscustomobject]@{
            Title  = "1. Prepare (server PC)"
            Detail = @'
Server install = Heimdall API + dashboard on one Windows PC.

Prepare in advance:
- Windows Server or workstation with local Administrator.
- .NET 10 SDK installed (dotnet --list-sdks shows a 10.x line).
- Full Heimdall git clone (not the portable client pack alone).
- Choose API port (default 5080). Plan firewall allow for inbound TCP.
- Pick an API key (POC default: heimdall-poc-key). Agents must use the same key.

This Setup window must be opened from the repo (scripts\Heimdall-Setup.lnk), not from a packed client folder.

'@
        },
        [pscustomobject]@{
            Title  = "2. Install API on this PC"
            Detail = @'
1. Open scripts\Heimdall-Setup.lnk on the server.
2. Click left: Install API on this PC.
3. Confirm port and API key when prompted. Accept UAC.
4. Wait for publish + Windows service HeimdallApi to start.

What it does:
- Stops HeimdallApi if running (no manual stop needed), publishes, then recreates and starts the service
- Publishes to %ProgramFiles%\Heimdall\Api\
- SQLite DB: %ProgramData%\Heimdall\heimdall.db
- Firewall rule for the chosen port (when policy allows)
- Log: %ProgramData%\Heimdall\logs\install-api-*.log

Look for:
- Setup verify step succeeds against /api/health
- productVersion in health matches your build (core SemVer, e.g. 0.1.0)

'@
        },
        [pscustomobject]@{
            Title  = "3. Verify API / dashboard"
            Detail = @'
1. Setup → Open dashboard... (or browse http://THIS-PC:5080).
2. Or: curl / Invoke-RestMethod http://localhost:5080/api/health
3. Get-Service HeimdallApi → Running

If agents are on other PCs, they must reach http://SERVER-HOSTNAME:5080 (not localhost).

Optional tools in this window:
- Backup API database...
- Remove seed/demo machines...
- Open logs folder

'@
        },
        [pscustomobject]@{
            Title  = "4. Next: deploy agents (Client branch)"
            Detail = @'
API alone does not collect workstation data. Switch the Steps branch above to Client install (default view) and:

1. Create client pack (once per agent build)
2. Push client pack to PC... (or copy dist\Heimdall-Client)
3. On each target: Install.lnk
4. Confirm hostnames on the Machines page

You do not reinstall the API when only the agent changes — just re-pack and push/install the client.

'@
        }
    )

    return [ordered]@{
        Client = $client
        Server = $server
    }
}

function Show-GuideBranch {
    param(
        [ValidateSet("Client", "Server")]
        [string]$Branch = "Client"
    )
    $script:UiGuideBranch = $Branch
    if (-not $script:GuideStepsByBranch) {
        $script:GuideStepsByBranch = Get-HeimdallGuideBranches
    }
    if (-not $script:UiGuideList -or $script:UiGuideList.IsDisposed) { return }

    $script:UiGuideList.Items.Clear()
    foreach ($step in $script:GuideStepsByBranch[$Branch]) {
        [void]$script:UiGuideList.Items.Add($step.Title)
    }
    if ($script:UiGuideList.Items.Count -gt 0) {
        $script:UiGuideList.SelectedIndex = 0
    }
    else {
        Show-GuideStepDetail -Index -1
    }
}

function Show-GuideStepDetail {
    param([int]$Index)
    if (-not $script:UiGuideDetail -or $script:UiGuideDetail.IsDisposed) { return }
    if (-not $script:GuideStepsByBranch) {
        $script:GuideStepsByBranch = Get-HeimdallGuideBranches
    }
    $branch = $script:UiGuideBranch
    if ($Index -lt 0 -or -not $script:GuideStepsByBranch[$branch] -or $Index -ge $script:GuideStepsByBranch[$branch].Count) {
        $script:UiGuideDetail.Text = "Select a step on the left for detailed instructions."
        return
    }
    $step = $script:GuideStepsByBranch[$branch][$Index]
    $script:UiGuideDetail.Text = $step.Detail.Trim() + "`r`n"
}

function Show-LaunchControl {
    Initialize-HeimdallLogging | Out-Null

    $form = New-Object System.Windows.Forms.Form
    $form.Text = "Heimdall Setup"
    $form.Width = 1100
    $form.Height = 860
    $form.StartPosition = "CenterScreen"
    $form.MinimumSize = New-Object System.Drawing.Size(980, 720)
    $form.Font = New-Object System.Drawing.Font("Segoe UI", 9)

    $header = New-Object System.Windows.Forms.Label
    $header.Text = "Heimdall Setup"
    $header.Font = New-Object System.Drawing.Font("Segoe UI Semibold", 14)
    $header.Left = 16
    $header.Top = 12
    $header.Width = 500
    $header.Height = 28
    $form.Controls.Add($header)

    $sub = New-Object System.Windows.Forms.Label
    $sub.Text = "Left = actions. Right = guided steps (Client by default). Click a step for details. Logs: %ProgramData%\Heimdall\logs\"
    $sub.Left = 16
    $sub.Top = 42
    $sub.Width = 1050
    $sub.Height = 22
    $form.Controls.Add($sub)

    $btnPanel = New-Object System.Windows.Forms.Panel
    $btnPanel.Left = 12
    $btnPanel.Top = 72
    $btnPanel.Width = 280
    $btnPanel.Height = 560
    $btnPanel.AutoScroll = $true
    $btnPanel.HorizontalScroll.Enabled = $false
    $btnPanel.HorizontalScroll.Visible = $false
    $btnPanel.AutoScrollMargin = New-Object System.Drawing.Size(0, 8)
    $btnPanel.Anchor = "Top, Bottom, Left"
    $form.Controls.Add($btnPanel)

    function New-ActionButton($text, $top, $enabled = $true) {
        $b = New-Object System.Windows.Forms.Button
        $b.Text = $text
        $b.Left = 0
        $b.Top = $top
        $b.Width = [Math]::Max(200, $btnPanel.ClientSize.Width - 8)
        $b.Height = 40
        $b.Enabled = $enabled
        $b.TextAlign = [System.Drawing.ContentAlignment]::MiddleLeft
        $b.Padding = New-Object System.Windows.Forms.Padding(8, 0, 0, 0)
        $b.Anchor = [System.Windows.Forms.AnchorStyles]::Top -bor [System.Windows.Forms.AnchorStyles]::Left -bor [System.Windows.Forms.AnchorStyles]::Right
        $btnPanel.Controls.Add($b)
        return $b
    }

    if ($script:IsPackedLayout) {
        $btnAgent = New-ActionButton "1. Install agent on this PC" 0 $true
        $btnPush = New-ActionButton "2. Push client pack to PC(s)..." 48 $true
        $btnClientCheck = New-ActionButton "3. Client health check" 96 $true
        $btnLogs = New-ActionButton "4. Open logs folder" 144 $true
        $btnRemoteLogs = New-ActionButton "5. Open remote logs folder..." 192 $true
        $btnBackupDb = New-ActionButton "6. Backup API database..." 240 $true
        $btnDash = New-ActionButton "7. Open dashboard..." 288 $true
        $btnPre = New-ActionButton "Check prerequisites" 336 $true
        $btnApi = $null
        $btnPack = $null
        $btnRemoveDemos = $null
        $btnDiag = $null
    }
    else {
        $btnApi = New-ActionButton "1. Install API on this PC" 0 $true
        $btnPack = New-ActionButton "2. Create client pack" 48 $true
        $btnPush = New-ActionButton "3. Push client pack to PC(s)..." 96 $true
        $btnAgent = New-ActionButton "4. Install agent on this PC" 144 $true
        $btnClientCheck = New-ActionButton "5. Client health check" 192 $true
        $btnLogs = New-ActionButton "6. Open logs folder" 240 $true
        $btnRemoteLogs = New-ActionButton "7. Open remote logs folder..." 288 $true
        $btnBackupDb = New-ActionButton "8. Backup API database..." 336 $true
        $btnRemoveDemos = New-ActionButton "9. Remove seed/demo machines..." 384 $true
        $btnDiag = New-ActionButton "10. Collect diagnostics" 432 $true
        $btnDash = New-ActionButton "11. Open dashboard..." 480 $true
        $btnPre = New-ActionButton "Check prerequisites" 528 $true
    }

    foreach ($btn in @($btnApi, $btnPack, $btnPush, $btnAgent, $btnClientCheck, $btnLogs, $btnRemoteLogs, $btnBackupDb, $btnRemoveDemos, $btnDiag, $btnDash, $btnPre)) {
        if ($btn) { Register-LaunchControlActionButton -Button $btn }
    }

    $servicesGroup = New-Object System.Windows.Forms.GroupBox
    $servicesGroup.Text = "Windows services"
    $servicesGroup.Left = 12
    $servicesGroup.Width = 280
    $servicesGroup.Height = 148
    $servicesGroup.Anchor = "Bottom, Left"
    $form.Controls.Add($servicesGroup)

    function New-ServiceControlButton {
        param(
            [System.Windows.Forms.Control]$Parent,
            [int]$Left,
            [int]$Top,
            [string]$Text,
            [string]$Tag
        )
        $b = New-Object System.Windows.Forms.Button
        $b.Text = $Text
        $b.Left = $Left
        $b.Top = $Top
        $b.Width = 78
        $b.Height = 24
        $b.Tag = $Tag
        $Parent.Controls.Add($b)
        return $b
    }

    $lblApiSvc = New-Object System.Windows.Forms.Label
    $lblApiSvc.Text = "Heimdall API"
    $lblApiSvc.Left = 10
    $lblApiSvc.Top = 20
    $lblApiSvc.Width = 100
    $lblApiSvc.Height = 18
    $servicesGroup.Controls.Add($lblApiSvc)

    $lblApiStatus = New-Object System.Windows.Forms.Label
    $lblApiStatus.Text = "..."
    $lblApiStatus.Left = 112
    $lblApiStatus.Top = 20
    $lblApiStatus.Width = 150
    $lblApiStatus.Height = 18
    $lblApiStatus.TextAlign = [System.Drawing.ContentAlignment]::TopRight
    $servicesGroup.Controls.Add($lblApiStatus)

    $btnApiStart = New-ServiceControlButton -Parent $servicesGroup -Left 10 -Top 42 -Text "Start" -Tag "Start"
    $btnApiStop = New-ServiceControlButton -Parent $servicesGroup -Left 94 -Top 42 -Text "Stop" -Tag "Stop"
    $btnApiRestart = New-ServiceControlButton -Parent $servicesGroup -Left 178 -Top 42 -Text "Restart" -Tag "Restart"

    $lblAgentSvc = New-Object System.Windows.Forms.Label
    $lblAgentSvc.Text = "Heimdall Agent"
    $lblAgentSvc.Left = 10
    $lblAgentSvc.Top = 72
    $lblAgentSvc.Width = 100
    $lblAgentSvc.Height = 18
    $servicesGroup.Controls.Add($lblAgentSvc)

    $lblAgentStatus = New-Object System.Windows.Forms.Label
    $lblAgentStatus.Text = "..."
    $lblAgentStatus.Left = 112
    $lblAgentStatus.Top = 72
    $lblAgentStatus.Width = 150
    $lblAgentStatus.Height = 18
    $lblAgentStatus.TextAlign = [System.Drawing.ContentAlignment]::TopRight
    $servicesGroup.Controls.Add($lblAgentStatus)

    $btnAgentStart = New-ServiceControlButton -Parent $servicesGroup -Left 10 -Top 90 -Text "Start" -Tag "Start"
    $btnAgentStop = New-ServiceControlButton -Parent $servicesGroup -Left 94 -Top 90 -Text "Stop" -Tag "Stop"
    $btnAgentRestart = New-ServiceControlButton -Parent $servicesGroup -Left 178 -Top 90 -Text "Restart" -Tag "Restart"

    $btnSvcRefresh = New-Object System.Windows.Forms.Button
    $btnSvcRefresh.Text = "Refresh status"
    $btnSvcRefresh.Left = 10
    $btnSvcRefresh.Top = 118
    $btnSvcRefresh.Width = 258
    $btnSvcRefresh.Height = 24
    $servicesGroup.Controls.Add($btnSvcRefresh)

    $script:HeimdallServiceUi = @{
        ApiStatus    = $lblApiStatus
        AgentStatus  = $lblAgentStatus
        ApiButtons   = @($btnApiStart, $btnApiStop, $btnApiRestart)
        AgentButtons = @($btnAgentStart, $btnAgentStop, $btnAgentRestart)
    }
    $script:HeimdallServiceStatusRefresh = { Update-HeimdallServiceStatusUi }

    $btnApiStart.Add_Click({ Invoke-LaunchControlAction { Invoke-HeimdallServiceControl -ServiceName "HeimdallApi" -Action "Start" } })
    $btnApiStop.Add_Click({ Invoke-LaunchControlAction { Invoke-HeimdallServiceControl -ServiceName "HeimdallApi" -Action "Stop" } })
    $btnApiRestart.Add_Click({ Invoke-LaunchControlAction { Invoke-HeimdallServiceControl -ServiceName "HeimdallApi" -Action "Restart" } })
    $btnAgentStart.Add_Click({ Invoke-LaunchControlAction { Invoke-HeimdallServiceControl -ServiceName "HeimdallAgent" -Action "Start" } })
    $btnAgentStop.Add_Click({ Invoke-LaunchControlAction { Invoke-HeimdallServiceControl -ServiceName "HeimdallAgent" -Action "Stop" } })
    $btnAgentRestart.Add_Click({ Invoke-LaunchControlAction { Invoke-HeimdallServiceControl -ServiceName "HeimdallAgent" -Action "Restart" } })
    $btnSvcRefresh.Add_Click({ Update-HeimdallServiceStatusUi; Set-UiStatus "Service status refreshed." })

    $guideBranchLabel = New-Object System.Windows.Forms.Label
    $guideBranchLabel.Text = "Steps branch"
    $guideBranchLabel.Left = 310
    $guideBranchLabel.Top = 72
    $guideBranchLabel.Width = 200
    $form.Controls.Add($guideBranchLabel)

    $radioClient = New-Object System.Windows.Forms.RadioButton
    $radioClient.Text = "1. Client install"
    $radioClient.Left = 310
    $radioClient.Top = 94
    $radioClient.Width = 150
    $radioClient.Checked = $true
    $form.Controls.Add($radioClient)

    $radioServer = New-Object System.Windows.Forms.RadioButton
    $radioServer.Text = "2. Server install"
    $radioServer.Left = 470
    $radioServer.Top = 94
    $radioServer.Width = 150
    $radioServer.Checked = $false
    $form.Controls.Add($radioServer)

    $guideStepsLabel = New-Object System.Windows.Forms.Label
    $guideStepsLabel.Text = "Steps (click for details)"
    $guideStepsLabel.Left = 310
    $guideStepsLabel.Top = 122
    $guideStepsLabel.Width = 280
    $form.Controls.Add($guideStepsLabel)

    $guideList = New-Object System.Windows.Forms.ListBox
    $guideList.Left = 310
    $guideList.Top = 146
    $guideList.Width = 300
    $guideList.Height = 150
    $form.Controls.Add($guideList)
    $script:UiGuideList = $guideList

    $guideDetailLabel = New-Object System.Windows.Forms.Label
    $guideDetailLabel.Text = "Step details"
    $guideDetailLabel.Left = 620
    $guideDetailLabel.Top = 122
    $guideDetailLabel.Width = 200
    $form.Controls.Add($guideDetailLabel)

    $guideDetail = New-Object System.Windows.Forms.TextBox
    $guideDetail.Left = 620
    $guideDetail.Top = 146
    $guideDetail.Width = 450
    $guideDetail.Height = 150
    $guideDetail.Multiline = $true
    $guideDetail.ScrollBars = "Vertical"
    $guideDetail.ReadOnly = $true
    $guideDetail.Font = New-Object System.Drawing.Font("Segoe UI", 9)
    $guideDetail.BackColor = [System.Drawing.Color]::White
    $form.Controls.Add($guideDetail)
    $script:UiGuideDetail = $guideDetail

    $actionStepsLabel = New-Object System.Windows.Forms.Label
    $actionStepsLabel.Text = "Action progress (updates when you run a left-side action)"
    $actionStepsLabel.Left = 310
    $actionStepsLabel.Top = 304
    $actionStepsLabel.Width = 500
    $form.Controls.Add($actionStepsLabel)

    $steps = New-Object System.Windows.Forms.ListBox
    $steps.Left = 310
    $steps.Top = 328
    $steps.Width = 760
    $steps.Height = 72
    $form.Controls.Add($steps)
    $script:UiSteps = $steps

    $logLabel = New-Object System.Windows.Forms.Label
    $logLabel.Text = "Progress log (also saved to disk)"
    $logLabel.Left = 310
    $logLabel.Top = 408
    $logLabel.Width = 400
    $form.Controls.Add($logLabel)

    $logBox = New-Object System.Windows.Forms.RichTextBox
    $logBox.Left = 310
    $logBox.Top = 432
    $logBox.Width = 760
    $logBox.Height = 180
    $logBox.ReadOnly = $true
    $logBox.Font = New-Object System.Drawing.Font("Consolas", 9)
    $logBox.BackColor = [System.Drawing.Color]::WhiteSmoke
    $form.Controls.Add($logBox)
    $script:UiLogBox = $logBox

    $status = New-Object System.Windows.Forms.Label
    $status.Text = "Ready"
    $status.Left = 16
    $status.Top = 650
    $status.Width = 600
    $status.Anchor = "Bottom, Left"
    $form.Controls.Add($status)
    $script:UiStatus = $status

    $logPathLbl = New-Object System.Windows.Forms.LinkLabel
    $logPathLbl.Text = $script:LogPath
    $logPathLbl.Left = 310
    $logPathLbl.Top = 620
    $logPathLbl.Width = 760
    $logPathLbl.Add_LinkClicked({
        if ($script:LogPath -and (Test-Path $script:LogPath)) {
            Start-Process notepad.exe $script:LogPath
        }
    })
    $form.Controls.Add($logPathLbl)

    $script:GuideStepsByBranch = Get-HeimdallGuideBranches
    $guideList.Add_SelectedIndexChanged({
        Show-GuideStepDetail -Index $script:UiGuideList.SelectedIndex
    })
    $radioClient.Add_CheckedChanged({
        if ($radioClient.Checked) { Show-GuideBranch -Branch Client }
    })
    $radioServer.Add_CheckedChanged({
        if ($radioServer.Checked) { Show-GuideBranch -Branch Server }
    })

    function Update-LaunchControlLeftColumnLayout {
        $servicesTop = $form.ClientSize.Height - 188
        $servicesGroup.Top = $servicesTop
        $btnPanel.Height = [Math]::Max(80, $servicesTop - $btnPanel.Top - 8)
    }

    # Layout resize
    $form.Add_Resize({
        $rightWidth = $form.ClientSize.Width - 330
        $half = [Math]::Max(200, [int](($rightWidth - 20) / 2))
        $guideList.Width = $half
        $guideDetail.Left = 310 + $half + 10
        $guideDetail.Width = [Math]::Max(200, $rightWidth - $half - 10)
        $guideDetailLabel.Left = $guideDetail.Left
        $steps.Width = $rightWidth
        $logBox.Width = $rightWidth
        $logBox.Height = [Math]::Max(100, $form.ClientSize.Height - 520)
        Update-LaunchControlLeftColumnLayout
        $status.Top = $form.ClientSize.Height - 36
        $logPathLbl.Top = $form.ClientSize.Height - 48
        $logPathLbl.Width = $rightWidth
    })

    Write-HeimdallLog "Setup UI ready. PackedLayout=$($script:IsPackedLayout) Admin=$(Test-IsAdministrator)" -Level OK
    Update-HeimdallServiceStatusUi
    $form.Add_Shown({
        Update-HeimdallServiceStatusUi
    })
    Show-GuideBranch -Branch Client
    if ($script:IsPackedLayout) {
        Write-HeimdallLog "Client pack mode: use Install agent (opens Install.lnk wizard)." -Level INFO
        Set-UiSteps @(
            "Packed folder ready.",
            "Use Client install steps on the right.",
            "Or click: Install agent on this PC"
        )
        $radioServer.Enabled = $false
        $radioServer.Text = "2. Server install (need full repo)"
    }
    else {
        Set-UiSteps @(
            "Select Client or Server branch above.",
            "Click a step for full instructions.",
            "Use left buttons to run each action."
        )
    }

    if ($btnApi) { $btnApi.Add_Click({ Invoke-LaunchControlAction { Start-GuidedApiInstall } }) }
    if ($btnPack) { $btnPack.Add_Click({ Invoke-LaunchControlAction { Start-GuidedPack -OfferInstallAfter } }) }
    $btnPush.Add_Click({ Invoke-LaunchControlAction { Push-ClientPackToMachine } })
    $btnAgent.Add_Click({ Invoke-LaunchControlAction { Start-GuidedCollectorInstall } })
    $btnClientCheck.Add_Click({ Invoke-LaunchControlAction { Start-ClientHealthCheck } })
    $btnLogs.Add_Click({ Open-LogsFolder })
    $btnRemoteLogs.Add_Click({ Open-RemoteLogsFolder })
    $btnBackupDb.Add_Click({ Invoke-LaunchControlAction { Backup-ApiDatabase } })
    if ($btnRemoveDemos) { $btnRemoveDemos.Add_Click({ Invoke-LaunchControlAction { Invoke-RemoveSeedDemoMachines } }) }
    if ($btnDiag) { $btnDiag.Add_Click({ Invoke-LaunchControlAction { Start-Diagnostics } }) }
    $btnDash.Add_Click({
        $u = Show-InputForm -Title "Open dashboard" -Prompt "API base URL" -Fields ([ordered]@{ ApiUrl = "http://localhost:5080" }) -AcceptLabel "Open"
        if ($u -and $u.ApiUrl) { Start-Process $u.ApiUrl.TrimEnd("/") }
    })
    $btnPre.Add_Click({
        Invoke-LaunchControlAction {
            $scenario = if ($script:IsPackedLayout) { "Collector" } else {
                $choice = [System.Windows.Forms.MessageBox]::Show(
                    "Yes = Agent install prerequisites`r`nNo = Create-pack / API prerequisites",
                    "Which check?",
                    [System.Windows.Forms.MessageBoxButtons]::YesNoCancel,
                    [System.Windows.Forms.MessageBoxIcon]::Question)
                if ($choice -eq [System.Windows.Forms.DialogResult]::Cancel) { return }
                if ($choice -eq [System.Windows.Forms.DialogResult]::Yes) { "Collector" } else { "Pack" }
            }
            $r = Invoke-PrerequisiteCheck -Scenario $scenario
            $text = if ($r.Ok) { "Prerequisites OK for $scenario." } else { "FAILED:`r`n- " + ($r.Issues -join "`r`n- ") }
            if ($r.Notes.Count) { $text += "`r`n`r`nNotes:`r`n- " + ($r.Notes -join "`r`n- ") }
            [System.Windows.Forms.MessageBox]::Show($text + "`r`n`r`nLog: $($script:LogPath)", "Prerequisites", "OK", $(if ($r.Ok) { "Information" } else { "Warning" })) | Out-Null
        }
    })

    # Direct mode shortcuts
    switch ($Mode) {
        "InstallApi"       { $form.Add_Shown({ Start-GuidedApiInstall }) }
        "PackCollector"    { $form.Add_Shown({ Start-GuidedPack -OfferInstallAfter }) }
        "InstallCollector" { $form.Add_Shown({ Start-GuidedCollectorInstall }) }
        "PushClientPack"   { $form.Add_Shown({ Push-ClientPackToMachine }) }
        "ClientCheck"      { $form.Add_Shown({ Start-ClientHealthCheck }) }
        "OpenLogs"         { $form.Add_Shown({ Open-LogsFolder }) }
        "OpenRemoteLogs"   { $form.Add_Shown({ Open-RemoteLogsFolder }) }
        "BackupApiDatabase" { $form.Add_Shown({ Backup-ApiDatabase }) }
        "RemoveSeedDemos"  { $form.Add_Shown({ Invoke-RemoveSeedDemoMachines }) }
        "Diagnostics"      { $form.Add_Shown({ Start-Diagnostics }) }
    }

    Update-LaunchControlLeftColumnLayout

    [void]$form.ShowDialog()
    Write-HeimdallLog "Setup closed." -Level INFO
}

try {
    Show-LaunchControl
}
catch {
    $msg = $_.Exception.Message
    if ($script:LogPath) {
        Add-Content -Path $script:LogPath -Value "[ERROR] $msg" -Encoding UTF8
        Add-Content -Path $script:LogPath -Value $_.ScriptStackTrace -Encoding UTF8
    }
    [System.Windows.Forms.MessageBox]::Show(
        "Heimdall Setup crashed:`r`n$msg`r`n`r`nLog: $($script:LogPath)",
        "Heimdall Setup",
        "OK",
        "Error") | Out-Null
    Write-Host "ERROR: $msg" -ForegroundColor Red
    Write-Host "Log: $($script:LogPath)"
    Write-Host "Press Enter to close..."
    [void][Console]::ReadLine()
    exit 1
}
