#Requires -RunAsAdministrator
<#
.SYNOPSIS
  Verbose Heimdall API + dashboard Windows service installer.
.NOTES
  Prefer scripts\Install-Api.cmd when double-clicking from Explorer (keeps console open).
  Log: %ProgramData%\Heimdall\logs\install-api-*.log
#>
param(
    [string]$InstallDir = "$env:ProgramFiles\Heimdall\Api",
    [int]$Port = 5080,
    [string]$ApiKey = "heimdall-poc-key",
    [switch]$NoPrompt
)

$ErrorActionPreference = "Stop"
$script:LastError = $null
$script:LogPath = $null
$exitCode = 1
$script:InstallStartedAt = $null
$script:InstallTimingEstimate = $null

$timingHelper = Join-Path $PSScriptRoot "Heimdall-InstallApiTiming.ps1"
if (Test-Path -LiteralPath $timingHelper) {
    . $timingHelper
}

$script:InstallProgressTotalSteps = 9

function Write-Log {
    param(
        [string]$Message,
        [ValidateSet("INFO", "WARN", "ERROR", "STEP", "OK")]
        [string]$Level = "INFO"
    )
    $ts = Get-Date -Format "yyyy-MM-dd HH:mm:ss"
    $line = "[$ts] [$Level] $Message"
    if (Get-Command Test-InstallApiProgressActive -ErrorAction SilentlyContinue) {
        if (Test-InstallApiProgressActive) {
            if ($script:LogPath) {
                Add-Content -Path $script:LogPath -Value $line -Encoding UTF8
            }
            if ($Level -in @("STEP", "OK", "ERROR", "WARN") -and (Get-Command Set-InstallApiProgressStatus -ErrorAction SilentlyContinue)) {
                Set-InstallApiProgressStatus -StatusLine $Message
            }
            return
        }
    }
    switch ($Level) {
        "STEP"  { Write-Host $line -ForegroundColor Cyan }
        "OK"    { Write-Host $line -ForegroundColor Green }
        "WARN"  { Write-Host $line -ForegroundColor Yellow }
        "ERROR" { Write-Host $line -ForegroundColor Red }
        default { Write-Host $line }
    }
    if ($script:LogPath) {
        Add-Content -Path $script:LogPath -Value $line -Encoding UTF8
    }
}

function Write-Banner {
    param([string]$Title, [ConsoleColor]$Color = [ConsoleColor]::White)
    $bar = "=" * 64
    if (Get-Command Test-InstallApiProgressActive -ErrorAction SilentlyContinue) {
        if (Test-InstallApiProgressActive) {
            if ($script:LogPath) {
                Add-Content -Path $script:LogPath -Value "`n$bar`n  $Title`n$bar`n" -Encoding UTF8
            }
            if (Get-Command Set-InstallApiProgressStatus -ErrorAction SilentlyContinue) {
                Set-InstallApiProgressStatus -StatusLine $Title
            }
            return
        }
    }
    Write-Host ""
    Write-Host $bar -ForegroundColor $Color
    Write-Host "  $Title" -ForegroundColor $Color
    Write-Host $bar -ForegroundColor $Color
    Write-Host ""
    if ($script:LogPath) {
        Add-Content -Path $script:LogPath -Value "`n$bar`n  $Title`n$bar`n" -Encoding UTF8
    }
}

function Invoke-Logged {
    param(
        [string]$StepName,
        [scriptblock]$Action,
        [int]$ProgressStep = 0,
        [string]$ProgressLabel = $null
    )
    if ($ProgressStep -gt 0 -and (Get-Command Set-InstallApiProgressStep -ErrorAction SilentlyContinue)) {
        $label = if ($ProgressLabel) { $ProgressLabel } else { $StepName }
        Set-InstallApiProgressStep -StepIndex $ProgressStep -TotalSteps $script:InstallProgressTotalSteps -StepName $label
    }
    Write-Log ">>> $StepName" -Level STEP
    try {
        & $Action
        Write-Log "<<< $StepName - done" -Level OK
    }
    catch {
        $script:LastError = $_
        Write-Log "<<< $StepName - FAILED: $($_.Exception.Message)" -Level ERROR
        if ($_.ScriptStackTrace) {
            Write-Log $_.ScriptStackTrace -Level ERROR
        }
        throw
    }
}

function Test-ServicesMmcLikelyOpen {
    return [bool](Get-Process -Name mmc -ErrorAction SilentlyContinue)
}

