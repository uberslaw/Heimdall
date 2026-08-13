#Requires -Version 5.1
<#
.SYNOPSIS
  HeimdallAgentHeal watchdog: recover crashed installs / stopped agent (Phase 3).

.DESCRIPTION
  Runs as SYSTEM via scheduled task HeimdallAgentHeal (opt-in at client install).
  - Service Stopped -> sc start (no idle gate)
  - Service missing + LKG -> restore immediately (no idle gate)
  - Stale lock / incomplete install + LKG -> restore only when idle (CPU and GPU < 20%)
    and no interactive Active session (service-missing path still restores)
  - No LKG + broken -> log only (Launch Control can surface)
  Destructive restore calls Install-WorkstationCollector.ps1 -HealOnly (same mutex/lock/LKG path).

  Logs: %ProgramData%\Heimdall\logs\heal-*.log
#>
[CmdletBinding()]
param()

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

$script:ServiceName = "HeimdallAgent"
$script:TaskName = "HeimdallAgentHeal"
$script:LockStaleMinutes = 30
$script:StartupGraceMinutes = 5
$script:IdleCpuThreshold = 20
$script:IdleGpuThreshold = 20
$script:IdleSampleCount = 3
$script:IdleSampleDelaySec = 15
$script:LogPath = $null

$script:HealRoot = Split-Path -Parent $MyInvocation.MyCommand.Path
$script:UpdateRoot = Join-Path $env:ProgramData "Heimdall\update"
$script:LockPath = Join-Path $script:UpdateRoot "install.lock"
$script:StatePath = Join-Path $script:UpdateRoot "install-state.json"
$script:LkgPath = Join-Path $script:UpdateRoot "lkg"
$script:LkgStagingPath = Join-Path $script:UpdateRoot "lkg.staging"
$script:LogRoot = Join-Path $env:ProgramData "Heimdall\logs"
$script:InstallDir = Join-Path ${env:ProgramFiles} "Heimdall\Agent"
$script:HealInstaller = Join-Path $script:HealRoot "Install-WorkstationCollector.ps1"

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

function Test-LkgPresent {
    return (Test-Path -LiteralPath (Join-Path $script:LkgPath "Heimdall.Agent.exe"))
}

function Test-LkgStagingPresent {
    return (Test-Path -LiteralPath (Join-Path $script:LkgStagingPath "Heimdall.Agent.exe"))
}

function Test-AnyLkgAvailable {
    return (Test-LkgPresent) -or (Test-LkgStagingPresent)
}

function Test-LockOwnerAlive {
    param([int]$OwnerPid)
    if ($OwnerPid -le 0) { return $false }
    try {
        $null = Get-Process -Id $OwnerPid -ErrorAction Stop
        return $true
    }
    catch {
        return $false
    }
}

function Get-InstallLockInfo {
    if (-not (Test-Path -LiteralPath $script:LockPath)) {
        return [pscustomobject]@{ Present = $false; Fresh = $false; OwnerAlive = $false; OwnerPid = 0; AgeMinutes = 0 }
    }
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
    catch { }

    $ownerAlive = Test-LockOwnerAlive -OwnerPid $ownerPid
    $ageMinutes = 0
    if ($startedUtc) {
        $ageMinutes = [Math]::Round(((Get-Date).ToUniversalTime() - $startedUtc.UtcDateTime).TotalMinutes, 1)
    }
    else {
        try {
            $ageMinutes = [Math]::Round(((Get-Date) - (Get-Item -LiteralPath $script:LockPath).LastWriteTime).TotalMinutes, 1)
        }
        catch { }
    }

    $fresh = $false
    if ($ownerAlive) {
        $fresh = $true
    }
    elseif ($startedUtc -and $ageMinutes -lt $script:LockStaleMinutes) {
        # Dead owner but young lock — treat as contended (installer may still be settling / AV).
        $fresh = $true
    }

    return [pscustomobject]@{
        Present    = $true
        Fresh      = $fresh
        OwnerAlive = $ownerAlive
        OwnerPid   = $ownerPid
        AgeMinutes = $ageMinutes
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
        return $true
    }
    catch {
        return $true
    }
}

