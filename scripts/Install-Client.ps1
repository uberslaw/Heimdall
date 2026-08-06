#Requires -Version 5.1
<#
.SYNOPSIS
  Heimdall client-only install wizard (workstation collector).

.DESCRIPTION
  Single-purpose guided install for the packed Heimdall-Client folder.
  Launched by Install.cmd / Install.lnk on target PCs. ASCII-only; PS 5.1; UTF-8 BOM.
#>

$ErrorActionPreference = "Stop"
Add-Type -AssemblyName System.Windows.Forms
Add-Type -AssemblyName System.Drawing
[System.Windows.Forms.Application]::EnableVisualStyles()

$script:ScriptDir = Split-Path -Parent $MyInvocation.MyCommand.Path
$versionHelperPath = Join-Path $script:ScriptDir "Heimdall-VersionCompare.ps1"
if (Test-Path -LiteralPath $versionHelperPath) {
    . $versionHelperPath
}
$collectorHelperPath = Join-Path $script:ScriptDir "Heimdall-CollectorInstall.ps1"
if (Test-Path -LiteralPath $collectorHelperPath) {
    . $collectorHelperPath
}
Import-HeimdallVersionCompare -ScriptDir $script:ScriptDir

$script:LogRoot = Join-Path $env:ProgramData "Heimdall\logs"
$script:DataRoot = Join-Path $env:ProgramData "Heimdall"
$script:LastInstallSettingsFile = Join-Path $env:LOCALAPPDATA "Heimdall\last-install-settings.json"
$script:AgentInstallDir = Join-Path ${env:ProgramFiles} "Heimdall\Agent"
# Track auto-bumped pack productVersion from VERSION.json (never hardcode 2/3/…).
$script:ProductVersionExpected = Resolve-HeimdallProductVersionExpected -ScriptDir $script:ScriptDir -Fallback "1"
$script:LogPath = $null
$script:UiLogBox = $null
$script:UiStatus = $null
$script:UiSteps = $null
$script:UiContent = $null
$script:UiNextBtn = $null
$script:CurrentStep = 0
$script:StepCount = 6
$script:Busy = $false
$script:InstallSucceeded = $false

$script:WizardData = @{
    ApiUrl       = ""
    ApiKey       = "heimdall-poc-key"
    MachineGroup = "POC"
    PrereqOk     = $false
    TestOk       = $false
    VerifyOk     = $false
}

$script:StepLabels = @(
    "1. Prerequisites",
    "2. Connection settings",
    "3. Test connection",
    "4. Install",
    "5. Verify",
    "6. Done"
)

# ---------------------------------------------------------------------------
# Logging
# ---------------------------------------------------------------------------

function Initialize-InstallLogging {
    if (-not (Test-Path $script:LogRoot)) {
        New-Item -ItemType Directory -Path $script:LogRoot -Force | Out-Null
    }
    if (-not (Test-Path $script:DataRoot)) {
        New-Item -ItemType Directory -Path $script:DataRoot -Force | Out-Null
    }
    $stamp = Get-Date -Format "yyyyMMdd-HHmmss"
    $script:LogPath = Join-Path $script:LogRoot "install-client-$stamp.log"
    $header = @"

================================================================
  Heimdall Install (client wizard)
================================================================
Log: $($script:LogPath)
User: $env:USERNAME | Machine: $env:COMPUTERNAME
ScriptDir: $($script:ScriptDir)
Started: $(Get-Date -Format "yyyy-MM-dd HH:mm:ss")

"@
    Add-Content -Path $script:LogPath -Value $header -Encoding UTF8
}

function Write-InstallLog {
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
}

function Set-UiStatus {
    param([string]$Text)
    if ($script:UiStatus -and -not $script:UiStatus.IsDisposed) {
        $script:UiStatus.Text = $Text
        [System.Windows.Forms.Application]::DoEvents()
    }
}

function Set-StepMarker {
    param(
        [int]$Index,
        [ValidateSet("", "OK", "WARN", "FAIL", "CURRENT")]
        [string]$State = ""
    )
    if (-not $script:UiSteps -or $script:UiSteps.IsDisposed) { return }
    if ($Index -lt 0 -or $Index -ge $script:UiSteps.Items.Count) { return }
    $label = $script:StepLabels[$Index]
    $prefix = switch ($State) {
        "OK"      { "[OK] " }
        "WARN"    { "[!] " }
        "FAIL"    { "[X] " }
        "CURRENT" { "[>] " }
        default   { "[ ] " }
    }
    $script:UiSteps.Items[$Index] = "$prefix$label"
    [System.Windows.Forms.Application]::DoEvents()
}