function Ensure-HeimdallApiFirewallRule {
    param(
        [Parameter(Mandatory = $true)]
        [int]$Port
    )

    $ruleDisplayName = "Heimdall API (port $Port)"
    $ruleInternalName = "HeimdallApi-Inbound-TCP-$Port"
    $portText = [string]$Port

    Write-Log "Ensuring Windows Firewall allows inbound TCP $Port ($ruleDisplayName)..."

    try {
        if (Get-Module -ListAvailable -Name NetSecurity) {
            Import-Module NetSecurity -ErrorAction SilentlyContinue | Out-Null
        }

        if (Get-Command New-NetFirewallRule -ErrorAction SilentlyContinue) {
            $stale = @(Get-NetFirewallRule -ErrorAction SilentlyContinue | Where-Object {
                $_.DisplayName -match '^Heimdall API \(port \d+\)$' -and $_.DisplayName -ne $ruleDisplayName
            })
            foreach ($old in $stale) {
                Write-Log "Removing stale firewall rule: $($old.DisplayName)"
                Remove-NetFirewallRule -Name $old.Name -ErrorAction Stop
            }

            $existing = @(Get-NetFirewallRule -DisplayName $ruleDisplayName -ErrorAction SilentlyContinue)
            if ($existing.Count -gt 0) {
                foreach ($rule in $existing) {
                    $filter = Get-NetFirewallPortFilter -AssociatedNetFirewallRule $rule -ErrorAction SilentlyContinue
                    if ($filter -and ($filter.LocalPort -ne $portText)) {
                        Write-Log "Updating firewall rule local port to $Port..."
                        Set-NetFirewallPortFilter -AssociatedNetFirewallRule $rule -Protocol TCP -LocalPort $Port -ErrorAction Stop
                    }
                    Set-NetFirewallRule -Name $rule.Name -Enabled True -Direction Inbound -Action Allow -ErrorAction Stop
                }
                Write-Log "Firewall rule '$ruleDisplayName' is active (TCP $Port)." -Level OK
            }
            else {
                New-NetFirewallRule `
                    -DisplayName $ruleDisplayName `
                    -Name $ruleInternalName `
                    -Direction Inbound `
                    -Action Allow `
                    -Protocol TCP `
                    -LocalPort $Port `
                    -Enabled True `
                    -Profile Any `
                    -ErrorAction Stop | Out-Null
                Write-Log "Created firewall rule '$ruleDisplayName' (TCP $Port)." -Level OK
            }
        }
        else {
            Write-Log "NetSecurity cmdlets unavailable; using netsh advfirewall fallback." -Level WARN
            $null = & netsh advfirewall firewall delete rule name="$ruleDisplayName" 2>&1
            $addOut = & netsh advfirewall firewall add rule name="$ruleDisplayName" dir=in action=allow protocol=TCP localport=$Port enable=yes profile=any 2>&1
            if ($LASTEXITCODE -ne 0) {
                throw "netsh add rule failed (exit $LASTEXITCODE): $($addOut -join ' ')"
            }
            Write-Log "Created firewall rule '$ruleDisplayName' via netsh (TCP $Port)." -Level OK
        }

        Write-Log "Firewall rules take effect immediately; no HeimdallApi service restart is required."
    }
    catch {
        Write-Log "Could not configure Windows Firewall: $($_.Exception.Message)" -Level WARN
        Write-Log "Install continues; local API access may still work. Allow inbound TCP $Port manually if remote agents cannot connect." -Level WARN
        Write-Log "No HeimdallApi restart is needed after you add a firewall rule; retry from the agent PC with Invoke-RestMethod or Test-NetConnection." -Level WARN
    }
}

function Wait-ServiceStopped {
    param(
        [Parameter(Mandatory = $true)]
        [string]$Name,
        [int]$TimeoutSec = 60
    )
    Write-Log "Waiting until service '$Name' is Stopped..."
    $deadline = (Get-Date).AddSeconds($TimeoutSec)
    while ($true) {
        $svc = Get-Service -Name $Name -ErrorAction SilentlyContinue
        if (-not $svc -or $svc.Status -eq "Stopped") {
            Write-Log "Service '$Name' is stopped (or absent)"
            return
        }
        if ((Get-Date) -ge $deadline) {
            Write-Log "Timed out waiting for '$Name' to stop after ${TimeoutSec}s (Status=$($svc.Status))" -Level WARN
            return
        }
        Start-Sleep -Seconds 1
        if (Get-Command Update-InstallApiProgressDisplay -ErrorAction SilentlyContinue) {
            if (Test-InstallApiProgressActive) { Update-InstallApiProgressDisplay }
        }
    }
}

function Wait-ServiceRemoved {
    param(
        [Parameter(Mandatory = $true)]
        [string]$Name,
        [int]$TimeoutSec = 90
    )
    Write-Log "Waiting until service '$Name' is fully removed..."
    $deadline = (Get-Date).AddSeconds($TimeoutSec)
    $warnedMmc = $false
    while ($true) {
        $svc = Get-Service -Name $Name -ErrorAction SilentlyContinue
        if (-not $svc) {
            $null = & sc.exe query $Name 2>&1
            if ($LASTEXITCODE -ne 0) {
                Write-Log "Service '$Name' is gone"
                return
            }
        }
        if (-not $warnedMmc -and (Test-ServicesMmcLikelyOpen)) {
            Write-Log "mmc.exe is running - close Services.msc if open; open handles delay service deletion (error 1072)." -Level WARN
            $warnedMmc = $true
        }
        if ((Get-Date) -ge $deadline) {
            Write-Log "Timed out waiting for '$Name' removal after ${TimeoutSec}s. Close services.msc if open, wait, then re-run." -Level WARN
            return
        }
        Start-Sleep -Seconds 2
        if (Get-Command Update-InstallApiProgressDisplay -ErrorAction SilentlyContinue) {
            if (Test-InstallApiProgressActive) { Update-InstallApiProgressDisplay }
        }
    }
}

try {
    $logRoot = Join-Path $env:ProgramData "Heimdall\logs"
    New-Item -ItemType Directory -Force -Path $logRoot | Out-Null
    $stamp = Get-Date -Format "yyyyMMdd-HHmmss"
    $script:LogPath = Join-Path $logRoot "install-api-$stamp.log"

    Write-Banner "Heimdall API installer" Cyan
    Write-Log "Log file: $script:LogPath"

    $script:InstallStartedAt = Get-Date
    if (Get-Command Get-InstallApiTimingEstimate -ErrorAction SilentlyContinue) {
        $script:InstallTimingEstimate = Get-InstallApiTimingEstimate -StartedAt $script:InstallStartedAt
        $estMmSs = Format-InstallApiDurationMmSs -TotalSec $script:InstallTimingEstimate.EstimatedSec
        $finishText = $script:InstallTimingEstimate.FinishAt.ToString("HH:mm:ss")
        Write-Log "Estimated install time: ~$estMmSs ($($script:InstallTimingEstimate.EstimatedSec)s incl. $($script:InstallTimingEstimate.BufferPercent)% buffer; baseline $($script:InstallTimingEstimate.BaselineSec)s from $($script:InstallTimingEstimate.Source))"
        Write-Log "Expected finish (wall clock): $finishText"
        Start-InstallApiConsoleCountdown -FinishAt $script:InstallTimingEstimate.FinishAt -EstimatedSec $script:InstallTimingEstimate.EstimatedSec -LogPath $script:LogPath -TotalSteps $script:InstallProgressTotalSteps
    }

    $isAdmin = ([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
    Write-Log "User: $env:USERNAME | Elevated: $isAdmin"
    Write-Log "Machine: $env:COMPUTERNAME | OS: $([System.Environment]::OSVersion.VersionString)"
    Write-Log "Params: InstallDir=$InstallDir Port=$Port ApiKey=****$($ApiKey.Substring([Math]::Max(0, $ApiKey.Length - 4)))"

    $root = Split-Path -Parent $PSScriptRoot
    $project = Join-Path $root "src\Heimdall.Api\Heimdall.Api.csproj"
    Write-Log "Repo root: $root"
    Write-Log "Project: $project"

    Invoke-Logged "Check .NET SDK and runtimes" -ProgressStep 1 -ProgressLabel "Checking .NET SDK" {
        $dotnet = Get-Command dotnet -ErrorAction Stop
        Write-Log "dotnet: $($dotnet.Source)"
        $sdks = & dotnet --list-sdks 2>&1
        $sdks | ForEach-Object { Write-Log "  SDK: $_" }
        if (-not ($sdks | Where-Object { $_ -match '^10\.' })) {
            Write-Log "No .NET 10 SDK listed - publish may fail. Install .NET 10 SDK." -Level WARN
        }
        $runtimes = & dotnet --list-runtimes 2>&1
        $runtimes | ForEach-Object { Write-Log "  Runtime: $_" }
        # AspNetCore patch N requires matching Microsoft.NETCore.App N (Error 1053 if missing).
        $aspPatches = @($runtimes | ForEach-Object {
            if ($_ -match '^Microsoft\.AspNetCore\.App (10\.\d+\.\d+)') { $Matches[1] }
        } | Select-Object -Unique)
        foreach ($ver in $aspPatches) {
            $hasCore = $runtimes | Where-Object { $_ -match "^Microsoft\.NETCore\.App $([regex]::Escape($ver))\b" }
            if (-not $hasCore) {
                throw "Microsoft.AspNetCore.App $ver is installed but Microsoft.NETCore.App $ver is missing. Install the .NET $ver Runtime (not only ASP.NET Core) from https://dotnet.microsoft.com/download/dotnet/10.0 — otherwise Heimdall.Api.exe fails immediately and the service reports Error 1053."
            }
        }
        if (-not ($runtimes | Where-Object { $_ -match '^Microsoft\.AspNetCore\.App 10\.' })) {
            Write-Log "No Microsoft.AspNetCore.App 10.x runtime - framework-dependent publish will not start." -Level WARN
        }
    }

    Invoke-Logged "Ensure project exists" -ProgressStep 2 -ProgressLabel "Preparing directories" {
        if (-not (Test-Path $project)) {
            throw "Project not found: $project - run from a full Heimdall clone (scripts next to src)."
        }
        Write-Log "Project OK"
    }

    Invoke-Logged "Ensure ProgramData\Heimdall" {
        $dataDir = Join-Path $env:ProgramData "Heimdall"
        New-Item -ItemType Directory -Force -Path $dataDir | Out-Null
        Write-Log "Data dir: $dataDir"
    }

    Invoke-Logged "Ensure install directory" {
        New-Item -ItemType Directory -Force -Path $InstallDir | Out-Null
        Write-Log "InstallDir: $InstallDir"
    }

    Invoke-Logged "Stop existing HeimdallApi (unlock publish target)" -ProgressStep 3 -ProgressLabel "Stopping service" {
        $svc = Get-Service -Name "HeimdallApi" -ErrorAction SilentlyContinue
        if ($svc) {
            Write-Log "Existing service Status=$($svc.Status)"
            if ($svc.Status -ne "Stopped") {
                Write-Log "Stopping HeimdallApi so binaries can be updated..."
                Stop-Service HeimdallApi -Force -ErrorAction SilentlyContinue
                Wait-ServiceStopped -Name "HeimdallApi"
            }
            else {
                Write-Log "Service already Stopped"
            }
        }
        else {
            Write-Log "No existing HeimdallApi service"
        }
    }

    Invoke-Logged "dotnet publish (verbose)" -ProgressStep 4 -ProgressLabel "Publishing API" {
        $finishAt = if ($script:InstallTimingEstimate) {
            $script:InstallTimingEstimate.FinishAt
        }
        else {
            $script:InstallStartedAt.AddSeconds(120)
        }
        Write-Log "Command: dotnet publish `"$project`" -c Release -o `"$InstallDir`" --self-contained false -v detailed"
        Write-Log "Full verbose publish log: $script:LogPath"
        if (Get-Command Invoke-DotNetPublishWithProgress -ErrorAction SilentlyContinue) {
            $publishExit = Invoke-DotNetPublishWithProgress `
                -Project $project `
                -OutputDir $InstallDir `
                -FinishAt $finishAt `
                -LogPath $script:LogPath
            if ($publishExit -ne 0) {
                throw "dotnet publish exited with code $publishExit"
            }
        }
        elseif (Get-Command Invoke-DotNetPublishWithLiveViewport -ErrorAction SilentlyContinue) {
            $publishExit = Invoke-DotNetPublishWithLiveViewport `
                -Project $project `
                -OutputDir $InstallDir `
                -FinishAt $finishAt `
                -LogPath $script:LogPath
            if ($publishExit -ne 0) {
                throw "dotnet publish exited with code $publishExit"
            }
        }
        else {
            & dotnet publish $project -c Release -o $InstallDir --self-contained false -v detailed 2>&1 | ForEach-Object {
                $line = "$_"
                Write-Host $line
                Add-Content -Path $script:LogPath -Value $line -Encoding UTF8
            }
            if ($LASTEXITCODE -ne 0) {
                throw "dotnet publish exited with code $LASTEXITCODE"
            }
        }
        $exe = Join-Path $InstallDir "Heimdall.Api.exe"
        if (-not (Test-Path $exe)) {
            throw "Expected exe missing after publish: $exe"
        }
        Write-Log "Published: $exe"
    }

    Invoke-Logged "Write appsettings.json" -ProgressStep 5 -ProgressLabel "Writing config" {
        $appsettings = Join-Path $InstallDir "appsettings.json"
        $dbPath = Join-Path $env:ProgramData "Heimdall\heimdall.db"
        $sandboxDbPath = Join-Path $env:ProgramData "Heimdall\heimdall-dev.db"
        $json = @{
            ConnectionStrings = @{
                Heimdall = "Data Source=$dbPath"
                HeimdallSandbox = "Data Source=$sandboxDbPath"
            }
            Heimdall = @{
                ApiKey = $ApiKey
                DatabaseMode = "live"
                LiveDashboardUrl = "http://${env:COMPUTERNAME}:$Port"
                DevDashboardUrl = "http://localhost:5080"
                UiTheme = "Cosmic"
                StaffAccess = @{
                    RequireWindowsAuth = $true
                    EmailDomainSuffixes = @("arup.com")
                    AllowDevBypass = $false
                    AdminEmails = @("christopher.owen@arup.com")
                    AdminPreviewMinutes = 30
                }
            }
            Logging = @{
                LogLevel = @{
                    Default = "Information"
                    "Microsoft.AspNetCore" = "Warning"
                }
            }
            AllowedHosts = "*"
            Urls = "http://0.0.0.0:$Port"
        } | ConvertTo-Json -Depth 5
        Set-Content -Path $appsettings -Value $json -Encoding UTF8
        Write-Log "Wrote $appsettings"
        Write-Log "SQLite: $dbPath"
        Write-Log "Sandbox SQLite: $sandboxDbPath"
        Write-Log "Urls: http://0.0.0.0:$Port"
        Write-Log "ApiKey (last 4): ****$($ApiKey.Substring([Math]::Max(0, $ApiKey.Length - 4)))"
    }

    Invoke-Logged "Stop/remove existing HeimdallApi service" -ProgressStep 6 -ProgressLabel "Recreating Windows service" {
        $svc = Get-Service -Name "HeimdallApi" -ErrorAction SilentlyContinue
        if ($svc) {
            Write-Log "Existing service Status=$($svc.Status)"
            if ($svc.Status -ne "Stopped") {
                Write-Log "Stopping HeimdallApi before service recreate..."
                Stop-Service HeimdallApi -Force -ErrorAction SilentlyContinue
                Wait-ServiceStopped -Name "HeimdallApi"
            }
            Write-Log "sc.exe delete HeimdallApi"
            $del = & sc.exe delete HeimdallApi 2>&1
            $del | ForEach-Object { Write-Log "  $_" }
            Wait-ServiceRemoved -Name "HeimdallApi"
        }
        else {
            Write-Log "No existing HeimdallApi service"
        }
    }

    Invoke-Logged "Create HeimdallApi service" {
        $exe = Join-Path $InstallDir "Heimdall.Api.exe"
        Write-Log "sc.exe create HeimdallApi binPath= `"$exe`" start= auto"
        $maxAttempts = 10
        $createExit = -1
        for ($attempt = 1; $attempt -le $maxAttempts; $attempt++) {
            $create = & sc.exe create HeimdallApi binPath= "`"$exe`"" start= auto DisplayName= "Heimdall API" 2>&1
            $createExit = $LASTEXITCODE
            $create | ForEach-Object { Write-Log "  $_" }
            if ($createExit -eq 0) { break }
            if ($createExit -eq 1072) {
                Write-Log "sc.exe create exit 1072 (marked for deletion) - attempt $attempt/$maxAttempts; sleeping 3s..." -Level WARN
                if (Test-ServicesMmcLikelyOpen) {
                    Write-Log "mmc.exe is running - close Services.msc if open so deletion can finish." -Level WARN
                }
                Start-Sleep -Seconds 3
                continue
            }
            throw "sc.exe create failed with exit $createExit"
        }
        if ($createExit -ne 0) {
            throw "sc.exe create failed with exit $createExit after $maxAttempts attempts (1072: close services.msc, wait, retry)"
        }
        $desc = & sc.exe description HeimdallApi "Heimdall ingest API and dashboard" 2>&1
        $desc | ForEach-Object { Write-Log "  $_" }
    }

    Invoke-Logged "Start HeimdallApi" -ProgressStep 7 -ProgressLabel "Starting service" {
        try {
            Start-Service HeimdallApi -ErrorAction Stop
        }
        catch {
            Write-Log "Start-Service error: $($_.Exception.Message)" -Level ERROR
            $svc = Get-Service HeimdallApi -ErrorAction SilentlyContinue
            if ($svc) { Write-Log "Service Status=$($svc.Status)" -Level ERROR }
            throw
        }
        Start-Sleep -Seconds 2
        $svc = Get-Service HeimdallApi
        Write-Log "Service Status=$($svc.Status)"
        if ($svc.Status -ne "Running") {
            throw "HeimdallApi did not reach Running (Status=$($svc.Status))"
        }
    }

    if (Get-Command Set-InstallApiProgressStep -ErrorAction SilentlyContinue) {
        Set-InstallApiProgressStep -StepIndex 8 -TotalSteps $script:InstallProgressTotalSteps -StepName "Firewall"
    }
    Write-Log ">>> Windows Firewall inbound rule (TCP $Port)" -Level STEP
    Ensure-HeimdallApiFirewallRule -Port $Port
    Write-Log "<<< Windows Firewall inbound rule - step finished" -Level OK

    Invoke-Logged "Probe health endpoint" -ProgressStep 9 -ProgressLabel "Health check" {
        $url = "http://localhost:$Port/api/health"
        Write-Log "GET $url"
        Start-Sleep -Seconds 1
        try {
            $r = Invoke-WebRequest -Uri $url -UseBasicParsing -TimeoutSec 10
            Write-Log "HTTP $($r.StatusCode) - $($r.Content)"
        }
        catch {
            Write-Log "Health probe failed (service may still be starting): $($_.Exception.Message)" -Level WARN
        }
    }

    Write-Banner "SUCCESS - Heimdall API installed" Green
    Write-Log "Dashboard: http://localhost:$Port"
    Write-Log "Health:    http://localhost:$Port/api/health"
    Write-Log "Service:   HeimdallApi"
    Write-Log "Log file:  $script:LogPath"
    if ($script:InstallStartedAt -and (Get-Command Save-InstallApiTimingResult -ErrorAction SilentlyContinue)) {
        $actualSec = [int][Math]::Max(0, ((Get-Date) - $script:InstallStartedAt).TotalSeconds)
        Save-InstallApiTimingResult -DurationSec $actualSec -Success $true
        Write-Log "Install duration: $(Format-InstallApiDurationMmSs -TotalSec $actualSec) ($actualSec s) - saved for next estimate"
    }
    $exitCode = 0
}
catch {
    $script:LastError = $_
    Write-Banner "FAILURE - Heimdall API install did not complete" Red
    Write-Log "Last error: $($_.Exception.Message)" -Level ERROR
    if ($_.ScriptStackTrace) { Write-Log $_.ScriptStackTrace -Level ERROR }
    Write-Log "Send this log for analysis: $script:LogPath" -Level WARN
    $exitCode = 1
}
finally {
    if (Get-Command Stop-InstallApiConsoleCountdown -ErrorAction SilentlyContinue) {
        Stop-InstallApiConsoleCountdown -KeepWindowOpen
    }
    if ($exitCode -ne 0 -and $script:InstallStartedAt -and (Get-Command Save-InstallApiTimingResult -ErrorAction SilentlyContinue)) {
        $actualSec = [int][Math]::Max(0, ((Get-Date) - $script:InstallStartedAt).TotalSeconds)
        Save-InstallApiTimingResult -DurationSec $actualSec -Success $false
    }
    Write-Host ""
    Write-Host "Full log path (copy this for Cursor / support):" -ForegroundColor Yellow
    Write-Host "  $script:LogPath" -ForegroundColor Yellow
    if ($script:LastError -and $exitCode -ne 0) {
        Write-Host ""
        Write-Host "Last error:" -ForegroundColor Red
        Write-Host "  $($script:LastError.Exception.Message)" -ForegroundColor Red
    }
    Write-Host ""
    if (-not $NoPrompt) {
        Read-Host "Press Enter to close"
    }
    exit $exitCode
}