function Test-WithinStartupGrace {
    try {
        $boot = (Get-CimInstance -ClassName Win32_OperatingSystem -ErrorAction Stop).LastBootUpTime
        $mins = ((Get-Date) - [DateTime]$boot).TotalMinutes
        return ($mins -lt $script:StartupGraceMinutes)
    }
    catch {
        return $false
    }
}

function Test-InteractiveSessionActive {
    # Prefer WTS (works from session 0 / SYSTEM). Fallback: query.exe user.
    try {
        if (-not ("HeimdallHealWts.Native" -as [type])) {
            Add-Type -TypeDefinition @"
using System;
using System.Runtime.InteropServices;
namespace HeimdallHealWts {
  public static class Native {
    public enum WTS_CONNECTSTATE_CLASS {
      WTSActive = 0, WTSConnected = 1, WTSConnectQuery = 2, WTSShadow = 3,
      WTSDisconnected = 4, WTSIdle = 5, WTSListen = 6, WTSReset = 7, WTSDown = 8, WTSInit = 9
    }
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    public struct WTS_SESSION_INFO {
      public int SessionId;
      public string pWinStationName;
      public WTS_CONNECTSTATE_CLASS State;
    }
    [DllImport("wtsapi32.dll", EntryPoint = "WTSEnumerateSessionsW", SetLastError = true)]
    public static extern bool WTSEnumerateSessions(IntPtr hServer, int Reserved, int Version, out IntPtr ppSessionInfo, out int pCount);
    [DllImport("wtsapi32.dll")]
    public static extern void WTSFreeMemory(IntPtr pMemory);
  }
}
"@
        }
        $ptr = [IntPtr]::Zero
        $count = 0
        if ([HeimdallHealWts.Native]::WTSEnumerateSessions([IntPtr]::Zero, 0, 1, [ref]$ptr, [ref]$count) -and $ptr -ne [IntPtr]::Zero) {
            try {
                $size = [System.Runtime.InteropServices.Marshal]::SizeOf([type][HeimdallHealWts.Native+WTS_SESSION_INFO])
                for ($i = 0; $i -lt $count; $i++) {
                    $info = [System.Runtime.InteropServices.Marshal]::PtrToStructure(
                        [IntPtr]::Add($ptr, $i * $size),
                        [type][HeimdallHealWts.Native+WTS_SESSION_INFO])
                    if ($info.SessionId -eq 0) { continue }
                    if ($info.State -eq [HeimdallHealWts.Native+WTS_CONNECTSTATE_CLASS]::WTSActive) {
                        return $true
                    }
                }
            }
            finally {
                [HeimdallHealWts.Native]::WTSFreeMemory($ptr)
            }
            return $false
        }
    }
    catch { }

    try {
        $psi = New-Object System.Diagnostics.ProcessStartInfo
        $psi.FileName = "query.exe"
        $psi.Arguments = "user"
        $psi.RedirectStandardOutput = $true
        $psi.RedirectStandardError = $true
        $psi.UseShellExecute = $false
        $psi.CreateNoWindow = $true
        $p = [System.Diagnostics.Process]::Start($psi)
        $stdout = $p.StandardOutput.ReadToEnd()
        $stderr = $p.StandardError.ReadToEnd()
        $p.WaitForExit(8000) | Out-Null
        $text = "$stdout`n$stderr"
        if ($text -match '(?i)\sActive\s') {
            return $true
        }
    }
    catch { }
    return $false
}

function Get-CpuUtilizationPercent {
    try {
        $sample = Get-Counter -Counter '\Processor(_Total)\% Processor Time' -SampleInterval 1 -MaxSamples 1 -ErrorAction Stop
        $val = ($sample.CounterSamples | Select-Object -First 1).CookedValue
        if ($null -eq $val) { return 0 }
        return [double]$val
    }
    catch {
        return 0
    }
}

