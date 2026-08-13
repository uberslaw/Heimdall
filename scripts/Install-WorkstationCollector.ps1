#Requires -Version 5.1
<#
.SYNOPSIS
  Silent Heimdall Agent service installer (Phase 1 + Phase 2 LKG rollback + Phase 3 heal add-on).

.DESCRIPTION
  Called by Install-WorkstationCollector.cmd (pack entry). Durable install lock +
  stage file under %ProgramData%\Heimdall\update\, cross-process mutex, hardened
  sc delete/create waits (1072 retry), last-known-good backup under update\lkg\,
  and restore (prefer LKG, else staging, else best-effort on-disk recreate).

  Stages: acquired-lock → backed-up → service-removed → copied → configured →
  service-created → service-running → committed
  (or rolled-back after a successful LKG heal).

  Cleared lock on success or successful LKG rollback. Env: HEIMDALL_SKIP_LAUNCH /
  HEIMDALL_NOPAUSE handled by .cmd.

  Phase 3 (opt-in): -EnableHealWatchdog or HEIMDALL_ENABLE_HEAL=1 registers
  SYSTEM task HeimdallAgentHeal. Silent UpdateClient must NOT pass this unless
  already desired — existing task is preserved and heal scripts refreshed when
  the task is already registered. -UnregisterHealWatchdog removes the task.
  -HealOnly restores from LKG (used by Heimdall-AgentHeal.ps1).
