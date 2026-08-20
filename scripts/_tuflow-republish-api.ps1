# Elevated one-shot: publish API (exclude appsettings), copy TuflowLauncher beside agent, restart HeimdallApi.
# Shows the same WinForms progress window as install-api (step X of Y + ETA), unless -NoProgressWindow
# (Launch Control ActionOnly / console-driven redeploy — progress is mirrored into launch-control logs).
# Exit 0 = publish + deploy + /api/health OK. Flood UI pages are NOT used as a gate (they return 403
# without an interactive Windows identity / flood membership).
param(
    [switch]$NoProgressWindow,
    # When Launch Control pre-creates a session log, use that path only (avoids matching republish-api-deploy.log).
    [string]$SessionLog = ''
)

$ErrorActionPreference = 'Stop'

if ($env:HEIMDALL_REDEPLOY_NO_UI -eq '1') {
    $NoProgressWindow = $true
}

$repoRoot = Split-Path -Parent $PSScriptRoot
if (-not (Test-Path -LiteralPath (Join-Path $repoRoot 'src\Heimdall.Api\Heimdall.Api.csproj'))) {
    # Fallback when script is invoked from an unexpected layout
    $repoRoot = 'C:\Heimdall'
}

$project = Join-Path $repoRoot 'src\Heimdall.Api\Heimdall.Api.csproj'
$publish = Join-Path $repoRoot 'dist\_publish\Api'
$dest = Join-Path $env:ProgramFiles 'Heimdall\Api'
$agentLauncher = Join-Path $env:ProgramFiles 'Heimdall\Agent\TuflowLauncher'
$launcherSrc = Join-Path $repoRoot 'dist\TuflowLauncher-publish'
$logDir = Join-Path $env:ProgramData 'Heimdall\logs'
New-Item -ItemType Directory -Force -Path $logDir | Out-Null
if (-not [string]::IsNullOrWhiteSpace($SessionLog)) {
    $log = $SessionLog.Trim()
    $parent = Split-Path -Parent $log
    if ($parent) { New-Item -ItemType Directory -Force -Path $parent | Out-Null }
}
elseif (-not [string]::IsNullOrWhiteSpace($env:HEIMDALL_REDEPLOY_LOG)) {
    $log = $env:HEIMDALL_REDEPLOY_LOG.Trim()
}
else {
    $log = Join-Path $logDir ("republish-api-{0:yyyyMMdd-HHmmss}.log" -f (Get-Date))
}
# Touch session log early so the parent can tail only this file (not the cumulative deploy log).
if (-not (Test-Path -LiteralPath $log)) {
    Set-Content -LiteralPath $log -Value '' -Encoding utf8
}
$deployLog = Join-Path $logDir 'republish-api-deploy.log'

$timingHelper = Join-Path $PSScriptRoot 'Heimdall-InstallApiTiming.ps1'
if (Test-Path -LiteralPath $timingHelper) {
    . $timingHelper
    # Separate baseline from full installs (redeploy is usually shorter).
    $script:InstallApiTimingFile = Join-Path $env:LOCALAPPDATA 'Heimdall\republish-api-timing.json'
    $script:InstallApiTimingDefaultColdSec = 90
}

$script:RepublishTotalSteps = 6
$script:RepublishStartedAt = $null
$script:RepublishExitCode = 1
$script:RepublishTimingEstimate = $null

function Write-RepublishLog([string]$Message, [string]$Level = 'INFO') {
    $line = "[{0:HH:mm:ss}] [{1}] {2}" -f (Get-Date), $Level, $Message
    if (Get-Command Test-InstallApiProgressActive -ErrorAction SilentlyContinue) {
        if (Test-InstallApiProgressActive) {
            Add-Content -LiteralPath $log -Value $line -ErrorAction SilentlyContinue
            Add-Content -LiteralPath $deployLog -Value $line -ErrorAction SilentlyContinue
            if ($Level -in @('STEP', 'OK', 'ERROR', 'WARN') -and (Get-Command Set-InstallApiProgressStatus -ErrorAction SilentlyContinue)) {
                Set-InstallApiProgressStatus -StatusLine $Message
            }
            return
        }
    }
    Write-Host $line
    Add-Content -LiteralPath $log -Value $line -ErrorAction SilentlyContinue
    Add-Content -LiteralPath $deployLog -Value $line -ErrorAction SilentlyContinue
}