function Get-GpuUtilizationPercent {
    try {
        $sample = Get-Counter -Counter '\GPU Engine(*)\Utilization Percentage' -ErrorAction Stop
        $vals = @($sample.CounterSamples | ForEach-Object { [double]$_.CookedValue })
        if ($vals.Count -eq 0) { return 0 }
        return ($vals | Measure-Object -Maximum).Maximum
    }
    catch {
        # Counters unavailable (VM / driver) — treat as idle for gate purposes.
        return 0
    }
}

function Test-SystemIdleForDestructiveHeal {
    Write-HealLog "Idle gate: need CPU < $($script:IdleCpuThreshold)% and GPU < $($script:IdleGpuThreshold)% on $($script:IdleSampleCount) samples (~$($script:IdleSampleDelaySec)s apart)" -Level STEP
    for ($i = 1; $i -le $script:IdleSampleCount; $i++) {
        if ($i -gt 1) {
            Start-Sleep -Seconds $script:IdleSampleDelaySec
        }
        $cpu = Get-CpuUtilizationPercent
        $gpu = Get-GpuUtilizationPercent
        Write-HealLog ("Idle sample {0}/{1}: CPU={2:0.0}% GPU={3:0.0}%" -f $i, $script:IdleSampleCount, $cpu, $gpu)
        if ($cpu -ge $script:IdleCpuThreshold -or $gpu -ge $script:IdleGpuThreshold) {
            Write-HealLog "Idle gate FAILED (busy) — skipping destructive heal this cycle" -Level WARN
            return $false
        }
    }
    Write-HealLog "Idle gate OK" -Level OK
    return $true
}

function Start-AgentServiceBestEffort {
    Write-HealLog "Starting $($script:ServiceName) (no idle gate)..." -Level STEP
    $out = & sc.exe start $script:ServiceName 2>&1
    $out | ForEach-Object { Write-HealLog "  $_" }
    Start-Sleep -Seconds 2
    $svc = Get-Service -Name $script:ServiceName -ErrorAction SilentlyContinue
    if ($svc -and $svc.Status -eq "Running") {
        Write-HealLog "Service Running after sc start" -Level OK
        return $true
    }
    Write-HealLog "Service not Running after sc start (Status=$(if ($svc) { $svc.Status } else { 'missing' }))" -Level ERROR
    return $false
}

function Invoke-HealOnlyRestore {
    param([string]$Reason)
    if (-not (Test-Path -LiteralPath $script:HealInstaller)) {
        Write-HealLog "Heal installer missing: $($script:HealInstaller) — cannot restore LKG" -Level ERROR
        return $false
    }
    Write-HealLog "Invoking HealOnly restore ($Reason) via $($script:HealInstaller)" -Level STEP
    $ps = Join-Path $env:SystemRoot "System32\WindowsPowerShell\v1.0\powershell.exe"
    if (-not (Test-Path -LiteralPath $ps)) { $ps = "powershell.exe" }
    $args = @(
        "-NoProfile"
        "-ExecutionPolicy", "Bypass"
        "-File", $script:HealInstaller
        "-HealOnly"
    )
    $p = Start-Process -FilePath $ps -ArgumentList $args -Wait -PassThru -WindowStyle Hidden
    $code = if ($p) { [int]$p.ExitCode } else { -1 }
    if ($code -eq 0) {
        Write-HealLog "HealOnly restore succeeded (exit 0)" -Level OK
        return $true
    }
    Write-HealLog "HealOnly restore failed (exit $code)" -Level ERROR
    return $false
}