function Clear-StepCurrentMarkers {
    for ($i = 0; $i -lt $script:StepCount; $i++) {
        $item = [string]$script:UiSteps.Items[$i]
        if ($item -match '^\[>\] ') {
            $rest = $item -replace '^\[[^\]]+\] ', ''
            $script:UiSteps.Items[$i] = "[ ] $rest"
        }
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

function Get-LastInstallSettings {
    if (-not (Test-Path $script:LastInstallSettingsFile)) { return $null }
    try {
        $raw = Get-Content -Raw -Path $script:LastInstallSettingsFile -Encoding UTF8
        if ([string]::IsNullOrWhiteSpace($raw)) { return $null }
        return ($raw | ConvertFrom-Json)
    }
    catch {
        Write-InstallLog "Could not read last install settings: $($_.Exception.Message)" -Level WARN
        return $null
    }
}

function Save-LastInstallSettings {
    param(
        [Parameter(Mandatory)][string]$ApiUrl,
        [Parameter(Mandatory)][string]$MachineGroup
    )
    $dir = Split-Path -Parent $script:LastInstallSettingsFile
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
    [System.IO.File]::WriteAllText($script:LastInstallSettingsFile, $json, $utf8Bom)
    Write-InstallLog "Saved settings for future installs: $($entry.apiUrl)" -Level INFO
}

function Get-DefaultApiUrlForWizard {
    return Resolve-HeimdallDefaultCollectorApiUrl -LastInstallSettingsFile $script:LastInstallSettingsFile -Log {
        param([string]$Message, [string]$Level)
        Write-InstallLog $Message -Level $Level
    }
}

function Get-DefaultMachineGroup {
    $last = Get-LastInstallSettings
    if ($last -and $last.machineGroup) {
        return [string]$last.machineGroup
    }
    return "POC"
}

function Get-PayloadPath {
    $candidates = @(
        (Join-Path $script:ScriptDir "payload"),
        (Join-Path $script:ScriptDir "..\dist\Heimdall-Client\payload"),
        (Join-Path $script:ScriptDir "..\dist\workstation-collector\payload")
    )
    foreach ($c in $candidates) {
        $exe = Join-Path $c "Heimdall.Agent.exe"
        if (Test-Path $exe) { return (Resolve-HeimdallFilesystemPath -Path $c) }
    }
    return $null
}

function Get-InstallerCmdPath {
    $names = @(
        (Join-Path $script:ScriptDir "Install-WorkstationCollector.cmd")
    )
    foreach ($n in $names) {
        if (Test-Path $n) { return (Resolve-HeimdallFilesystemPath -Path $n) }
    }
    return $null
}

function Read-LocalPackVersion {
    $candidates = @(
        (Join-Path $script:ScriptDir "VERSION.json"),
        (Join-Path $script:ScriptDir "PACKED.txt")
    )
    foreach ($c in $candidates) {
        if (-not (Test-Path $c)) { continue }
        if ($c -like "*.json") {
            try {
                return Get-Content -Raw -Path $c | ConvertFrom-Json
            }
            catch {
                Write-InstallLog "Could not parse VERSION.json: $($_.Exception.Message)" -Level WARN
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

function Read-AgentAppSettingsFromDisk {
    $path = Join-Path $script:AgentInstallDir "appsettings.json"
    if (-not (Test-Path $path)) {
        return [pscustomobject]@{
            Ok           = $false
            Path         = $path
            Error        = "appsettings.json not found"
            ApiBaseUrl   = $null
            ApiKey       = $null
            MachineGroup = $null
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
        }
    }
    catch {
        return [pscustomobject]@{
            Ok           = $false
            Path         = $path
            Error        = $_.Exception.Message
            ApiBaseUrl   = $null
            ApiKey       = $null
            MachineGroup = $null
        }
    }
}

function Open-LogsFolder {
    if (-not (Test-Path $script:LogRoot)) {
        New-Item -ItemType Directory -Path $script:LogRoot -Force | Out-Null
    }
    Start-Process explorer.exe $script:LogRoot
    Write-InstallLog "Opened logs folder: $($script:LogRoot)" -Level OK
}

function Set-WizardBusy {
    param([bool]$Busy)
    $script:Busy = $Busy
    if ($script:UiNextBtn -and -not $script:UiNextBtn.IsDisposed) {
        $script:UiNextBtn.Enabled = -not $Busy
    }
    [System.Windows.Forms.Application]::DoEvents()
}

function Wait-ProcessWithUiPump {
    param([System.Diagnostics.Process]$Process)
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

# ---------------------------------------------------------------------------
# Step actions
# ---------------------------------------------------------------------------

function Invoke-StepPrerequisites {
    Write-InstallLog "Checking prerequisites..." -Level STEP
    Set-UiStatus "Checking prerequisites..."
    $issues = New-Object System.Collections.Generic.List[string]

    if (Test-IsAdministrator) {
        Write-InstallLog "Administrator: yes" -Level OK
    }
    else {
        $issues.Add("Not running as Administrator (required for Windows service install).")
        Write-InstallLog "Administrator: NO" -Level ERROR
    }

    $payload = Get-PayloadPath
    if ($payload) {
        Write-InstallLog "Payload found: $payload" -Level OK
        $ver = Read-LocalPackVersion
        if ($ver -and $ver.productVersion) {
            Write-InstallLog "Pack productVersion: $($ver.productVersion)" -Level INFO
        }
    }
    else {
        $issues.Add("payload\Heimdall.Agent.exe not found. Copy the whole dist\Heimdall-Client folder from the build PC.")
        Write-InstallLog "Payload: MISSING" -Level ERROR
    }

    $installer = Get-InstallerCmdPath
    if ($installer) {
        Write-InstallLog "Installer script: $installer" -Level OK
    }
    else {
        $issues.Add("Install-WorkstationCollector.cmd not found beside this wizard.")
        Write-InstallLog "Installer CMD: MISSING" -Level ERROR
    }

    if ($issues.Count -eq 0) {
        $script:WizardData.PrereqOk = $true
        Set-StepMarker -Index 0 -State "OK"
        Set-UiStatus "Prerequisites passed"
        Write-InstallLog "Prerequisites OK" -Level OK
        return $true
    }

    $script:WizardData.PrereqOk = $false
    Set-StepMarker -Index 0 -State "FAIL"
    Set-UiStatus "Prerequisites failed - see log"
    foreach ($i in $issues) { Write-InstallLog $i -Level ERROR }
    [System.Windows.Forms.MessageBox]::Show(
        ("Cannot install:`r`n`r`n- " + ($issues -join "`r`n- ") + "`r`n`r`nLog:`r`n$($script:LogPath)"),
        "Prerequisites failed",
        "OK",
        "Error") | Out-Null
    return $false
}

function Invoke-StepTestConnection {
    Write-InstallLog "Testing connection to API..." -Level STEP
    Set-UiStatus "Testing API connection..."
    $apiUrl = $script:WizardData.ApiUrl
    $apiKey = $script:WizardData.ApiKey

    $health = Test-ApiHealth -ApiUrl $apiUrl
    $localVer = Read-LocalPackVersion
    $localPv = if ($localVer -and $localVer.productVersion) { $localVer.productVersion } else { $script:ProductVersionExpected }

    if ($health.Ok) {
        $apiPv = [string]$health.Payload.productVersion
        $mn = [string]$health.Payload.machineName
        # Pack vs ProductVersionExpected only — never compare to API health SemVer ($apiPv).
        $expectedPv = if ($localVer -and $localVer.productVersion) {
            [string]$localVer.productVersion
        }
        else {
            $script:ProductVersionExpected
        }
        Write-InstallLog "GET $($health.Uri) OK - server=$mn apiProductVersion=$apiPv (API SemVer; independent of client pack)" -Level OK
        $versionOk = Test-HeimdallProductVersionAccept -LocalVersion $localPv -ExpectedClientVersion $expectedPv -Log {
            param([string]$Message, [string]$Level)
            Write-InstallLog $Message -Level $Level
        } -ConfirmMismatch {
            param([string]$PackPv, [string]$ExpectedPv)
            $r = [System.Windows.Forms.MessageBox]::Show(
                "API is reachable, but this pack's client version differs from the expected client version.`r`n`r`nPack:     $PackPv`r`nExpected: $ExpectedPv`r`n`r`n(API SemVer is not compared.)`r`n`r`nInstall anyway?",
                "Version mismatch",
                [System.Windows.Forms.MessageBoxButtons]::YesNo,
                [System.Windows.Forms.MessageBoxIcon]::Warning)
            return ($r -eq [System.Windows.Forms.DialogResult]::Yes)
        }
        if (-not $versionOk) {
            $script:WizardData.TestOk = $false
            return $false
        }
        if (Test-HeimdallProductVersionMatch -VersionA $localPv -VersionB $expectedPv) {
            Set-StepMarker -Index 2 -State "OK"
        }
        else {
            Set-StepMarker -Index 2 -State "WARN"
        }

        $auth = Test-ApiConfigAuth -ApiUrl $apiUrl -ApiKey $apiKey
        if ($auth.Ok) {
            Write-InstallLog "API key accepted by /api/config" -Level OK
        }
        else {
            Write-InstallLog "API key check failed (HTTP $($auth.Status)): $($auth.Error)" -Level WARN
            $r = [System.Windows.Forms.MessageBox]::Show(
                "Health OK but API key was rejected (HTTP $($auth.Status)).`r`nKeys must match the server.`r`n`r`nContinue anyway?",
                "API key check",
                [System.Windows.Forms.MessageBoxButtons]::YesNo,
                [System.Windows.Forms.MessageBoxIcon]::Warning)
            if ($r -ne [System.Windows.Forms.DialogResult]::Yes) {
                $script:WizardData.TestOk = $false
                return $false
            }
            Set-StepMarker -Index 2 -State "WARN"
        }
    }
    else {
        Write-InstallLog "API not reachable: $($health.Error)" -Level WARN
        Write-InstallLog "URI: $($health.Uri)" -Level INFO
        $r = [System.Windows.Forms.MessageBox]::Show(
            "Cannot reach:`r`n$($health.Uri)`r`n$($health.Error)`r`n`r`nInstall can continue (agent queues offline), but heartbeats fail until URL/firewall is fixed.`r`n`r`nContinue?",
            "API unreachable",
            [System.Windows.Forms.MessageBoxButtons]::YesNo,
            [System.Windows.Forms.MessageBoxIcon]::Warning)
        if ($r -ne [System.Windows.Forms.DialogResult]::Yes) {
            $script:WizardData.TestOk = $false
            Set-StepMarker -Index 2 -State "FAIL"
            return $false
        }
        Set-StepMarker -Index 2 -State "WARN"
    }

    $script:WizardData.TestOk = $true
    Set-UiStatus "Connection test complete"
    return $true
}

function Invoke-StepInstall {
    Write-InstallLog "Starting service install..." -Level STEP
    Set-UiStatus "Installing HeimdallAgent service..."
    Set-StepMarker -Index 3 -State "CURRENT"

    $installer = Get-InstallerCmdPath
    $payload = Get-PayloadPath
    if (-not $installer -or -not $payload) {
        Write-InstallLog "Installer or payload missing." -Level ERROR
        Set-StepMarker -Index 3 -State "FAIL"
        return $false
    }

    Write-InstallLog "Running elevated installer..." -Level INFO
    Write-InstallLog "Installer: $installer" -Level INFO
    Write-InstallLog "Payload: $payload" -Level INFO
    $exit = Invoke-HeimdallElevatedCollectorInstall `
        -InstallerCmdPath $installer `
        -ApiUrl (Normalize-ApiUrl $script:WizardData.ApiUrl) `
        -ApiKey $script:WizardData.ApiKey `
        -MachineGroup $script:WizardData.MachineGroup `
        -PayloadPath $payload `
        -AlreadyElevated:(Test-IsAdministrator) `
        -PumpUi { [System.Windows.Forms.Application]::DoEvents() } `
        -Log { param($m, $l) Write-InstallLog $m -Level $l }
    Write-InstallLog "Installer exit code: $exit" -Level $(if ($exit -eq 0) { "OK" } else { "ERROR" })

    if ($exit -ne 0) {
        $installLog = Get-HeimdallInstallAgentLogTail -LineCount 30 -LogRoot $script:LogRoot
        if ($installLog) {
            Write-InstallLog "Latest service install log: $($installLog.Path)" -Level INFO
            foreach ($line in $installLog.Lines) {
                Write-InstallLog "  install> $line" -Level INFO
            }
        }
        else {
            Write-InstallLog "No install-agent-*.log yet — see install> lines above from service install console capture" -Level WARN
        }
    }

    if ($exit -eq 0) {
        Set-StepMarker -Index 3 -State "OK"
        Set-UiStatus "Install finished"
        return $true
    }

    Set-StepMarker -Index 3 -State "FAIL"
    Set-UiStatus "Install failed - see log"
    $installTail = ""
    $installLog = Get-HeimdallInstallAgentLogTail -LineCount 15 -LogRoot $script:LogRoot
    if ($installLog) {
        $installTail = "`r`n`r`nService install log ($($installLog.Path)):`r`n$($installLog.Text)"
    }
    [System.Windows.Forms.MessageBox]::Show(
        "Install did not complete successfully (exit $exit).$installTail`r`n`r`nLogs under:`r`n$($script:LogRoot)`r`n  install-client-*.log (this wizard)`r`n  install-agent-*.log (service install)",
        "Install failed",
        "OK",
        "Error") | Out-Null
    return $false
}

function Invoke-StepVerify {
    Write-InstallLog "Post-install verification..." -Level STEP
    Set-UiStatus "Verifying install..."
    Set-StepMarker -Index 4 -State "CURRENT"

    $svc = Get-Service -Name HeimdallAgent -ErrorAction SilentlyContinue
    $svcOk = $svc -and $svc.Status -eq "Running"
    $exeOk = Test-Path (Join-Path $script:AgentInstallDir "Heimdall.Agent.exe")
    $diskSettings = Read-AgentAppSettingsFromDisk
    $settingsOk = $diskSettings.Ok
    $expectedUrl = Normalize-ApiUrl $script:WizardData.ApiUrl
    $diskUrl = if ($diskSettings.ApiBaseUrl) { Normalize-ApiUrl $diskSettings.ApiBaseUrl } else { "" }
    $urlMatchOk = $settingsOk -and ($diskUrl -eq $expectedUrl)

    if ($svcOk) {
        Write-InstallLog "HeimdallAgent service: Running" -Level OK
    }
    else {
        Write-InstallLog "HeimdallAgent service: NOT running (status=$($svc.Status))" -Level ERROR
    }

    if ($settingsOk) {
        Write-InstallLog "appsettings.json: $($diskSettings.Path)" -Level OK
        Write-InstallLog "ApiBaseUrl on disk: $diskUrl" -Level INFO
    }
    else {
        Write-InstallLog "appsettings.json read failed: $($diskSettings.Error)" -Level ERROR
    }

    if ($settingsOk -and -not $urlMatchOk) {
        Write-InstallLog "ApiBaseUrl MISMATCH: expected='$expectedUrl' actual='$diskUrl'" -Level ERROR
    }
    elseif ($urlMatchOk) {
        Write-InstallLog "ApiBaseUrl on disk matches install input." -Level OK
    }

    $health = Test-ApiHealth -ApiUrl $script:WizardData.ApiUrl
    if ($health.Ok) {
        Write-InstallLog "API health after install: OK" -Level OK
    }
    else {
        Write-InstallLog "API health after install: FAILED ($($health.Error))" -Level WARN
    }

    $verifyOk = $svcOk -and $exeOk -and $settingsOk -and $urlMatchOk
    $script:WizardData.VerifyOk = $verifyOk

    if ($verifyOk) {
        Save-LastInstallSettings -ApiUrl $script:WizardData.ApiUrl -MachineGroup $script:WizardData.MachineGroup
        Set-StepMarker -Index 4 -State "OK"
        Set-UiStatus "Verification passed"
        Write-InstallLog "Verification PASSED" -Level OK
        $script:InstallSucceeded = $true
        return $true
    }

    Set-StepMarker -Index 4 -State "FAIL"
    Set-UiStatus "Verification failed - see log"
    Write-InstallLog "Verification FAILED" -Level ERROR
    return $false
}

# ---------------------------------------------------------------------------
# UI: step content panels
# ---------------------------------------------------------------------------

function Show-StepContent {
    param([int]$StepIndex)
    if (-not $script:UiContent) { return }
    $script:UiContent.Controls.Clear()

    $title = New-Object System.Windows.Forms.Label
    $title.Font = New-Object System.Drawing.Font("Segoe UI Semibold", 11)
    $title.Left = 8
    $title.Top = 8
    $title.Width = 520
    $title.Height = 28
    $script:UiContent.Controls.Add($title)

    $body = New-Object System.Windows.Forms.Label
    $body.Left = 8
    $body.Top = 40
    $body.Width = 520
    $body.Height = 200
    $script:UiContent.Controls.Add($body)

    switch ($StepIndex) {
        0 {
            $title.Text = "Step 1: Prerequisites"
            $body.Text = @"
Checks run automatically when you open this step.

Required:
- Run as Administrator (Install.cmd elevates for you)
- payload\Heimdall.Agent.exe present in this folder
- Install-WorkstationCollector.cmd present

See the progress log below for pass/fail details.
"@
        }
        1 {
            $title.Text = "Step 2: Connection settings"
            $body.Text = "Enter the Heimdall API this PC should report to.`r`n`r`nIMPORTANT: Do NOT use localhost unless the API runs on THIS machine.`r`nUse your server hostname or IP, e.g. http://YOUR-SERVER:5080"
            $body.Height = 56

            $flUrl = New-Object System.Windows.Forms.Label
            $flUrl.Text = "ApiUrl"
            $flUrl.Left = 8
            $flUrl.Top = 104
            $flUrl.Width = 110
            $script:UiContent.Controls.Add($flUrl)
            $boxUrl = New-Object System.Windows.Forms.TextBox
            $boxUrl.Left = 120
            $boxUrl.Top = 102
            $boxUrl.Width = 400
            $boxUrl.Text = $script:WizardData.ApiUrl
            $script:UiContent.Controls.Add($boxUrl)

            $flKey = New-Object System.Windows.Forms.Label
            $flKey.Text = "ApiKey"
            $flKey.Left = 8
            $flKey.Top = 140
            $flKey.Width = 110
            $script:UiContent.Controls.Add($flKey)
            $boxKey = New-Object System.Windows.Forms.TextBox
            $boxKey.Left = 120
            $boxKey.Top = 138
            $boxKey.Width = 400
            $boxKey.Text = $script:WizardData.ApiKey
            $script:UiContent.Controls.Add($boxKey)

            $flGroup = New-Object System.Windows.Forms.Label
            $flGroup.Text = "MachineGroup"
            $flGroup.Left = 8
            $flGroup.Top = 176
            $flGroup.Width = 110
            $script:UiContent.Controls.Add($flGroup)
            $boxGroup = New-Object System.Windows.Forms.TextBox
            $boxGroup.Left = 120
            $boxGroup.Top = 174
            $boxGroup.Width = 400
            $boxGroup.Text = $script:WizardData.MachineGroup
            $script:UiContent.Controls.Add($boxGroup)

            $warn = New-Object System.Windows.Forms.Label
            $warn.ForeColor = [System.Drawing.Color]::DarkRed
            $warn.Text = "Avoid localhost on remote PCs - use the API server hostname or IP."
            $warn.Left = 8
            $warn.Top = 212
            $warn.Width = 520
            $warn.Height = 32
            $script:UiContent.Controls.Add($warn)

            $script:UiContent.Tag = @{
                ApiUrl       = $boxUrl
                ApiKey       = $boxKey
                MachineGroup = $boxGroup
            }
        }
        2 {
            $title.Text = "Step 3: Test connection"
            $body.Text = @"
Probes the API before install:

- GET /api/health (reachability; API SemVer is informational only)
- Client pack version vs expected client version (independent of API)
- GET /api/config with your API key

Click Next to run tests. Results appear in the log below.
"@
        }
        3 {
            $title.Text = "Step 4: Install"
            $body.Text = @"
Installs the HeimdallAgent Windows service:

- Copies payload to Program Files\Heimdall\Agent
- Writes appsettings.json (ApiBaseUrl, ApiKey, MachineGroup)
- Creates and starts HeimdallAgent service

Click Install to begin. An elevated console may flash briefly.
"@
        }
        4 {
            $title.Text = "Step 5: Verify"
            $body.Text = @"
Checks after install:

- HeimdallAgent service running
- appsettings.json ApiBaseUrl matches what you entered
- API health reachable

Runs automatically when you reach this step.
"@
        }
        5 {
            $title.Text = "Step 6: Done"
            if ($script:InstallSucceeded) {
                $body.Text = @"
Install complete.

Hostname $env:COMPUTERNAME should appear on the dashboard Machines page within about 1-2 minutes after the first heartbeat.

Logs:
$($script:LogRoot)

Click Finish to close this wizard.
"@
            }
            else {
                $body.Text = @"
Install did not fully succeed.

Review the progress log and files under:
$($script:LogRoot)

Use Open logs to browse install-*.log files.
"@
            }
        }
    }
}

function Read-ConnectionFieldsFromUi {
    $tag = $script:UiContent.Tag
    if (-not $tag) { return $true }
    if ($tag.ApiUrl) { $script:WizardData.ApiUrl = Normalize-ApiUrl $tag.ApiUrl.Text }
    if ($tag.ApiKey) { $script:WizardData.ApiKey = $tag.ApiKey.Text.Trim() }
    if ($tag.MachineGroup) { $script:WizardData.MachineGroup = $tag.MachineGroup.Text.Trim() }
    return $true
}

function Update-NextButtonLabel {
    if (-not $script:UiNextBtn) { return }
    switch ($script:CurrentStep) {
        0 { $script:UiNextBtn.Text = "Next" }
        1 { $script:UiNextBtn.Text = "Next" }
        2 { $script:UiNextBtn.Text = "Next" }
        3 { $script:UiNextBtn.Text = "Install" }
        4 { $script:UiNextBtn.Text = "Next" }
        5 { $script:UiNextBtn.Text = "Finish" }
        default { $script:UiNextBtn.Text = "Next" }
    }
}

function Go-ToStep {
    param([int]$StepIndex)
    if ($StepIndex -lt 0 -or $StepIndex -ge $script:StepCount) { return }

    Clear-StepCurrentMarkers
    $script:CurrentStep = $StepIndex
    Set-StepMarker -Index $StepIndex -State "CURRENT"
    Show-StepContent -StepIndex $StepIndex
    Update-NextButtonLabel
    Set-UiStatus "Step $($StepIndex + 1) of $($script:StepCount)"
}

function Invoke-NextStep {
    if ($script:Busy) { return }

    switch ($script:CurrentStep) {
        0 {
            if (-not $script:WizardData.PrereqOk) {
                if (-not (Invoke-StepPrerequisites)) { return }
            }
            Go-ToStep -StepIndex 1
        }
        1 {
            Read-ConnectionFieldsFromUi | Out-Null
            if ([string]::IsNullOrWhiteSpace($script:WizardData.ApiUrl)) {
                [System.Windows.Forms.MessageBox]::Show(
                    "ApiUrl is required.`r`n`r`nEnter the Heimdall API server URL, e.g. http://YOUR-SERVER:5080",
                    "Missing ApiUrl",
                    "OK",
                    "Warning") | Out-Null
                return
            }
            if (Test-ApiUrlLooksLocalhost -ApiUrl $script:WizardData.ApiUrl) {
                $r = [System.Windows.Forms.MessageBox]::Show(
                    "ApiUrl is localhost. That only works if the Heimdall API is on THIS PC.`r`n`r`nRemote collectors must use the API server hostname or IP.`r`n`r`nContinue anyway?",
                    "Localhost warning",
                    [System.Windows.Forms.MessageBoxButtons]::YesNo,
                    [System.Windows.Forms.MessageBoxIcon]::Warning)
                if ($r -ne [System.Windows.Forms.DialogResult]::Yes) { return }
            }
            Set-StepMarker -Index 1 -State "OK"
            Write-InstallLog "Settings: ApiUrl=$($script:WizardData.ApiUrl) Group=$($script:WizardData.MachineGroup)" -Level INFO
            Go-ToStep -StepIndex 2
        }
        2 {
            Set-WizardBusy -Busy $true
            try {
                $ok = Invoke-StepTestConnection
                if ($ok) { Go-ToStep -StepIndex 3 }
            }
            finally {
                Set-WizardBusy -Busy $false
            }
        }
        3 {
            Set-WizardBusy -Busy $true
            try {
                $ok = Invoke-StepInstall
                if ($ok) {
                    Go-ToStep -StepIndex 4
                    Invoke-StepVerify | Out-Null
                    Go-ToStep -StepIndex 5
                }
            }
            finally {
                Set-WizardBusy -Busy $false
            }
        }
        4 {
            Set-WizardBusy -Busy $true
            try {
                Invoke-StepVerify | Out-Null
                Go-ToStep -StepIndex 5
            }
            finally {
                Set-WizardBusy -Busy $false
            }
        }
        5 {
            return
        }
    }
}

function Invoke-BackStep {
    if ($script:Busy) { return }
    if ($script:CurrentStep -le 0) { return }
    if ($script:CurrentStep -ge 5) { return }
    Go-ToStep -StepIndex ($script:CurrentStep - 1)
}

# ---------------------------------------------------------------------------
# Main form
# ---------------------------------------------------------------------------

function Show-InstallWizard {
    Initialize-InstallLogging | Out-Null

    $script:WizardData.ApiUrl = Get-DefaultApiUrlForWizard
    $script:WizardData.MachineGroup = Get-DefaultMachineGroup

    $form = New-Object System.Windows.Forms.Form
    $form.Text = "Heimdall Install"
    $form.Width = 920
    $form.Height = 680
    $form.StartPosition = "CenterScreen"
    $form.MinimumSize = New-Object System.Drawing.Size(820, 600)
    $form.Font = New-Object System.Drawing.Font("Segoe UI", 9)

    $header = New-Object System.Windows.Forms.Label
    $header.Text = "Heimdall Client Install"
    $header.Font = New-Object System.Drawing.Font("Segoe UI Semibold", 14)
    $header.Left = 16
    $header.Top = 12
    $header.Width = 500
    $header.Height = 28
    $form.Controls.Add($header)

    $sub = New-Object System.Windows.Forms.Label
    $sub.Text = "Guided install for this PC. Progress is logged to ProgramData\Heimdall\logs\"
    $sub.Left = 16
    $sub.Top = 42
    $sub.Width = 700
    $sub.Height = 20
    $form.Controls.Add($sub)

    $stepsLabel = New-Object System.Windows.Forms.Label
    $stepsLabel.Text = "Steps"
    $stepsLabel.Left = 16
    $stepsLabel.Top = 72
    $stepsLabel.Width = 200
    $form.Controls.Add($stepsLabel)

    $steps = New-Object System.Windows.Forms.ListBox
    $steps.Left = 16
    $steps.Top = 96
    $steps.Width = 240
    $steps.Height = 180
    $steps.Enabled = $false
    foreach ($l in $script:StepLabels) {
        [void]$steps.Items.Add("[ ] $l")
    }
    $form.Controls.Add($steps)
    $script:UiSteps = $steps

    $content = New-Object System.Windows.Forms.Panel
    $content.Left = 270
    $content.Top = 96
    $content.Width = 540
    $content.Height = 260
    $content.BorderStyle = "FixedSingle"
    $form.Controls.Add($content)
    $script:UiContent = $content

    $logLabel = New-Object System.Windows.Forms.Label
    $logLabel.Text = "Progress log"
    $logLabel.Left = 16
    $logLabel.Top = 368
    $logLabel.Width = 200
    $form.Controls.Add($logLabel)

    $logBox = New-Object System.Windows.Forms.RichTextBox
    $logBox.Left = 16
    $logBox.Top = 392
    $logBox.Width = 860
    $logBox.Height = 200
    $logBox.ReadOnly = $true
    $logBox.Font = New-Object System.Drawing.Font("Consolas", 9)
    $logBox.BackColor = [System.Drawing.Color]::WhiteSmoke
    $form.Controls.Add($logBox)
    $script:UiLogBox = $logBox

    $backBtn = New-Object System.Windows.Forms.Button
    $backBtn.Text = "Back"
    $backBtn.Left = 16
    $backBtn.Top = 608
    $backBtn.Width = 90
    $backBtn.Height = 32
    $form.Controls.Add($backBtn)

    $nextBtn = New-Object System.Windows.Forms.Button
    $nextBtn.Text = "Next"
    $nextBtn.Left = 116
    $nextBtn.Top = 608
    $nextBtn.Width = 110
    $nextBtn.Height = 32
    $form.Controls.Add($nextBtn)
    $script:UiNextBtn = $nextBtn

    $logsBtn = New-Object System.Windows.Forms.Button
    $logsBtn.Text = "Open logs"
    $logsBtn.Left = 236
    $logsBtn.Top = 608
    $logsBtn.Width = 100
    $logsBtn.Height = 32
    $form.Controls.Add($logsBtn)

    $cancelBtn = New-Object System.Windows.Forms.Button
    $cancelBtn.Text = "Cancel"
    $cancelBtn.Left = 786
    $cancelBtn.Top = 608
    $cancelBtn.Width = 90
    $cancelBtn.Height = 32
    $cancelBtn.DialogResult = [System.Windows.Forms.DialogResult]::Cancel
    $form.CancelButton = $cancelBtn
    $form.Controls.Add($cancelBtn)

    $status = New-Object System.Windows.Forms.Label
    $status.Text = "Ready"
    $status.Left = 350
    $status.Top = 614
    $status.Width = 420
    $form.Controls.Add($status)
    $script:UiStatus = $status

    $form.Add_Resize({
        $logBox.Width = [Math]::Max(400, $form.ClientSize.Width - 40)
        $logBox.Height = [Math]::Max(120, $form.ClientSize.Height - 480)
        $cancelBtn.Top = $form.ClientSize.Height - 48
        $backBtn.Top = $form.ClientSize.Height - 48
        $nextBtn.Top = $form.ClientSize.Height - 48
        $logsBtn.Top = $form.ClientSize.Height - 48
        $status.Top = $form.ClientSize.Height - 42
    })

    $backBtn.Add_Click({ Invoke-BackStep })
    $nextBtn.Add_Click({
        if ($script:CurrentStep -eq 5) {
            $form.DialogResult = [System.Windows.Forms.DialogResult]::OK
            $form.Close()
            return
        }
        Invoke-NextStep
    })
    $logsBtn.Add_Click({ Open-LogsFolder })

    $form.Add_Shown({
        Write-InstallLog "Install wizard ready. Admin=$(Test-IsAdministrator)" -Level OK
        Go-ToStep -StepIndex 0
        Set-WizardBusy -Busy $true
        try {
            Invoke-StepPrerequisites | Out-Null
        }
        finally {
            Set-WizardBusy -Busy $false
        }
    })

    $result = $form.ShowDialog()
    Write-InstallLog "Install wizard closed (result=$result success=$script:InstallSucceeded)" -Level INFO

    if (-not $script:InstallSucceeded -and $result -ne [System.Windows.Forms.DialogResult]::OK) {
        return 1
    }
    if (-not $script:InstallSucceeded) {
        return 1
    }
    return 0
}

try {
    $exitCode = Show-InstallWizard
    if ($exitCode -ne 0) {
        Write-Host "Install did not complete successfully." -ForegroundColor Yellow
        Write-Host "Log: $($script:LogPath)"
        Write-Host "Press Enter to close..."
        [void][Console]::ReadLine()
    }
    exit $exitCode
}
catch {
    $msg = $_.Exception.Message
    if ($script:LogPath) {
        Add-Content -Path $script:LogPath -Value "[ERROR] $msg" -Encoding UTF8
        Add-Content -Path $script:LogPath -Value $_.ScriptStackTrace -Encoding UTF8
    }
    [System.Windows.Forms.MessageBox]::Show(
        "Install wizard crashed:`r`n$msg`r`n`r`nLog: $($script:LogPath)",
        "Heimdall Install",
        "OK",
        "Error") | Out-Null
    Write-Host "ERROR: $msg" -ForegroundColor Red
    Write-Host "Log: $($script:LogPath)"
    Write-Host "Press Enter to close..."
    [void][Console]::ReadLine()
    exit 1
}