function Set-RepublishStep([int]$Index, [string]$Name) {
    if (Get-Command Set-InstallApiProgressStep -ErrorAction SilentlyContinue) {
        Set-InstallApiProgressStep -StepIndex $Index -TotalSteps $script:RepublishTotalSteps -StepName $Name
    }
    Write-RepublishLog $Name -Level STEP
}

function Wait-RepublishSeconds([int]$Seconds) {
    $deadline = (Get-Date).AddSeconds($Seconds)
    while ((Get-Date) -lt $deadline) {
        if (Get-Command Update-InstallApiProgressDisplay -ErrorAction SilentlyContinue) {
            if (Test-InstallApiProgressActive) { Update-InstallApiProgressDisplay }
        }
        Start-Sleep -Milliseconds 250
    }
}

function Get-HeimdallApiServicePid {
    try {
        $row = Get-CimInstance Win32_Service -Filter "Name='HeimdallApi'" -ErrorAction SilentlyContinue
        if ($row -and [int]$row.ProcessId -gt 0) { return [int]$row.ProcessId }
    }
    catch { }
    return 0
}

# Request stop via sc.exe (avoids Stop-Service's long "Waiting for service..." spam),
# poll until Stopped, then force-kill Heimdall.Api if still holding files.
function Stop-HeimdallApiForRedeploy {
    param(
        [int]$TimeoutSec = 45,
        [int]$ForceKillAfterSec = 20
    )

    $svc = Get-Service -Name HeimdallApi -ErrorAction SilentlyContinue
    if (-not $svc) {
        Write-RepublishLog 'HeimdallApi service not installed - nothing to stop'
        return
    }
    if ($svc.Status -eq 'Stopped') {
        Write-RepublishLog 'HeimdallApi already stopped'
        return
    }

    Write-RepublishLog 'Stopping HeimdallApi (sc.exe stop)...'
    # sc.exe returns immediately after requesting stop; avoids Stop-Service blocking + WARN spam.
    $null = & sc.exe stop HeimdallApi 2>&1

    $started = Get-Date
    $deadline = $started.AddSeconds($TimeoutSec)
    $killed = $false
    while ($true) {
        $svc = Get-Service -Name HeimdallApi -ErrorAction SilentlyContinue
        if (-not $svc -or $svc.Status -eq 'Stopped') {
            Write-RepublishLog 'HeimdallApi is stopped'
            break
        }

        $elapsed = ((Get-Date) - $started).TotalSeconds
        if (-not $killed -and $elapsed -ge $ForceKillAfterSec) {
            $svcPid = Get-HeimdallApiServicePid
            if ($svcPid -gt 0) {
                Write-RepublishLog "HeimdallApi still $($svc.Status) after ${ForceKillAfterSec}s — force-killing PID $svcPid" -Level WARN
                try {
                    Stop-Process -Id $svcPid -Force -ErrorAction Stop
                    $killed = $true
                }
                catch {
                    Write-RepublishLog "Force-kill PID $svcPid failed: $($_.Exception.Message)" -Level WARN
                }
            }
            else {
                # Service reports running but no PID — try process name.
                $procs = @(Get-Process -Name 'Heimdall.Api' -ErrorAction SilentlyContinue)
                if ($procs.Count -gt 0) {
                    Write-RepublishLog ("Force-killing Heimdall.Api process(es): " + ($procs.Id -join ', ')) -Level WARN
                    $procs | Stop-Process -Force -ErrorAction SilentlyContinue
                    $killed = $true
                }
            }
        }

        if ((Get-Date) -ge $deadline) {
            $svcPid = Get-HeimdallApiServicePid
            Write-RepublishLog "Timed out waiting for HeimdallApi to stop (Status=$($svc.Status), PID=$svcPid)" -Level WARN
            if ($svcPid -gt 0) {
                try { Stop-Process -Id $svcPid -Force -ErrorAction SilentlyContinue } catch { }
            }
            Get-Process -Name 'Heimdall.Api' -ErrorAction SilentlyContinue | Stop-Process -Force -ErrorAction SilentlyContinue
            break
        }

        if (Get-Command Update-InstallApiProgressDisplay -ErrorAction SilentlyContinue) {
            if (Test-InstallApiProgressActive) { Update-InstallApiProgressDisplay }
        }
        Start-Sleep -Milliseconds 400
    }

    # Brief settle so file locks release before robocopy.
    Wait-RepublishSeconds 2
}