# --- main ---
try {
    Ensure-Dir $script:LogRoot
    Ensure-Dir (Join-Path $env:ProgramData "Heimdall")
    $stamp = Get-Date -Format "yyyyMMdd-HHmmss"
    $script:LogPath = Join-Path $script:LogRoot "heal-$stamp.log"

    Write-HealLog "HeimdallAgentHeal start (task=$($script:TaskName))" -Level STEP
    Write-HealLog "Log: $($script:LogPath)"
    Write-HealLog "HealRoot=$($script:HealRoot) LKG=$(Test-LkgPresent) staging=$(Test-LkgStagingPresent)"

    $lock = Get-InstallLockInfo
    if ($lock.Present) {
        Write-HealLog "install.lock present age=$($lock.AgeMinutes)m pid=$($lock.OwnerPid) alive=$($lock.OwnerAlive) fresh=$($lock.Fresh)"
    }

    $svc = Get-Service -Name $script:ServiceName -ErrorAction SilentlyContinue
    $svcStatus = if ($svc) { [string]$svc.Status } else { "MISSING" }
    Write-HealLog "Service status: $svcStatus"

    # Live installer holds lock — never interfere (including start), except we still prefer restore when service is missing and lock is stale.
    if ($lock.Fresh -and $lock.OwnerAlive) {
        Write-HealLog "Live install.lock holder — skipping heal this cycle" -Level WARN
        exit 0
    }

    # 1) Service missing + LKG -> restore immediately (prefer restore over idle/interactive gates)
    if (-not $svc) {
        if (Test-AnyLkgAvailable) {
            if ($lock.Fresh -and -not $lock.OwnerAlive) {
                Write-HealLog "Service missing; young lock with dead owner — proceeding with restore (prefer restore)" -Level WARN
            }
            Write-HealLog "Service MISSING + LKG available — restoring immediately (no idle gate)" -Level WARN
            $ok = Invoke-HealOnlyRestore -Reason "service-missing"
            exit $(if ($ok) { 0 } else { 1 })
        }
        Write-HealLog "BROKEN: HeimdallAgent service missing and no LKG/staging — cannot invent bits. Use Launch Control / reinstall from pack. See this log." -Level ERROR
        exit 0
    }

    # 2) Service Stopped -> start (no idle gate)
    if ($svc.Status -eq "Stopped") {
        if ($lock.Fresh) {
            Write-HealLog "Service Stopped but install.lock still fresh — skipping sc start this cycle" -Level WARN
            exit 0
        }
        $ok = Start-AgentServiceBestEffort
        exit $(if ($ok) { 0 } else { 1 })
    }

    if ($svc.Status -eq "Running") {
        # 3) Incomplete / stale lock with service running — may still need LKG restore if mid-failure state
        if (-not (Test-PriorInstallNeedsHeal)) {
            Write-HealLog "Healthy: service Running and no incomplete install state" -Level OK
            exit 0
        }

        if (-not (Test-AnyLkgAvailable)) {
            Write-HealLog "Incomplete install state but no LKG — logging only (service still Running). Launch Control can inspect install-state.json." -Level WARN
            exit 0
        }

        if (Test-WithinStartupGrace) {
            Write-HealLog "Within $($script:StartupGraceMinutes)m of boot — deferring destructive heal" -Level INFO
            exit 0
        }

        if (Test-InteractiveSessionActive) {
            Write-HealLog "Interactive session Active — skipping destructive heal (service is Running)" -Level INFO
            exit 0
        }

        if ($lock.Fresh) {
            Write-HealLog "install.lock still considered fresh — skipping destructive heal" -Level WARN
            exit 0
        }

        if (-not (Test-SystemIdleForDestructiveHeal)) {
            exit 0
        }

        Write-HealLog "Stale/incomplete install + LKG + idle — invoking HealOnly restore" -Level WARN
        $ok = Invoke-HealOnlyRestore -Reason "stale-or-incomplete"
        exit $(if ($ok) { 0 } else { 1 })
    }

    # Other statuses (StartPending, etc.)
    Write-HealLog "Service status '$($svc.Status)' — no action this cycle" -Level INFO
    exit 0
}
catch {
    try { Write-HealLog $_.Exception.Message -Level ERROR } catch { }
    exit 1
}