#>
[CmdletBinding()]
param(
    [string]$ApiUrl = "http://BNELT5CG5152D8R:5080",
    [string]$ApiKey = "heimdall-poc-key",
    [string]$MachineGroup = "POC",
    [string]$InstallDir = "",
    [string]$Payload = "",
    [switch]$EnableHealWatchdog,
    [switch]$UnregisterHealWatchdog,
    [switch]$HealOnly
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

$script:ServiceName = "HeimdallAgent"
$script:MutexName = "Global\HeimdallAgentInstall"
$script:LockStaleMinutes = 30
$script:ServiceRemoved = $false
$script:LogPath = $null
$script:Mutex = $null
$script:MutexOwned = $false
$script:ExitCode = 1
$script:StateStartedUtc = $null
$script:RestoredFromLkg = $false
$script:HadStagingBackup = $false

if ([string]::IsNullOrWhiteSpace($InstallDir)) {
    $InstallDir = Join-Path ${env:ProgramFiles} "Heimdall\Agent"
}
if ([string]::IsNullOrWhiteSpace($Payload)) {
    $Payload = Join-Path $PSScriptRoot "payload"
}

$script:UpdateRoot = Join-Path $env:ProgramData "Heimdall\update"
$script:LockPath = Join-Path $script:UpdateRoot "install.lock"
$script:StatePath = Join-Path $script:UpdateRoot "install-state.json"
$script:LkgPath = Join-Path $script:UpdateRoot "lkg"
$script:LkgStagingPath = Join-Path $script:UpdateRoot "lkg.staging"
$script:LkgOldPath = Join-Path $script:UpdateRoot "lkg.old"
$script:LogRoot = Join-Path $env:ProgramData "Heimdall\logs"
$script:HealRoot = Join-Path $env:ProgramData "Heimdall\heal"
$script:HealTaskName = "HeimdallAgentHeal"
$script:HealScriptName = "Heimdall-AgentHeal.ps1"

# Env override for silent/scripted enable (wizard sets this; UpdateClient must not).
if (-not $EnableHealWatchdog) {
    if ($env:HEIMDALL_ENABLE_HEAL -eq "1") {
        $EnableHealWatchdog = $true
    }
}

function Write-InstallLog {
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

function Remove-DirBestEffort([string]$Path) {
    if (-not (Test-Path -LiteralPath $Path)) { return }
    try {
        Remove-Item -LiteralPath $Path -Recurse -Force -ErrorAction Stop
    }
    catch {
        Write-InstallLog "Could not remove ${Path}: $($_.Exception.Message)" -Level WARN
    }
}

function Write-InstallState {
    param(
        [Parameter(Mandatory)][string]$Stage,
        [string]$Detail = "",
        [bool]$RolledBack = $false
    )
    Ensure-Dir $script:UpdateRoot
    $now = (Get-Date).ToUniversalTime().ToString("o")
    $obj = [ordered]@{
        stage           = $Stage
        startedUtc      = if ($script:StateStartedUtc) { $script:StateStartedUtc } else { $now }
        updatedUtc      = $now
        pid             = $PID
        computer        = $env:COMPUTERNAME
        installDir      = $InstallDir
        payload         = $Payload
        apiUrl          = $ApiUrl
        machineGroup    = $MachineGroup
        serviceRemoved  = [bool]$script:ServiceRemoved
        rolledBack      = [bool]$RolledBack
        lkgPresent      = [bool](Test-LkgPresent)
        stagingPresent  = [bool](Test-LkgStagingPresent)
        detail          = $Detail
    }
    if (-not $script:StateStartedUtc) { $script:StateStartedUtc = $now }
    $json = $obj | ConvertTo-Json -Depth 4
    $utf8 = New-Object System.Text.UTF8Encoding $true
    [System.IO.File]::WriteAllText($script:StatePath, $json, $utf8)
    Write-InstallLog "Stage=$Stage$(if ($Detail) { " — $Detail" })" -Level STEP
}

function Write-InstallLock {
    Ensure-Dir $script:UpdateRoot
    $started = (Get-Date).ToUniversalTime().ToString("o")
    $body = @(
        "pid=$PID"
        "startedUtc=$started"
        "computer=$env:COMPUTERNAME"
        "script=$PSCommandPath"
    ) -join [Environment]::NewLine
    $utf8 = New-Object System.Text.UTF8Encoding $true
    [System.IO.File]::WriteAllText($script:LockPath, $body, $utf8)
}

function Clear-InstallLock {
    try {
        if (Test-Path -LiteralPath $script:LockPath) {
            Remove-Item -LiteralPath $script:LockPath -Force -ErrorAction Stop
        }
    }
    catch {
        Write-InstallLog "Could not remove install.lock: $($_.Exception.Message)" -Level WARN
    }
}

function Clear-InstallLockAndState {
    Clear-InstallLock
    try {
        if (Test-Path -LiteralPath $script:StatePath) {
            Remove-Item -LiteralPath $script:StatePath -Force -ErrorAction Stop
        }
    }
    catch {
        Write-InstallLog "Could not remove install-state.json: $($_.Exception.Message)" -Level WARN
    }
}

function Test-LockOwnerAlive {
    param([int]$OwnerPid)
    if ($OwnerPid -le 0) { return $false }
    try {
        $p = Get-Process -Id $OwnerPid -ErrorAction Stop
        return $null -ne $p
    }
    catch {
        return $false
    }
}

function Test-ExistingLockIsFresh {
    if (-not (Test-Path -LiteralPath $script:LockPath)) { return $false }
    $ownerPid = 0
    $startedUtc = $null
    try {
        foreach ($line in Get-Content -LiteralPath $script:LockPath -ErrorAction Stop) {
            if ($line -match '^pid=(\d+)') { $ownerPid = [int]$Matches[1] }
            if ($line -match '^startedUtc=(.+)$') {
                try { $startedUtc = [DateTimeOffset]::Parse($Matches[1].Trim()) } catch { }
            }
        }
    }
    catch { return $false }

    if ($ownerPid -eq $PID) { return $true }
    if (Test-LockOwnerAlive -OwnerPid $ownerPid) {
        Write-InstallLog "install.lock held by live pid $ownerPid — refusing concurrent install" -Level ERROR
        return $true
    }
    if ($startedUtc -and ((Get-Date).ToUniversalTime() - $startedUtc.UtcDateTime).TotalMinutes -lt $script:LockStaleMinutes) {
        # Owner dead but lock young — still treat as contended briefly (AV / crash mid-write).
        $age = [int]((Get-Date).ToUniversalTime() - $startedUtc.UtcDateTime).TotalMinutes
        Write-InstallLog "install.lock age ${age}m with dead pid $ownerPid — treating as stale, taking over" -Level WARN
        return $false
    }
    Write-InstallLog "Stale install.lock (pid $ownerPid) — taking over" -Level WARN
    return $false
}

function Acquire-InstallMutex {
    $created = $false
    try {
        $script:Mutex = New-Object System.Threading.Mutex($false, $script:MutexName, [ref]$created)
    }
    catch {
        throw "Could not create install mutex $($script:MutexName): $($_.Exception.Message)"
    }
    Write-InstallLog "Waiting for install mutex ($($script:MutexName))..."
    $acquired = $false
    try {
        $acquired = $script:Mutex.WaitOne([TimeSpan]::FromMinutes(2))
    }
    catch [System.Threading.AbandonedMutexException] {
        # Previous installer crashed while holding the mutex — we now own it.
        $acquired = $true
        Write-InstallLog "Acquired abandoned install mutex (prior installer crashed)" -Level WARN
    }
    if (-not $acquired) {
        throw "Timed out waiting for install mutex — another installer may be running"
    }
    $script:MutexOwned = $true
    Write-InstallLog "Install mutex acquired" -Level OK
}

function Release-InstallMutex {
    if ($script:MutexOwned -and $script:Mutex) {
        try { $script:Mutex.ReleaseMutex() } catch { }
        $script:MutexOwned = $false
    }
    if ($script:Mutex) {
        try { $script:Mutex.Dispose() } catch { }
        $script:Mutex = $null
    }
}

function Test-ServicesMmcLikelyOpen {
    return [bool](Get-Process -Name mmc -ErrorAction SilentlyContinue)
}

function Wait-ServiceStopped {
    param([string]$Name, [int]$TimeoutSec = 60)
    Write-InstallLog "Waiting until service '$Name' is Stopped..."
    $deadline = (Get-Date).AddSeconds($TimeoutSec)
    while ($true) {
        $svc = Get-Service -Name $Name -ErrorAction SilentlyContinue
        if (-not $svc -or $svc.Status -eq "Stopped") {
            Write-InstallLog "Service '$Name' is stopped (or absent)"
            return
        }
        if ((Get-Date) -ge $deadline) {
            Write-InstallLog "Timed out waiting for '$Name' to stop (Status=$($svc.Status))" -Level WARN
            return
        }
        Start-Sleep -Seconds 1
    }
}

function Wait-ServiceRemoved {
    param([string]$Name, [int]$TimeoutSec = 90)
    Write-InstallLog "Waiting until service '$Name' is fully removed..."
    $deadline = (Get-Date).AddSeconds($TimeoutSec)
    $warnedMmc = $false
    while ($true) {
        $svc = Get-Service -Name $Name -ErrorAction SilentlyContinue
        if (-not $svc) {
            $null = & sc.exe query $Name 2>&1
            if ($LASTEXITCODE -ne 0) {
                Write-InstallLog "Service '$Name' is gone"
                return
            }
        }
        if (-not $warnedMmc -and (Test-ServicesMmcLikelyOpen)) {
            Write-InstallLog "mmc.exe is running - close Services.msc if open; open handles delay service deletion (error 1072)." -Level WARN
            $warnedMmc = $true
        }
        if ((Get-Date) -ge $deadline) {
            Write-InstallLog "Timed out waiting for '$Name' removal after ${TimeoutSec}s." -Level WARN
            return
        }
        Start-Sleep -Seconds 2
    }
}

function Test-LkgPresent {
    $exe = Join-Path $script:LkgPath "Heimdall.Agent.exe"
    return (Test-Path -LiteralPath $exe)
}

function Test-LkgStagingPresent {
    $exe = Join-Path $script:LkgStagingPath "Heimdall.Agent.exe"
    return (Test-Path -LiteralPath $exe)
}

function Get-LkgRestoreSource {
    if (Test-LkgPresent) { return $script:LkgPath }
    if (Test-LkgStagingPresent) { return $script:LkgStagingPath }
    return $null
}

function Backup-CurrentInstallToStaging {
    <#
      Copy current Program Files agent → lkg.staging. Does NOT replace committed lkg\
      until Commit-LkgFromInstallDir runs after a successful install.
    #>
    $exe = Join-Path $InstallDir "Heimdall.Agent.exe"
    if (-not (Test-Path -LiteralPath $exe)) {
        Write-InstallLog "No existing agent at $InstallDir — skipping LKG staging backup (fresh install)"
        Write-InstallState -Stage "backed-up" -Detail "No prior install to stage (fresh)"
        return $false
    }

    Write-InstallLog "Backing up current install to lkg.staging (committed lkg preserved until success)" -Level STEP
    Remove-DirBestEffort $script:LkgStagingPath
    Ensure-Dir $script:LkgStagingPath
    $rc = 0
    & robocopy.exe $InstallDir $script:LkgStagingPath /E /NFL /NDL /NJH /NJS /nc /ns /np /R:2 /W:2 | Out-Null
    $rc = $LASTEXITCODE
    if ($rc -ge 8) {
        throw "LKG staging robocopy failed with exit $rc"
    }
    if (-not (Test-LkgStagingPresent)) {
        throw "LKG staging missing Heimdall.Agent.exe after backup"
    }
    $settingsStaged = Test-Path -LiteralPath (Join-Path $script:LkgStagingPath "appsettings.json")
    $script:HadStagingBackup = $true
    Write-InstallState -Stage "backed-up" -Detail "lkg.staging ready (appsettings=$(if ($settingsStaged) { 'yes' } else { 'no' }); robocopy $rc)"
    Write-InstallLog "Staged current agent (incl. appsettings if present) → $($script:LkgStagingPath)" -Level OK
    return $true
}

function Commit-LkgFromInstallDir {
    <#
      After successful commit: replace single LKG with the newly installed good bits.
      Only then is the previous LKG discarded.
    #>
    $exe = Join-Path $InstallDir "Heimdall.Agent.exe"
    if (-not (Test-Path -LiteralPath $exe)) {
        Write-InstallLog "Commit LKG skipped — missing $exe" -Level WARN
        return
    }

    Write-InstallLog "Committing new LKG from $InstallDir" -Level STEP
    Remove-DirBestEffort $script:LkgOldPath
    if (Test-Path -LiteralPath $script:LkgPath) {
        try {
            Rename-Item -LiteralPath $script:LkgPath -NewName "lkg.old" -ErrorAction Stop
        }
        catch {
            Write-InstallLog "Could not rename prior LKG aside: $($_.Exception.Message) — removing" -Level WARN
            Remove-DirBestEffort $script:LkgPath
        }
    }

    Ensure-Dir $script:LkgPath
    $rc = 0
    & robocopy.exe $InstallDir $script:LkgPath /E /NFL /NDL /NJH /NJS /nc /ns /np /R:2 /W:2 | Out-Null
    $rc = $LASTEXITCODE
    if ($rc -ge 8 -or -not (Test-LkgPresent)) {
        Write-InstallLog "New LKG copy failed (robocopy $rc) — restoring prior LKG if available" -Level WARN
        Remove-DirBestEffort $script:LkgPath
        if (Test-Path -LiteralPath $script:LkgOldPath) {
            try {
                Rename-Item -LiteralPath $script:LkgOldPath -NewName "lkg" -ErrorAction Stop
            }
            catch {
                Write-InstallLog "Could not restore prior LKG name: $($_.Exception.Message)" -Level WARN
            }
        }
        return
    }

    Remove-DirBestEffort $script:LkgOldPath
    Remove-DirBestEffort $script:LkgStagingPath
    Write-InstallLog "LKG committed at $($script:LkgPath)" -Level OK
}

function Restore-FromLkg {
    param(
        [string]$Reason = "rollback"
    )
    $source = Get-LkgRestoreSource
    if (-not $source) {
        Write-InstallLog "LKG restore skipped — no lkg or lkg.staging with Heimdall.Agent.exe ($Reason)" -Level WARN
        return $false
    }

    $sourceLabel = Split-Path -Leaf $source
    Write-InstallLog "Restoring agent from $sourceLabel → $InstallDir ($Reason)" -Level WARN
    try {
        $existing = Get-Service -Name $script:ServiceName -ErrorAction SilentlyContinue
        if ($existing) {
            try { Stop-Service -Name $script:ServiceName -Force -ErrorAction SilentlyContinue } catch { }
            & sc.exe stop $script:ServiceName 2>&1 | ForEach-Object { Write-InstallLog "  $_" }
            Wait-ServiceStopped -Name $script:ServiceName
            & sc.exe delete $script:ServiceName 2>&1 | ForEach-Object { Write-InstallLog "  $_" }
            $script:ServiceRemoved = $true
            Wait-ServiceRemoved -Name $script:ServiceName
        }

        Ensure-Dir $InstallDir
        $rc = 0
        & robocopy.exe $source $InstallDir /E /NFL /NDL /NJH /NJS /nc /ns /np /R:2 /W:2 | Out-Null
        $rc = $LASTEXITCODE
        if ($rc -ge 8) {
            throw "LKG restore robocopy failed with exit $rc"
        }
        $exe = Join-Path $InstallDir "Heimdall.Agent.exe"
        if (-not (Test-Path -LiteralPath $exe)) {
            throw "Heimdall.Agent.exe missing after LKG restore"
        }

        $svc = Get-Service -Name $script:ServiceName -ErrorAction SilentlyContinue
        if (-not $svc) {
            New-HeimdallAgentService -ExePath $exe
        }
        Start-HeimdallAgentService

        $script:RestoredFromLkg = $true
        Write-InstallState -Stage "rolled-back" -Detail "Restored from $sourceLabel ($Reason)" -RolledBack $true
        Write-InstallLog "LKG restore succeeded from $sourceLabel — service Running" -Level OK
        return $true
    }
    catch {
        Write-InstallLog "LKG restore failed: $($_.Exception.Message)" -Level ERROR
        return $false
    }
}

function Test-PriorInstallNeedsHeal {
    if (Test-Path -LiteralPath $script:LockPath) {
        return $true
    }
    if (-not (Test-Path -LiteralPath $script:StatePath)) {
        return $false
    }
    try {
        $st = Get-Content -LiteralPath $script:StatePath -Raw -ErrorAction Stop | ConvertFrom-Json
        $stage = [string]$st.stage
        if ([string]::IsNullOrWhiteSpace($stage)) { return $false }
        if ($stage -eq "committed") { return $false }
        if ($stage -eq "rolled-back") {
            $svc = Get-Service -Name $script:ServiceName -ErrorAction SilentlyContinue
            return (-not $svc -or $svc.Status -ne "Running")
        }
        # Leftover incomplete stage from a prior attempt.
        return $true
    }
    catch {
        return $true
    }
}

function Remove-HeimdallAgentService {
    $svc = Get-Service -Name $script:ServiceName -ErrorAction SilentlyContinue
    if (-not $svc) {
        $null = & sc.exe query $script:ServiceName 2>&1
        if ($LASTEXITCODE -ne 0) {
            Write-InstallLog "No existing HeimdallAgent service"
            return
        }
    }

    Write-InstallLog "Stopping $($script:ServiceName)..."
    try {
        Stop-Service -Name $script:ServiceName -Force -ErrorAction SilentlyContinue
    }
    catch { }
    & sc.exe stop $script:ServiceName 2>&1 | ForEach-Object { Write-InstallLog "  $_" }
    Wait-ServiceStopped -Name $script:ServiceName

    Write-InstallLog "sc.exe delete $($script:ServiceName)"
    $del = & sc.exe delete $script:ServiceName 2>&1
    $del | ForEach-Object { Write-InstallLog "  $_" }
    $script:ServiceRemoved = $true
    Wait-ServiceRemoved -Name $script:ServiceName
    Write-InstallState -Stage "service-removed" -Detail "HeimdallAgent deleted (or delete requested)"
}

function New-HeimdallAgentService {
    param([Parameter(Mandatory)][string]$ExePath)
    Write-InstallLog "sc.exe create $($script:ServiceName) binPath= `"$ExePath`" start= auto"
    $maxAttempts = 10
    $createExit = -1
    for ($attempt = 1; $attempt -le $maxAttempts; $attempt++) {
        $create = & sc.exe create $script:ServiceName binPath= "`"$ExePath`"" start= auto DisplayName= "Heimdall Agent" 2>&1
        $createExit = $LASTEXITCODE
        $create | ForEach-Object { Write-InstallLog "  $_" }
        if ($createExit -eq 0) { break }
        if ($createExit -eq 1072) {
            Write-InstallLog "sc.exe create exit 1072 (marked for deletion) - attempt $attempt/$maxAttempts; sleeping 3s..." -Level WARN
            if (Test-ServicesMmcLikelyOpen) {
                Write-InstallLog "mmc.exe is running - close Services.msc if open so deletion can finish." -Level WARN
            }
            Start-Sleep -Seconds 3
            continue
        }
        throw "sc.exe create failed with exit $createExit"
    }
    if ($createExit -ne 0) {
        throw "sc.exe create failed with exit $createExit after $maxAttempts attempts (1072: close services.msc, wait, retry)"
    }
    $null = & sc.exe description $script:ServiceName "Heimdall workstation usage reporter" 2>&1
    Write-InstallState -Stage "service-created" -Detail "sc create OK"
}

function Start-HeimdallAgentService {
    try {
        Start-Service -Name $script:ServiceName -ErrorAction Stop
    }
    catch {
        Write-InstallLog "Start-Service error: $($_.Exception.Message)" -Level ERROR
        $out = & sc.exe start $script:ServiceName 2>&1
        $out | ForEach-Object { Write-InstallLog "  $_" }
        if ($LASTEXITCODE -ne 0) {
            throw "sc.exe start failed (exit $LASTEXITCODE)"
        }
    }
    Start-Sleep -Seconds 2
    $svc = Get-Service -Name $script:ServiceName -ErrorAction SilentlyContinue
    if (-not $svc -or $svc.Status -ne "Running") {
        throw "HeimdallAgent did not reach Running (Status=$(if ($svc) { $svc.Status } else { 'missing' }))"
    }
    Write-InstallState -Stage "service-running" -Detail "Status=Running"
    Write-InstallLog "Service Status=$($svc.Status)" -Level OK
}

function Try-BestEffortRecreateService {
    <#
      Phase 1 fallback when no LKG/staging: recreate + start whatever Heimdall.Agent.exe is on disk.
    #>
    $exe = Join-Path $InstallDir "Heimdall.Agent.exe"
    if (-not (Test-Path -LiteralPath $exe)) {
        Write-InstallLog "Best-effort recreate skipped — missing $exe" -Level WARN
        return $false
    }
    Write-InstallLog "Best-effort: recreating HeimdallAgent from on-disk bits at $InstallDir" -Level WARN
    try {
        $existing = Get-Service -Name $script:ServiceName -ErrorAction SilentlyContinue
        if (-not $existing) {
            New-HeimdallAgentService -ExePath $exe
        }
        Start-HeimdallAgentService
        Write-InstallLog "Best-effort recreate: service Running (no LKG available)" -Level WARN
        return $true
    }
    catch {
        Write-InstallLog "Best-effort recreate failed: $($_.Exception.Message)" -Level ERROR
        return $false
    }
}

function Test-HealWatchdogRegistered {
    try {
        $null = Get-ScheduledTask -TaskName $script:HealTaskName -ErrorAction Stop
        return $true
    }
    catch {
        return $false
    }
}

function Sync-HealWatchdogFiles {
    <#
      Copy heal + installer scripts into %ProgramData%\Heimdall\heal so the
      scheduled task does not depend on a disposable pack folder.
    #>
    $srcHeal = Join-Path $PSScriptRoot $script:HealScriptName
    $srcInstaller = Join-Path $PSScriptRoot "Install-WorkstationCollector.ps1"
    if (-not (Test-Path -LiteralPath $srcHeal)) {
        Write-InstallLog "Heal script missing next to installer: $srcHeal — cannot sync heal files" -Level WARN
        return $false
    }
    if (-not (Test-Path -LiteralPath $srcInstaller)) {
        Write-InstallLog "Installer missing for heal sync: $srcInstaller" -Level WARN
        return $false
    }
    # Avoid copying onto ourselves when already running from ProgramData\heal.
    $destHeal = Join-Path $script:HealRoot $script:HealScriptName
    $runningFromHeal = $false
    try {
        $runningFromHeal = [string]::Equals(
            [IO.Path]::GetFullPath($PSScriptRoot).TrimEnd('\'),
            [IO.Path]::GetFullPath($script:HealRoot).TrimEnd('\'),
            [StringComparison]::OrdinalIgnoreCase)
    }
    catch { }
    if ($runningFromHeal) {
        Write-InstallLog "Already running from heal root — skip file sync" -Level INFO
        return (Test-Path -LiteralPath $destHeal)
    }

    Ensure-Dir $script:HealRoot
    Copy-Item -LiteralPath $srcHeal -Destination $destHeal -Force
    Copy-Item -LiteralPath $srcInstaller -Destination (Join-Path $script:HealRoot "Install-WorkstationCollector.ps1") -Force
    if (-not (Test-Path -LiteralPath $destHeal)) {
        Write-InstallLog "Heal sync failed — $destHeal missing after copy" -Level ERROR
        return $false
    }
    Write-InstallLog "Synced heal scripts → $($script:HealRoot)" -Level OK
    return $true
}

function Register-HealWatchdogTask {
    if (-not (Sync-HealWatchdogFiles)) {
        throw "Cannot register HeimdallAgentHeal — heal script sync failed"
    }
    $healPs1 = Join-Path $script:HealRoot $script:HealScriptName
    $psExe = Join-Path $env:SystemRoot "System32\WindowsPowerShell\v1.0\powershell.exe"
    if (-not (Test-Path -LiteralPath $psExe)) { $psExe = "powershell.exe" }

    $arg = "-NoProfile -ExecutionPolicy Bypass -File `"$healPs1`""
    $action = New-ScheduledTaskAction -Execute $psExe -Argument $arg

    $triggerStartup = New-ScheduledTaskTrigger -AtStartup
    try { $triggerStartup.Delay = "PT5M" } catch { }

    # Every 15 minutes; start one minute from now so first interval is soon after register.
    $triggerInterval = New-ScheduledTaskTrigger -Once -At ((Get-Date).AddMinutes(1))
    $triggerInterval.RepetitionInterval = (New-TimeSpan -Minutes 15)
    try {
        $triggerInterval.RepetitionDuration = [TimeSpan]::FromDays(3650)
    }
    catch {
        $triggerInterval.RepetitionDuration = [TimeSpan]::MaxValue
    }

    $principal = New-ScheduledTaskPrincipal -UserId "SYSTEM" -LogonType ServiceAccount -RunLevel Highest
    $settings = New-ScheduledTaskSettingsSet -AllowStartIfOnBatteries -DontStopIfGoingOnBatteries -StartWhenAvailable -ExecutionTimeLimit (New-TimeSpan -Hours 2)

    Register-ScheduledTask -TaskName $script:HealTaskName -Action $action -Trigger @($triggerStartup, $triggerInterval) -Principal $principal -Settings $settings -Force | Out-Null
    if (-not (Test-HealWatchdogRegistered)) {
        throw "Register-ScheduledTask reported success but task $($script:HealTaskName) not found"
    }
    Write-InstallLog "Registered scheduled task $($script:HealTaskName) (AtStartup+5m delay, every 15m)" -Level OK
}

function Unregister-HealWatchdogTask {
    if (-not (Test-HealWatchdogRegistered)) {
        Write-InstallLog "Heal task $($script:HealTaskName) not registered — nothing to remove" -Level INFO
        return
    }
    try {
        Unregister-ScheduledTask -TaskName $script:HealTaskName -Confirm:$false -ErrorAction Stop
        Write-InstallLog "Unregistered scheduled task $($script:HealTaskName)" -Level OK
    }
    catch {
        Write-InstallLog "Failed to unregister $($script:HealTaskName): $($_.Exception.Message)" -Level ERROR
        throw
    }
}

function Complete-HealWatchdogAfterInstall {
    <#
      After successful agent install:
      - EnableHealWatchdog => sync scripts + ensure task registered
      - else if task already present => refresh scripts only (silent update preserve)
      - else => leave disabled (do not silently uninstall add-on)
    #>
    if ($EnableHealWatchdog) {
        Write-InstallLog "EnableHealWatchdog requested — registering HeimdallAgentHeal" -Level STEP
        Register-HealWatchdogTask
        return
    }
    if (Test-HealWatchdogRegistered) {
        Write-InstallLog "Heal task already registered — refreshing heal scripts (not disabling)" -Level STEP
        [void](Sync-HealWatchdogFiles)
        return
    }
    Write-InstallLog "Heal watchdog add-on not selected (and not previously registered) — skipped" -Level INFO
}

function Assert-Administrator {
    $admin = $false
    try {
        $id = [Security.Principal.WindowsIdentity]::GetCurrent()
        $principal = New-Object Security.Principal.WindowsPrincipal($id)
        $admin = $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
    }
    catch { }
    if (-not $admin) {
        throw "Administrator rights required."
    }
}

function Invoke-HealOnlyMode {
    Write-Host ""
    Write-Host "================================================================"
    Write-Host "  Heimdall HealOnly (LKG restore)"
    Write-Host "================================================================"
    Write-Host ""
    Write-InstallLog "HealOnly mode — restore from LKG/staging" -Level STEP
    Write-InstallLog "LKG dir: $($script:LkgPath) (present=$(Test-LkgPresent)) staging=$(Test-LkgStagingPresent)"

    Assert-Administrator
    Acquire-InstallMutex

    if (Test-ExistingLockIsFresh) {
        throw "Another Heimdall install is in progress (durable install.lock). HealOnly deferred."
    }

    Write-InstallLock
    $script:StateStartedUtc = $null
    $script:ServiceRemoved = $false
    $script:RestoredFromLkg = $false
    Write-InstallState -Stage "acquired-lock" -Detail "HealOnly mutex + install.lock held"

    $ok = Restore-FromLkg -Reason "heal-watchdog"
    if (-not $ok) {
        throw "HealOnly LKG restore failed (no usable LKG/staging or restore error)"
    }
    Clear-InstallLock
    Write-InstallLog "HealOnly complete — install.lock cleared; install-state.json stage=rolled-back" -Level OK
    $script:ExitCode = 0
}

try {
    Ensure-Dir $script:LogRoot
    Ensure-Dir (Join-Path $env:ProgramData "Heimdall")
    $stamp = Get-Date -Format "yyyyMMdd-HHmmss"
    if ($HealOnly) {
        $script:LogPath = Join-Path $script:LogRoot "heal-restore-$stamp.log"
    }
    else {
        $script:LogPath = Join-Path $script:LogRoot "install-agent-$stamp.log"
    }

    Write-InstallLog "Log file: $($script:LogPath)"
    Write-InstallLog "User: $env:USERNAME  Machine: $env:COMPUTERNAME"

    if ($UnregisterHealWatchdog) {
        Write-Host ""
        Write-Host "================================================================"
        Write-Host "  Heimdall — unregister heal watchdog"
        Write-Host "================================================================"
        Write-Host ""
        Assert-Administrator
        Unregister-HealWatchdogTask
        Write-InstallLog "UnregisterHealWatchdog finished" -Level OK
        $script:ExitCode = 0
        exit $script:ExitCode
    }

    if ($HealOnly) {
        Invoke-HealOnlyMode
        exit $script:ExitCode
    }

    Write-Host ""
    Write-Host "================================================================"
    Write-Host "  Heimdall Client agent installer (resilient + LKG)"
    Write-Host "================================================================"
    Write-Host ""

    Write-InstallLog "ApiUrl=$ApiUrl MachineGroup=$MachineGroup InstallDir=$InstallDir"
    Write-InstallLog "Payload=$Payload"
    Write-InstallLog "EnableHealWatchdog=$EnableHealWatchdog"
    Write-InstallLog "LKG dir: $($script:LkgPath) (present=$(Test-LkgPresent))"

    Assert-Administrator

    $exePayload = Join-Path $Payload "Heimdall.Agent.exe"
    if (-not (Test-Path -LiteralPath $exePayload)) {
        throw "Payload not found: `"$exePayload`". This installer expects the Heimdall-Client pack (must include payload\)."
    }

    Acquire-InstallMutex

    if (Test-ExistingLockIsFresh) {
        throw "Another Heimdall install is in progress (durable install.lock). Retry later or clear stale lock under $($script:UpdateRoot)."
    }

    # Phase 2: heal incomplete prior install (stale lock / leftover stage) before starting a new attempt.
    if (Test-PriorInstallNeedsHeal) {
        Write-InstallLog "Prior incomplete install.lock/state detected — attempting LKG heal before new install" -Level WARN
        $healed = Restore-FromLkg -Reason "heal-before-install"
        if ($healed) {
            Clear-InstallLock
            Write-InstallLog "Pre-install LKG heal OK; continuing with new install (state left as rolled-back until this attempt advances)" -Level OK
        }
        else {
            Write-InstallLog "Pre-install LKG heal unavailable/failed — continuing; catch path will try again if this attempt fails" -Level WARN
        }
    }

    Write-InstallLock
    $script:StateStartedUtc = $null
    $script:ServiceRemoved = $false
    $script:RestoredFromLkg = $false
    Write-InstallState -Stage "acquired-lock" -Detail "Mutex + install.lock held"

    Write-InstallLog "Probe API health (best-effort)" -Level STEP
    $health = $ApiUrl.TrimEnd("/") + "/api/health"
    try {
        $null = & curl.exe -sS -m 10 $health 2>&1
        if ($LASTEXITCODE -eq 0) {
            Write-InstallLog "API reachable: $health" -Level OK
        }
        else {
            Write-InstallLog "API not reachable yet at $health — install continues; fix URL/firewall if heartbeats fail." -Level WARN
        }
    }
    catch {
        Write-InstallLog "API health probe error: $($_.Exception.Message)" -Level WARN
    }

    # Backup current bits BEFORE sc delete / robocopy. Committed lkg\ is not replaced until success.
    Backup-CurrentInstallToStaging

    Write-InstallLog "Stop/remove existing HeimdallAgent service" -Level STEP
    Remove-HeimdallAgentService

    Ensure-Dir $InstallDir
    Write-InstallLog "Copy payload to install directory" -Level STEP
    $rc = 0
    & robocopy.exe $Payload $InstallDir /E /NFL /NDL /NJH /NJS /nc /ns /np | Out-Null
    $rc = $LASTEXITCODE
    if ($rc -ge 8) {
        throw "robocopy failed with exit $rc"
    }
    $exeInstalled = Join-Path $InstallDir "Heimdall.Agent.exe"
    if (-not (Test-Path -LiteralPath $exeInstalled)) {
        throw "Heimdall.Agent.exe missing after copy"
    }
    Write-InstallState -Stage "copied" -Detail "robocopy exit $rc"
    Write-InstallLog "Copied payload to $InstallDir" -Level OK

    Write-InstallLog "Write appsettings.json" -Level STEP
    $queuePath = Join-Path $env:ProgramData "Heimdall\queue.db"
    $settings = [ordered]@{
        Heimdall = [ordered]@{
            ApiBaseUrl   = $ApiUrl.TrimEnd("/")
            ApiKey       = $ApiKey
            MachineGroup = $MachineGroup
            QueuePath    = $queuePath
        }
        Logging  = [ordered]@{
            LogLevel = [ordered]@{
                Default                      = "Information"
                "Microsoft.Hosting.Lifetime" = "Information"
            }
        }
    }
    $settingsPath = Join-Path $InstallDir "appsettings.json"
    $settingsJson = $settings | ConvertTo-Json -Depth 6
    $utf8 = New-Object System.Text.UTF8Encoding $true
    [System.IO.File]::WriteAllText($settingsPath, $settingsJson, $utf8)
    # Read-back verify
    $readBack = Get-Content -LiteralPath $settingsPath -Raw | ConvertFrom-Json
    if (-not $readBack.Heimdall.ApiBaseUrl) {
        throw "appsettings.json read-back failed (ApiBaseUrl missing)"
    }
    Write-InstallState -Stage "configured" -Detail "appsettings.json written"
    Write-InstallLog "Wrote $settingsPath" -Level OK
    Write-InstallLog "QueuePath=$queuePath"

    New-HeimdallAgentService -ExePath $exeInstalled
    Start-HeimdallAgentService

    Write-InstallState -Stage "committed" -Detail "Install succeeded"
    Commit-LkgFromInstallDir
    Clear-InstallLockAndState

    # Phase 3 add-on (opt-in) / preserve existing registration across silent updates
    try {
        Complete-HealWatchdogAfterInstall
    }
    catch {
        Write-InstallLog "Heal watchdog post-install step failed: $($_.Exception.Message)" -Level WARN
    }

    Write-Host ""
    Write-Host "================================================================"
    Write-Host "  SUCCESS — Heimdall client agent installed"
    Write-Host "================================================================"
    Write-InstallLog "API:     $ApiUrl" -Level OK
    Write-InstallLog "Service: HeimdallAgent" -Level OK
    Write-InstallLog "Host:    $env:COMPUTERNAME (dashboard Machines after first heartbeat)" -Level OK
    Write-InstallLog "Group:   $MachineGroup" -Level OK
    Write-InstallLog "Heal:    $(if (Test-HealWatchdogRegistered) { 'HeimdallAgentHeal registered' } else { 'not registered' })" -Level OK
    Write-InstallLog "Log:     $($script:LogPath)" -Level OK
    $script:ExitCode = 0
}
catch {
    $msg = $_.Exception.Message
    Write-InstallLog $msg -Level ERROR
    if (-not $HealOnly -and -not $UnregisterHealWatchdog) {
        try { Write-InstallState -Stage "failed" -Detail $msg } catch { }

        $recovered = $false
        # Only heal after the service was removed (destructive window). Prefer LKG/staging over on-disk bits.
        if ($script:ServiceRemoved) {
            $recovered = Restore-FromLkg -Reason "install-failed"
            if ($recovered) {
                Clear-InstallLock
                Write-InstallLog "Install failed but LKG rollback succeeded (service Running). install.lock cleared; install-state.json stage=rolled-back for Launch Control." -Level WARN
            }
            else {
                $recovered = Try-BestEffortRecreateService
                if ($recovered) {
                    Write-InstallLog "Install failed; LKG unavailable — best-effort on-disk recreate succeeded. Lock left for operator visibility." -Level WARN
                }
                else {
                    Write-InstallLog "Install failed; LKG restore and best-effort recreate both failed. Machine may show NOT INSTALLED." -Level ERROR
                }
            }
        }

        Write-Host ""
        Write-Host "================================================================"
        Write-Host "  FAILURE — Heimdall client agent install did not complete"
        Write-Host "================================================================"
        if ($script:LogPath) {
            Write-Host "Send this log for analysis:"
            Write-Host "  $($script:LogPath)"
        }
        if ($script:RestoredFromLkg) {
            Write-Host "Agent was rolled back to last-known-good (see install-state.json stage=rolled-back)."
        }
    }
    $script:ExitCode = 1
}
finally {
    Release-InstallMutex
    Write-Host ""
    Write-Host "Full log path:"
    if ($script:LogPath) { Write-Host "  $($script:LogPath)" } else { Write-Host "  (none)" }
}

exit $script:ExitCode