try {
    $script:RepublishStartedAt = Get-Date
    Write-RepublishLog "Log file: $log"

    if (Get-Command Get-InstallApiTimingEstimate -ErrorAction SilentlyContinue) {
        $script:RepublishTimingEstimate = Get-InstallApiTimingEstimate -StartedAt $script:RepublishStartedAt
        $estMmSs = Format-InstallApiDurationMmSs -TotalSec $script:RepublishTimingEstimate.EstimatedSec
        Write-RepublishLog ("Estimated redeploy time: ~{0} ({1}s; baseline {2}s from {3})" -f `
            $estMmSs,
            $script:RepublishTimingEstimate.EstimatedSec,
            $script:RepublishTimingEstimate.BaselineSec,
            $script:RepublishTimingEstimate.Source)
        if (-not $NoProgressWindow) {
            Start-InstallApiConsoleCountdown `
                -FinishAt $script:RepublishTimingEstimate.FinishAt `
                -EstimatedSec $script:RepublishTimingEstimate.EstimatedSec `
                -LogPath $log `
                -TotalSteps $script:RepublishTotalSteps `
                -WindowTitle 'Heimdall API redeploy'
        }
        else {
            Write-RepublishLog 'Progress window skipped (-NoProgressWindow) — watch Launch Control console / this log' -Level INFO
        }
    }

    Set-RepublishStep 1 'Preparing'
    if (-not (Test-Path -LiteralPath $project)) {
        throw "Project not found: $project"
    }
    Write-RepublishLog "Project: $project"

    Set-RepublishStep 2 'Publishing API'
    Write-RepublishLog "Publishing API to $publish..."
    if (Test-Path -LiteralPath $publish) {
        Remove-Item -LiteralPath $publish -Recurse -Force
    }
    New-Item -ItemType Directory -Force -Path $publish | Out-Null

    $finishAt = if ($script:RepublishTimingEstimate) {
        $script:RepublishTimingEstimate.FinishAt
    }
    else {
        $script:RepublishStartedAt.AddSeconds(90)
    }

    if (Get-Command Invoke-DotNetPublishWithProgress -ErrorAction SilentlyContinue) {
        $publishExit = Invoke-DotNetPublishWithProgress `
            -Project $project `
            -OutputDir $publish `
            -FinishAt $finishAt `
            -LogPath $log
        if ($publishExit -ne 0) {
            throw "dotnet publish failed: $publishExit (see $log)"
        }
    }
    else {
        $publishLog = Join-Path $logDir ("republish-api-publish-{0:yyyyMMdd-HHmmss}.log" -f (Get-Date))
        & dotnet publish $project -c Release -o $publish --self-contained false -v minimal *>&1 |
            Tee-Object -FilePath $publishLog |
            ForEach-Object { $_ }
        if ($LASTEXITCODE -ne 0) {
            Write-RepublishLog "Publish log: $publishLog" -Level ERROR
            throw "dotnet publish failed: $LASTEXITCODE (see $publishLog)"
        }
    }

    Set-RepublishStep 3 'Stopping HeimdallApi'
    Stop-HeimdallApiForRedeploy -TimeoutSec 45 -ForceKillAfterSec 20

    Set-RepublishStep 4 'Deploying binaries'
    Write-RepublishLog 'Robocopy (exclude appsettings)...'
    & robocopy $publish $dest /E /XF appsettings.json appsettings.*.json /NFL /NDL /NJH /NJS /nc /ns /np
    # robocopy 0-7 = success
    if ($LASTEXITCODE -ge 8) { throw "robocopy failed: $LASTEXITCODE" }

    $mergeHelper = Join-Path $PSScriptRoot 'Merge-HeimdallCodeMeterAppsettings.ps1'
    if (Test-Path -LiteralPath $mergeHelper) {
        . $mergeHelper
        $cfg = Join-Path $dest 'appsettings.json'
        if (Merge-HeimdallCodeMeterAppsettings -AppSettingsPath $cfg -EnableIfRuntimePresent) {
            Write-RepublishLog "Merged Heimdall:CodeMeter into $cfg (Enabled if cmu32 is present)" -Level OK
        }
    }

    Set-RepublishStep 5 'TuflowLauncher (optional)'
    if (Test-Path (Join-Path $env:ProgramFiles 'Heimdall\Agent')) {
        Write-RepublishLog 'Copying TuflowLauncher beside agent...'
        New-Item -ItemType Directory -Force -Path $agentLauncher | Out-Null
        if (Test-Path -LiteralPath $launcherSrc) {
            & robocopy $launcherSrc $agentLauncher /E /NFL /NDL /NJH /NJS /nc /ns /np | Out-Null
            if ($LASTEXITCODE -ge 8) {
                Write-RepublishLog "WARN: TuflowLauncher robocopy exit $LASTEXITCODE (continuing)" -Level WARN
            }
        }
        else {
            Write-RepublishLog "TuflowLauncher source missing ($launcherSrc) - skipped" -Level WARN
        }
    }
    else {
        Write-RepublishLog 'Agent folder not installed - skipping TuflowLauncher copy'
    }

    Set-RepublishStep 6 'Start service + health'
    Write-RepublishLog 'Starting HeimdallApi...'
    # SCM default ServicesPipeTimeout is 30s. After a large robocopy / AV scan / DB seed,
    # HeimdallApi often reports RUNNING a few seconds later — Start-Service then throws 1053
    # even though the process is still starting. Do not treat that as a hard fail; poll health.
    try {
        Start-Service HeimdallApi -ErrorAction Stop
    }
    catch {
        Write-RepublishLog ("Start-Service: $($_.Exception.Message) — will poll for Running + health") -Level WARN
        try { & sc.exe start HeimdallApi 2>&1 | Out-Null } catch { }
    }

    $health = $null
    $deadline = (Get-Date).AddSeconds(120)
    while ((Get-Date) -lt $deadline) {
        $svc = Get-Service HeimdallApi -ErrorAction SilentlyContinue
        if ($svc -and $svc.Status -eq 'Running') {
            try {
                # Public health only - do not probe Flood-gated Razor pages (403 without flood access).
                $health = Invoke-RestMethod 'http://127.0.0.1:5080/api/health' -TimeoutSec 10
                if ($health -and "$($health.status)" -match '^(?i)ok$') { break }
            }
            catch {
                # Still binding / seeding — keep waiting.
            }
        }
        Wait-RepublishSeconds 2
    }

    $svc = Get-Service HeimdallApi -ErrorAction SilentlyContinue
    if (-not $svc -or $svc.Status -ne 'Running') {
        throw "HeimdallApi status is $(if ($svc) { $svc.Status } else { 'missing' }), expected Running"
    }
    if (-not $health -or "$($health.status)" -notmatch '^(?i)ok$') {
        throw "HeimdallApi is Running but /api/health did not return ok within 120s"
    }
    Write-RepublishLog ("Health: " + ($health | ConvertTo-Json -Compress)) -Level OK

    $dllPath = Join-Path $dest 'Heimdall.Api.dll'
    $dllInfo = Get-Item -LiteralPath $dllPath -ErrorAction Stop
    $dllStamp = $dllInfo.LastWriteTime.ToString('yyyy-MM-dd HH:mm:ss')
    Write-RepublishLog ("Installed Heimdall.Api.dll LastWriteTime={0} Size={1}" -f $dllStamp, $dllInfo.Length) -Level OK

    # Unique success markers for Launch Control (do not rely on MSBuild "Done" noise).
    Write-RepublishLog 'HEIMDALL_REDEPLOY_OK' -Level OK
    Write-RepublishLog "DONE (log: $log)" -Level OK

    $resultPath = Join-Path $logDir 'republish-api-last-result.json'
    $result = [ordered]@{
        ok                = $true
        finishedUtc       = (Get-Date).ToUniversalTime().ToString('o')
        logPath           = $log
        dllPath           = $dllPath
        dllLastWriteTime  = $dllInfo.LastWriteTime.ToString('o')
        dllLength         = $dllInfo.Length
        health            = $health
    }
    ($result | ConvertTo-Json -Depth 6) | Set-Content -LiteralPath $resultPath -Encoding UTF8

    if ($script:RepublishStartedAt -and (Get-Command Save-InstallApiTimingResult -ErrorAction SilentlyContinue)) {
        $actualSec = [int][Math]::Max(0, ((Get-Date) - $script:RepublishStartedAt).TotalSeconds)
        Save-InstallApiTimingResult -DurationSec $actualSec -Success $true
        Write-RepublishLog ("Redeploy duration: {0} ({1}s) - saved for next estimate" -f `
            (Format-InstallApiDurationMmSs -TotalSec $actualSec), $actualSec)
    }
    $script:RepublishExitCode = 0
}
catch {
    Write-RepublishLog "FAIL: $($_.Exception.Message)" -Level ERROR
    Write-RepublishLog 'HEIMDALL_REDEPLOY_FAIL' -Level ERROR
    try {
        $failPath = Join-Path $logDir 'republish-api-last-result.json'
        $failObj = [ordered]@{
            ok          = $false
            finishedUtc = (Get-Date).ToUniversalTime().ToString('o')
            logPath     = $log
            error       = $_.Exception.Message
        }
        ($failObj | ConvertTo-Json -Depth 4) | Set-Content -LiteralPath $failPath -Encoding UTF8
    }
    catch { }
    try { Start-Service HeimdallApi -ErrorAction SilentlyContinue } catch { }
    if ($script:RepublishStartedAt -and (Get-Command Save-InstallApiTimingResult -ErrorAction SilentlyContinue)) {
        $actualSec = [int][Math]::Max(0, ((Get-Date) - $script:RepublishStartedAt).TotalSeconds)
        Save-InstallApiTimingResult -DurationSec $actualSec -Success $false
    }
    Write-Host "Full log: $log"
    $script:RepublishExitCode = 1
}
finally {
    if (-not $NoProgressWindow -and (Get-Command Stop-InstallApiConsoleCountdown -ErrorAction SilentlyContinue)) {
        if ($script:RepublishExitCode -eq 0) {
            if (Get-Command Set-InstallApiProgressStatus -ErrorAction SilentlyContinue) {
                Set-InstallApiProgressStatus -StatusLine 'Redeploy finished successfully'
            }
            if ($script:InstallApiProgressLabels.Eta) {
                $script:InstallApiProgressLabels.Eta.Text = 'Done'
            }
            # Brief pause so the window is readable when launched elevated without -NoExit.
            Wait-RepublishSeconds 3
            Stop-InstallApiConsoleCountdown
        }
        else {
            if (Get-Command Set-InstallApiProgressStatus -ErrorAction SilentlyContinue) {
                Set-InstallApiProgressStatus -StatusLine 'Redeploy failed - see Open logs folder'
            }
            Stop-InstallApiConsoleCountdown -KeepWindowOpen
            Wait-RepublishSeconds 8
            Stop-InstallApiConsoleCountdown
        }
    }
}

exit $script:RepublishExitCode
