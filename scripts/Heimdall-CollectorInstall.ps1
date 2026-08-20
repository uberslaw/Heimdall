#Requires -Version 5.1
<#
.SYNOPSIS
  Shared collector install launch, default ApiUrl, and version compare bootstrap.

.DESCRIPTION
  Dot-source from Install-Client.ps1 and Heimdall-LaunchControl.ps1.
  ASCII-only; PS 5.1; UTF-8 BOM.
#>

$script:HeimdallDefaultCollectorApiUrl = "http://BNELT5CG5152D8R:5080"

function Resolve-HeimdallFilesystemPath {
    param([Parameter(Mandatory)][string]$Path)
    if ([string]::IsNullOrWhiteSpace($Path)) { return $Path }
    if ($Path -match '^[^:]+::(.+)$') {
        return $Matches[1]
    }
    try {
        if (Test-Path -LiteralPath $Path) {
            return (Resolve-Path -LiteralPath $Path).ProviderPath
        }
    }
    catch { }
    return $Path
}

function Import-HeimdallVersionCompare {
    param([Parameter(Mandatory)][string]$ScriptDir)
    if (Get-Command Test-HeimdallProductVersionMatch -ErrorAction SilentlyContinue) {
        return
    }
    $helperPath = Join-Path $ScriptDir "Heimdall-VersionCompare.ps1"
    if (Test-Path -LiteralPath $helperPath) {
        . $helperPath
        foreach ($fn in @(
                'Get-HeimdallCoreProductVersion',
                'Get-HeimdallSimpleClientVersion',
                'Test-HeimdallProductVersionMatch',
                'Format-HeimdallVersionCompareLine'
            )) {
            $cmd = Get-Command $fn -ErrorAction SilentlyContinue
            if ($cmd) {
                New-Item -Path "Function:\script:$fn" -Value $cmd.ScriptBlock -Force | Out-Null
            }
        }
    }
    if (-not (Get-Command Test-HeimdallProductVersionMatch -ErrorAction SilentlyContinue)) {
        Write-HeimdallVersionCompareInlineFallback
    }
}

function Write-HeimdallVersionCompareInlineFallback {
    if (Get-Command Test-HeimdallProductVersionMatch -ErrorAction SilentlyContinue) {
        return
    }
    function Get-HeimdallCoreProductVersion {
        param([string]$Version)
        if ([string]::IsNullOrWhiteSpace($Version)) { return "" }
        $v = $Version.Trim()
        $plusIdx = $v.IndexOf('+')
        if ($plusIdx -ge 0) {
            $v = $v.Substring(0, $plusIdx)
        }
        return $v.Trim()
    }
    function Get-HeimdallSimpleClientVersion {
        param([string]$Version)
        $core = Get-HeimdallCoreProductVersion -Version $Version
        if ([string]::IsNullOrWhiteSpace($core)) { return $null }
        $n = 0
        if ([int]::TryParse($core, [ref]$n) -and $n -ge 0 -and ($core -match '^\d+$')) {
            return $n
        }
        return 1
    }
    function Test-HeimdallProductVersionMatch {
        param(
            [string]$VersionA,
            [string]$VersionB
        )
        $a = Get-HeimdallSimpleClientVersion -Version $VersionA
        $b = Get-HeimdallSimpleClientVersion -Version $VersionB
        if ($null -eq $a -or $null -eq $b) {
            return $true
        }
        return ($a -eq $b)
    }
    function Format-HeimdallVersionCompareLine {
        param(
            [string]$LabelA,
            [string]$VersionA,
            [string]$LabelB,
            [string]$VersionB
        )
        $coreA = Get-HeimdallCoreProductVersion -Version $VersionA
        $coreB = Get-HeimdallCoreProductVersion -Version $VersionB
        $simpleA = Get-HeimdallSimpleClientVersion -Version $VersionA
        $simpleB = Get-HeimdallSimpleClientVersion -Version $VersionB
        $match = Test-HeimdallProductVersionMatch -VersionA $VersionA -VersionB $VersionB
        if ($match) {
            if ($VersionA -ne $VersionB) {
                return "$LabelA=$VersionA | $LabelB=$VersionB | simple=$simpleA (match; legacy SemVer maps to 1)"
            }
            return "$LabelA=$VersionA | $LabelB=$VersionB | match"
        }
        return "$LabelA=$VersionA (core=$coreA simple=$simpleA) | $LabelB=$VersionB (core=$coreB simple=$simpleB) | MISMATCH"
    }
    foreach ($fn in @(
            'Get-HeimdallCoreProductVersion',
            'Get-HeimdallSimpleClientVersion',
            'Test-HeimdallProductVersionMatch',
            'Format-HeimdallVersionCompareLine'
        )) {
        $cmd = Get-Command $fn -ErrorAction SilentlyContinue
        if ($cmd) {
            New-Item -Path "Function:\script:$fn" -Value $cmd.ScriptBlock -Force | Out-Null
        }
    }
}

function Resolve-HeimdallDefaultCollectorApiUrl {
    param(
        [string]$LastInstallSettingsFile = $null,
        [string]$PackFolder = $null,
        [scriptblock]$Log = $null
    )

    # Pack-baked URL (Create client pack) wins for wizard / silent defaults on that pack.
    $packRoots = @()
    if (-not [string]::IsNullOrWhiteSpace($PackFolder)) { $packRoots += $PackFolder }
    if ($PSScriptRoot) { $packRoots += $PSScriptRoot }
    foreach ($root in $packRoots) {
        $packApi = Join-Path $root "pack-api.json"
        if (-not (Test-Path -LiteralPath $packApi)) { continue }
        try {
            $meta = Get-Content -LiteralPath $packApi -Raw -Encoding UTF8 | ConvertFrom-Json
            $u = $null
            try { $u = [string]$meta.apiBaseUrl } catch { $u = $null }
            if (-not [string]::IsNullOrWhiteSpace($u)) {
                $u = $u.Trim().TrimEnd("/")
                if ($Log) { & $Log "Prefill ApiUrl from pack-api.json: $u" "INFO" }
                return $u
            }
        }
        catch {
            if ($Log) { & $Log "Could not read pack-api.json: $($_.Exception.Message)" "WARN" }
        }
    }

    if ($LastInstallSettingsFile -and (Test-Path -LiteralPath $LastInstallSettingsFile)) {
        try {
            $raw = Get-Content -Raw -Path $LastInstallSettingsFile -Encoding UTF8
            if (-not [string]::IsNullOrWhiteSpace($raw)) {
                $last = $raw | ConvertFrom-Json
                if ($last -and $last.apiUrl) {
                    $u = [string]$last.apiUrl
                    $u = $u.Trim().TrimEnd('/')
                    if ($u) {
                        if ($Log) { & $Log "Prefill ApiUrl from last install: $u" "INFO" }
                        return $u
                    }
                }
            }
        }
        catch {
            if ($Log) { & $Log "Could not read last install settings: $($_.Exception.Message)" "WARN" }
        }
    }
    if ($Log) { & $Log "Prefill ApiUrl from default host: $($script:HeimdallDefaultCollectorApiUrl)" "INFO" }
    return $script:HeimdallDefaultCollectorApiUrl
}

function Invoke-HeimdallElevatedCollectorInstall {
    param(
        [Parameter(Mandatory)][string]$InstallerCmdPath,
        [Parameter(Mandatory)][string]$ApiUrl,
        [Parameter(Mandatory)][string]$ApiKey,
        [Parameter(Mandatory)][string]$MachineGroup,
        [Parameter(Mandatory)][string]$PayloadPath,
        [switch]$AlreadyElevated,
        [switch]$EnableHealWatchdog,
        [scriptblock]$PumpUi = $null,
        [scriptblock]$Log = $null
    )

    $installer = Resolve-HeimdallFilesystemPath -Path $InstallerCmdPath
    $payload = Resolve-HeimdallFilesystemPath -Path $PayloadPath
    $packDir = Resolve-HeimdallFilesystemPath -Path (Split-Path -Parent $InstallerCmdPath)

    $tempCmd = Join-Path $env:TEMP ("heimdall-install-" + (Get-Date -Format "yyyyMMdd-HHmmss") + "-" + ([guid]::NewGuid().ToString("N")).Substring(0, 6) + ".cmd")
    $captureLog = Join-Path $env:TEMP ("heimdall-install-capture-" + (Get-Date -Format "yyyyMMdd-HHmmss") + "-" + ([guid]::NewGuid().ToString("N")).Substring(0, 6) + ".log")

    $healArg = ""
    if ($EnableHealWatchdog) {
        $healArg = " -EnableHealWatchdog"
    }

    $batchLines = @(
        '@echo off'
        'setlocal EnableExtensions EnableDelayedExpansion'
        ('cd /d "{0}"' -f $packDir)
        'set HEIMDALL_SKIP_LAUNCH=1'
        'set HEIMDALL_NOPAUSE=1'
    )
    if ($EnableHealWatchdog) {
        $batchLines += 'set HEIMDALL_ENABLE_HEAL=1'
    }
    $batchLines += @(
        ('call "{0}" -ApiUrl "{1}" -ApiKey "{2}" -MachineGroup "{3}" -Payload "{4}"{5} > "{6}" 2>&1' -f $installer, $ApiUrl, $ApiKey, $MachineGroup, $payload, $healArg, $captureLog)
        'set EC=!ERRORLEVEL!'
        'exit /b !EC!'
    )
    $batchContent = ($batchLines -join "`r`n") + "`r`n"
    [System.IO.File]::WriteAllText($tempCmd, $batchContent, [System.Text.Encoding]::ASCII)
    if ($Log) { & $Log "Elevated install wrapper: $tempCmd" "INFO" }

    try {
        $startParams = @{
            FilePath         = $tempCmd
            WorkingDirectory = $packDir
            PassThru         = $true
        }
        if (-not $AlreadyElevated) {
            $startParams['Verb'] = 'RunAs'
        }
        else {
            $startParams['WindowStyle'] = 'Hidden'
        }
        if ($Log) {
            $elevNote = if ($AlreadyElevated) { "already elevated (console captured to log)" } else { "RunAs UAC" }
            & $Log "Starting installer via temp wrapper ($elevNote)" "INFO"
            & $Log "Service install capture: $captureLog" "INFO"
        }
        $proc = Start-Process @startParams
        while ($proc -and -not $proc.HasExited) {
            if ($PumpUi) {
                & $PumpUi
            }
            else {
                try {
                    [System.Windows.Forms.Application]::DoEvents()
                }
                catch { }
            }
            Start-Sleep -Milliseconds 150
        }
        $exitCode = -1
        if ($proc) {
            $proc.Refresh()
            $exitCode = [int]$proc.ExitCode
        }
        if (Test-Path -LiteralPath $captureLog) {
            try {
                $captureLines = @(Get-Content -LiteralPath $captureLog -ErrorAction Stop)
                if ($Log -and $captureLines.Count -gt 0) {
                    $capLevel = if ($exitCode -eq 0) { "INFO" } else { "ERROR" }
                    & $Log "Service install console output:" $capLevel
                    foreach ($line in $captureLines) {
                        & $Log "  install> $line" $capLevel
                    }
                }
            }
            catch {
                if ($Log) { & $Log "Could not read service install capture: $($_.Exception.Message)" "WARN" }
            }
        }
        elseif ($Log -and $exitCode -ne 0) {
            & $Log "Service install produced no console capture at $captureLog" "WARN"
        }
        return $exitCode
    }
    finally {
        Remove-Item -LiteralPath $tempCmd -Force -ErrorAction SilentlyContinue
        Remove-Item -LiteralPath $captureLog -Force -ErrorAction SilentlyContinue
    }
}

function Get-HeimdallInstallAgentLogTail {
    param(
        [int]$LineCount = 30,
        [string]$LogRoot = (Join-Path $env:ProgramData "Heimdall\logs")
    )
    if (-not (Test-Path -LiteralPath $LogRoot)) {
        return $null
    }
    $logFiles = @(
        Get-ChildItem -LiteralPath $LogRoot -Filter "install-agent-*.log" -ErrorAction SilentlyContinue
        Get-ChildItem -LiteralPath $LogRoot -Filter "install-workstation-collector-*.log" -ErrorAction SilentlyContinue
    )
    $latest = $logFiles |
        Sort-Object LastWriteTime -Descending |
        Select-Object -First 1
    if (-not $latest) {
        return $null
    }
    try {
        $all = @(Get-Content -LiteralPath $latest.FullName -ErrorAction Stop)
        if ($all.Count -le $LineCount) {
            $tail = $all
        }
        else {
            $tail = $all[($all.Count - $LineCount)..($all.Count - 1)]
        }
        return [pscustomobject]@{
            Path  = $latest.FullName
            Lines = $tail
            Text  = ($tail -join [Environment]::NewLine)
        }
    }
    catch {
        return [pscustomobject]@{
            Path  = $latest.FullName
            Lines = @("Could not read log: $($_.Exception.Message)")
            Text  = "Could not read log: $($_.Exception.Message)"
        }
    }
}

function Get-HeimdallInstallWorkstationCollectorLogTail {
    param(
        [int]$LineCount = 30,
        [string]$LogRoot = (Join-Path $env:ProgramData "Heimdall\logs")
    )
    return Get-HeimdallInstallAgentLogTail -LineCount $LineCount -LogRoot $LogRoot
}

function Resolve-HeimdallProductVersionExpected {
    param(
        [Parameter(Mandatory)][string]$ScriptDir,
        [string]$RepoRoot = $null,
        # Legacy integer when no VERSION.json (pre-integer packs map to 1 in VersionCompare).
        [string]$Fallback = "1"
    )
    $candidates = @(
        (Join-Path $ScriptDir "VERSION.json")
    )
    if (-not [string]::IsNullOrWhiteSpace($RepoRoot)) {
        $candidates += (Join-Path $RepoRoot "dist\Heimdall-Client\VERSION.json")
        $candidates += (Join-Path $RepoRoot "dist\workstation-collector\VERSION.json")
    }
    foreach ($c in $candidates) {
        if (-not (Test-Path -LiteralPath $c)) { continue }
        try {
            $obj = Get-Content -Raw -LiteralPath $c | ConvertFrom-Json
            $pv = [string]$obj.productVersion
            if (-not [string]::IsNullOrWhiteSpace($pv)) {
                return $pv.Trim()
            }
        }
        catch { }
    }
    return $Fallback
}

function Test-HeimdallBinaryContainsAscii {
    <#
    Best-effort capability fingerprint: search a binary for a marker string
    as ASCII and as UTF-16LE (common for .NET metadata / user strings).
    Caps read size for speed.
    #>
    param(
        [Parameter(Mandatory)][string]$Path,
        [Parameter(Mandatory)][string]$Needle,
        [int]$MaxBytes = 12000000
    )
    if (-not (Test-Path -LiteralPath $Path)) { return $false }
    if ([string]::IsNullOrEmpty($Needle)) { return $false }
    try {
        $fs = [System.IO.File]::Open($Path, [System.IO.FileMode]::Open, [System.IO.FileAccess]::Read, [System.IO.FileShare]::ReadWrite)
        try {
            $len = [Math]::Min([int64]$fs.Length, [int64]$MaxBytes)
            if ($len -le 0) { return $false }
            $buf = New-Object byte[] $len
            $read = 0
            while ($read -lt $len) {
                $n = $fs.Read($buf, $read, [int]($len - $read))
                if ($n -le 0) { break }
                $read += $n
            }
            $ascii = [System.Text.Encoding]::ASCII.GetString($buf, 0, $read)
            if ($ascii.IndexOf($Needle, [StringComparison]::Ordinal) -ge 0) { return $true }
            $utf16 = [System.Text.Encoding]::Unicode.GetString($buf, 0, $read)
            return $utf16.IndexOf($Needle, [StringComparison]::Ordinal) -ge 0
        }
        finally {
            $fs.Dispose()
        }
    }
    catch {
        return $false
    }
}

function Get-HeimdallClientVersionMilestoneNotes {
    <#
    Known simple-integer client milestones (from VersionCompare + field history).
    Used by the install wizard to explain what to expect / probe for v5+.
    #>
    param([int]$SimpleVersion)
    switch ($SimpleVersion) {
        { $_ -le 1 } {
            return @(
                "v1 / legacy SemVer (e.g. 0.1.0): early POC agent; treated as simple version 1.",
                "Expect: Heimdall.Agent.exe under Program Files\Heimdall\Agent.",
                "Do NOT expect: UpdateClient silent deploy, disk usage scan poll."
            )
        }
        2 {
            return @(
                "v2: early integer ProductVersion packs (before silent UpdateClient).",
                "Expect: integer ProductVersion on Heimdall.Agent.exe.",
                "Do NOT expect: UpdateClient (needs v3+)."
            )
        }
        3 {
            return @(
                "v3: first UpdateClient-capable build (Fleet silent Deploy).",
                "Expect: DLL marker UpdateClient / ClientUpdateHelper; bootstrap via Install.lnk no longer required after this.",
                "Do NOT expect: on-demand disk usage scan (needs v6+)."
            )
        }
        4 {
            return @(
                "v4: post-UpdateClient integer pack (no separate VersionCompare gate).",
                "Expect: same as v3 capability set (UpdateClient yes; disk scan no).",
                "Corroborate: ProductVersion=4 on exe; UpdateClient marker present; DiskUsageScanner absent."
            )
        }
        5 {
            return @(
                "v5: last common field build before disk-scan pickup (e.g. hosts stuck Queued on scans).",
                "Expect: UpdateClient yes; TuflowLauncher folder often present on modelling packs.",
                "Do NOT expect: DiskUsageScanner / GET disk-usage pending poll (added in v6).",
                "Corroborate: ProductVersion=5; UpdateClient marker YES; DiskUsageScanner marker NO."
            )
        }
        6 {
            return @(
                "v6: first disk usage scan agent (MinDiskUsageScanVersion).",
                "Expect: DiskUsageScanner + disk-usage API client methods in DLL; UpdateClient still present.",
                "Corroborate: ProductVersion=6+; DiskUsageScanner marker YES."
            )
        }
        default {
            if ($SimpleVersion -ge 6) {
                return @(
                    ("v{0}: integer pack at or after disk-scan baseline (v6+)." -f $SimpleVersion),
                    "Expect: UpdateClient + DiskUsageScanner capability markers in Heimdall.Agent.dll.",
                    "Also common: TuflowLauncher\TuflowLauncher.exe beside the agent; fleet snapshot posting."
                )
            }
            return @(("v{0}: no dedicated milestone notes; trust ProductVersion on the exe." -f $SimpleVersion))
        }
    }
}

function Get-HeimdallClientVersionProbe {
    <#
    Probe installed agent vs this pack's productVersion. Lists every path checked.
    Primary version = Win32 ProductVersion on Heimdall.Agent.exe (matches heartbeat AgentVersion).
    Capability markers in Heimdall.Agent.dll corroborate v3 (UpdateClient) and v6 (DiskUsageScanner).
    #>
    param(
        [string]$AgentInstallDir = (Join-Path ${env:ProgramFiles} "Heimdall\Agent"),
        [string]$PackScriptDir = $null
    )

    $checks = @()
    $addCheck = {
        param([string]$Path, [string]$What, [string]$Result, [string]$Detail = "")
        $script:__hdCheckBuf += ,([pscustomobject]@{
                Path   = $Path
                What   = $What
                Result = $Result
                Detail = $Detail
            })
    }
    $script:__hdCheckBuf = @()

    $exe = Join-Path $AgentInstallDir "Heimdall.Agent.exe"
    $dll = Join-Path $AgentInstallDir "Heimdall.Agent.dll"
    $settings = Join-Path $AgentInstallDir "appsettings.json"
    $tuflow = Join-Path $AgentInstallDir "TuflowLauncher\TuflowLauncher.exe"
    $dataRoot = Join-Path $env:ProgramData "Heimdall"
    $logRoot = Join-Path $dataRoot "logs"

    $installed = $false
    $productVersion = $null
    $fileVersion = $null
    $buildDateTime = $null
    $simple = $null
    $hasUpdateClient = $null
    $hasDiskUsageScan = $null
    $hasTuflowLauncher = $false
    $serviceStatus = $null

    if (Test-Path -LiteralPath $exe) {
        try {
            $vi = [System.Diagnostics.FileVersionInfo]::GetVersionInfo($exe)
            $fi = Get-Item -LiteralPath $exe
            $installed = $true
            $productVersion = if ($vi.ProductVersion) { $vi.ProductVersion.Trim() } else { $null }
            $fileVersion = if ($vi.FileVersion) { $vi.FileVersion.Trim() } else { $null }
            $buildDateTime = $fi.LastWriteTime.ToString("yyyy-MM-dd HH:mm:ss")
            if (Get-Command Get-HeimdallSimpleClientVersion -ErrorAction SilentlyContinue) {
                $simple = Get-HeimdallSimpleClientVersion -Version $productVersion
            }
            & $addCheck $exe "ProductVersion (primary)" $(if ($productVersion) { "OK" } else { "MISSING" }) `
            ("version=" + $(if ($productVersion) { $productVersion } else { "?" }) + "; LastWriteTime=" + $buildDateTime)
            if ($fileVersion -and $fileVersion -ne $productVersion) {
                & $addCheck $exe "FileVersion" "OK" $fileVersion
            }
        }
        catch {
            & $addCheck $exe "ProductVersion (primary)" "ERROR" $_.Exception.Message
        }
    }
    else {
        & $addCheck $exe "Heimdall.Agent.exe present" "MISSING" "No installed agent at default path"
    }

    if (Test-Path -LiteralPath $dll) {
        try {
            $dllVi = [System.Diagnostics.FileVersionInfo]::GetVersionInfo($dll)
            $dllPv = if ($dllVi.ProductVersion) { $dllVi.ProductVersion.Trim() } else { $null }
            & $addCheck $dll "DLL ProductVersion" "OK" $(if ($dllPv) { $dllPv } else { "(empty)" })
            if ($productVersion -and $dllPv -and $dllPv -ne $productVersion) {
                & $addCheck $dll "DLL vs EXE version" "WARN" ("DLL=$dllPv EXE=$productVersion")
            }
        }
        catch {
            & $addCheck $dll "DLL ProductVersion" "ERROR" $_.Exception.Message
        }

        $hasUpdateClient = [bool](Test-HeimdallBinaryContainsAscii -Path $dll -Needle "UpdateClient")
        & $addCheck $dll "Capability marker UpdateClient (v3+)" $(if ($hasUpdateClient) { "PRESENT" } else { "ABSENT" }) "ASCII/UTF-16 search in DLL"

        $hasDiskUsageScan = [bool](
            (Test-HeimdallBinaryContainsAscii -Path $dll -Needle "DiskUsageScanner") -or
            (Test-HeimdallBinaryContainsAscii -Path $dll -Needle "disk-usage")
        )
        & $addCheck $dll "Capability marker DiskUsageScanner (v6+)" $(if ($hasDiskUsageScan) { "PRESENT" } else { "ABSENT" }) "ASCII/UTF-16 search for DiskUsageScanner / disk-usage"
    }
    else {
        & $addCheck $dll "Heimdall.Agent.dll present" "MISSING" "Needed for capability markers"
    }

    if (Test-Path -LiteralPath $tuflow) {
        $hasTuflowLauncher = $true
        $tfi = Get-Item -LiteralPath $tuflow
        & $addCheck $tuflow "TuflowLauncher.exe" "PRESENT" ("LastWriteTime=" + $tfi.LastWriteTime.ToString("yyyy-MM-dd HH:mm:ss"))
    }
    else {
        & $addCheck $tuflow "TuflowLauncher.exe" "ABSENT" "Optional; modelling packs usually include it"
    }

    if (Test-Path -LiteralPath $settings) {
        & $addCheck $settings "appsettings.json" "PRESENT" ""
    }
    else {
        & $addCheck $settings "appsettings.json" "ABSENT" ""
    }

    & $addCheck $dataRoot "ProgramData\Heimdall" $(if (Test-Path -LiteralPath $dataRoot) { "PRESENT" } else { "ABSENT" }) ""
    & $addCheck $logRoot "ProgramData\Heimdall\logs" $(if (Test-Path -LiteralPath $logRoot) { "PRESENT" } else { "ABSENT" }) ""

    try {
        $svc = Get-Service -Name HeimdallAgent -ErrorAction SilentlyContinue
        if ($svc) {
            $serviceStatus = [string]$svc.Status
            & $addCheck "Service:HeimdallAgent" "Windows service" $serviceStatus ""
        }
        else {
            & $addCheck "Service:HeimdallAgent" "Windows service" "NOT_INSTALLED" ""
        }
    }
    catch {
        & $addCheck "Service:HeimdallAgent" "Windows service" "ERROR" $_.Exception.Message
    }

    $packVersion = $null
    $packExeVersion = $null
    $packBuilt = $null
    $packVersionJson = $null
    $packExe = $null
    if (-not [string]::IsNullOrWhiteSpace($PackScriptDir)) {
        $packVersionJson = Join-Path $PackScriptDir "VERSION.json"
        if (Test-Path -LiteralPath $packVersionJson) {
            try {
                $vj = Get-Content -Raw -LiteralPath $packVersionJson | ConvertFrom-Json
                $packVersion = [string]$vj.productVersion
                $packedAt = [string]$vj.packedAtUtc
                $detail = "productVersion=" + $(if ($packVersion) { $packVersion } else { "?" })
                if ($packedAt) { $detail += "; packedAtUtc=$packedAt" }
                & $addCheck $packVersionJson "Pack VERSION.json productVersion" "OK" $detail
            }
            catch {
                & $addCheck $packVersionJson "Pack VERSION.json productVersion" "ERROR" $_.Exception.Message
            }
        }
        else {
            & $addCheck $packVersionJson "Pack VERSION.json" "MISSING" "Fall back to payload exe ProductVersion"
        }

        $packExe = Join-Path $PackScriptDir "payload\Heimdall.Agent.exe"
        if (Test-Path -LiteralPath $packExe) {
            try {
                $pvi = [System.Diagnostics.FileVersionInfo]::GetVersionInfo($packExe)
                $pfi = Get-Item -LiteralPath $packExe
                $packExeVersion = if ($pvi.ProductVersion) { $pvi.ProductVersion.Trim() } else { $null }
                $packBuilt = $pfi.LastWriteTime.ToString("yyyy-MM-dd HH:mm:ss")
                if (-not $packVersion) { $packVersion = $packExeVersion }
                & $addCheck $packExe "Pack payload ProductVersion (will install)" "OK" `
                ("version=" + $(if ($packExeVersion) { $packExeVersion } else { "?" }) + "; LastWriteTime=" + $packBuilt)
            }
            catch {
                & $addCheck $packExe "Pack payload ProductVersion" "ERROR" $_.Exception.Message
            }
        }
        else {
            & $addCheck $packExe "Pack payload Heimdall.Agent.exe" "MISSING" ""
        }
    }

    $checks = @($script:__hdCheckBuf)
    Remove-Variable -Name __hdCheckBuf -Scope Script -ErrorAction SilentlyContinue

    $milestoneNotes = @()
    if ($null -ne $simple) {
        $milestoneNotes = @(Get-HeimdallClientVersionMilestoneNotes -SimpleVersion ([int]$simple))
    }

    $corroboration = @()
    if ($null -ne $simple) {
        $sv = [int]$simple
        if ($sv -ge 3) {
            if ($hasUpdateClient) { $corroboration += "v$sv expects UpdateClient: PRESENT (OK)" }
            else { $corroboration += "v$sv expects UpdateClient: ABSENT (unexpected for v3+)" }
        }
        elseif ($sv -lt 3 -and $hasUpdateClient) {
            $corroboration += "v$sv usually lacks UpdateClient, but marker PRESENT (unusual)"
        }

        if ($sv -ge 6) {
            if ($hasDiskUsageScan) { $corroboration += "v$sv expects DiskUsageScanner: PRESENT (OK)" }
            else { $corroboration += "v$sv expects DiskUsageScanner: ABSENT (unexpected for v6+)" }
        }
        elseif ($sv -eq 5) {
            if (-not $hasDiskUsageScan) { $corroboration += "v5 expects DiskUsageScanner: ABSENT (OK - scans need v6+)" }
            else { $corroboration += "v5 ProductVersion but DiskUsageScanner PRESENT (DLL newer than label?)" }
            if ($hasUpdateClient) { $corroboration += "v5 expects UpdateClient: PRESENT (OK)" }
        }
        elseif ($sv -lt 6 -and $hasDiskUsageScan) {
            $corroboration += "v$sv ProductVersion but DiskUsageScanner PRESENT (unexpected for pre-v6)"
        }
    }

    $compare = "unknown"
    $installedLabel = if ($installed -and $productVersion) { $productVersion } else { "(not installed)" }
    $packLabel = if ($packVersion) { $packVersion } else { "(unknown pack)" }
    if (-not $installed) {
        $compare = "fresh install -> pack $packLabel"
    }
    elseif ($packVersion -and (Get-Command Test-HeimdallProductVersionMatch -ErrorAction SilentlyContinue)) {
        if (Test-HeimdallProductVersionMatch -VersionA $productVersion -VersionB $packVersion) {
            $compare = "SAME (installed $installedLabel == pack $packLabel)"
        }
        else {
            $instSimple = Get-HeimdallSimpleClientVersion -Version $productVersion
            $packSimple = Get-HeimdallSimpleClientVersion -Version $packVersion
            if ($null -ne $instSimple -and $null -ne $packSimple -and ([int]$packSimple) -gt ([int]$instSimple)) {
                $compare = "UPGRADE (installed $installedLabel -> pack $packLabel)"
            }
            elseif ($null -ne $instSimple -and $null -ne $packSimple -and ([int]$packSimple) -lt ([int]$instSimple)) {
                $compare = "DOWNGRADE (installed $installedLabel -> pack $packLabel)"
            }
            else {
                $compare = "DIFFERENT (installed $installedLabel vs pack $packLabel)"
            }
        }
    }
    else {
        $compare = "installed $installedLabel | pack $packLabel"
    }

    $result = New-Object psobject
    $result | Add-Member NoteProperty Installed $installed
    $result | Add-Member NoteProperty InstallDir $AgentInstallDir
    $result | Add-Member NoteProperty ExePath $exe
    $result | Add-Member NoteProperty DllPath $dll
    $result | Add-Member NoteProperty ProductVersion $productVersion
    $result | Add-Member NoteProperty FileVersion $fileVersion
    $result | Add-Member NoteProperty SimpleVersion $simple
    $result | Add-Member NoteProperty BuildDateTime $buildDateTime
    $result | Add-Member NoteProperty HasUpdateClientMarker $hasUpdateClient
    $result | Add-Member NoteProperty HasDiskUsageScanMarker $hasDiskUsageScan
    $result | Add-Member NoteProperty HasTuflowLauncher $hasTuflowLauncher
    $result | Add-Member NoteProperty ServiceStatus $serviceStatus
    $result | Add-Member NoteProperty PackVersion $packVersion
    $result | Add-Member NoteProperty PackExeVersion $packExeVersion
    $result | Add-Member NoteProperty PackBuildDateTime $packBuilt
    $result | Add-Member NoteProperty PackVersionJsonPath $packVersionJson
    $result | Add-Member NoteProperty PackExePath $packExe
    $result | Add-Member NoteProperty CompareSummary $compare
    $result | Add-Member NoteProperty MilestoneNotes $milestoneNotes
    $result | Add-Member NoteProperty Corroboration $corroboration
    $result | Add-Member NoteProperty Checks $checks
    return $result
}

function Format-HeimdallClientVersionProbeSummary {
    param(
        [Parameter(Mandatory)]$Probe,
        [switch]$IncludeAllChecks
    )
    $lines = New-Object System.Collections.Generic.List[string]
    $lines.Add("Installed vs this pack") | Out-Null
    $lines.Add("----------------------") | Out-Null
    if ($Probe.Installed) {
        $built = if ($Probe.BuildDateTime) { [string]$Probe.BuildDateTime } else { "?" }
        $lines.Add("Installed now:  v$($Probe.ProductVersion)  (build/file $built)") | Out-Null
        $lines.Add("Install folder: $($Probe.InstallDir)") | Out-Null
        if ($null -ne $Probe.ServiceStatus -and $Probe.ServiceStatus -ne "") {
            $lines.Add("Service:        HeimdallAgent = $($Probe.ServiceStatus)") | Out-Null
        }
    }
    else {
        $lines.Add("Installed now:  (none found)") | Out-Null
        $lines.Add("Checked:        $($Probe.ExePath)") | Out-Null
    }
    $packBuilt = if ($Probe.PackBuildDateTime) { [string]$Probe.PackBuildDateTime } else { "?" }
    $lines.Add("This pack:      v$(if ($Probe.PackVersion) { $Probe.PackVersion } else { '?' })  (payload build/file $packBuilt)") | Out-Null
    $lines.Add("Compare:        $($Probe.CompareSummary)") | Out-Null
    $lines.Add("") | Out-Null

    if ($Probe.MilestoneNotes -and @($Probe.MilestoneNotes).Count -gt 0) {
        $lines.Add("If this host is v$($Probe.SimpleVersion) (milestone notes):") | Out-Null
        foreach ($n in @($Probe.MilestoneNotes)) { $lines.Add("  - $n") | Out-Null }
        $lines.Add("") | Out-Null
    }
    if ($Probe.Corroboration -and @($Probe.Corroboration).Count -gt 0) {
        $lines.Add("Capability corroboration:") | Out-Null
        foreach ($c in @($Probe.Corroboration)) { $lines.Add("  - $c") | Out-Null }
        $lines.Add("") | Out-Null
    }

    $lines.Add("Locations checked:") | Out-Null
    foreach ($c in @($Probe.Checks)) {
        $detail = if ($c.Detail) { " - $($c.Detail)" } else { "" }
        $lines.Add("  [$($c.Result)] $($c.What)") | Out-Null
        $lines.Add("           $($c.Path)$detail") | Out-Null
    }
    return ($lines -join "`r`n")
}

function Test-HeimdallProductVersionAccept {
    param(
        [string]$LocalVersion,
        # Client pack baseline (ProductVersionExpected from local VERSION.json).
        # Do NOT pass API /api/health productVersion — that is API SemVer and is independent.
        [string]$ExpectedClientVersion = $null,
        [scriptblock]$Log = $null,
        [scriptblock]$ConfirmMismatch = $null
    )
    $localPv = [string]$LocalVersion
    $expectedPv = [string]$ExpectedClientVersion

    # Guard: never treat API SemVer (e.g. 0.1.0+hash) as a client pack baseline.
    if (-not [string]::IsNullOrWhiteSpace($expectedPv)) {
        $coreExpected = Get-HeimdallCoreProductVersion -Version $expectedPv
        if ($coreExpected -notmatch '^\d+$') {
            if ($Log) {
                & $Log "Ignoring non-integer ExpectedClientVersion '$expectedPv' (API /api/health SemVer is reachability-only; not compared to client pack)." "WARN"
            }
            return $true
        }
    }

    $verLine = Format-HeimdallVersionCompareLine -LabelA "Pack" -VersionA $localPv -LabelB "Expected" -VersionB $expectedPv
    if ($Log) { & $Log "Version: $verLine" "INFO" }

    if (Test-HeimdallProductVersionMatch -VersionA $localPv -VersionB $expectedPv) {
        $simple = Get-HeimdallSimpleClientVersion -Version $localPv
        if ($localPv -ne $expectedPv) {
            if ($Log) { & $Log "Client version match (simple=$simple; legacy SemVer maps to 1)" "INFO" }
        }
        else {
            if ($Log) { & $Log "Client pack version matches expected ($localPv)." "OK" }
        }
        return $true
    }

    $simpleA = Get-HeimdallSimpleClientVersion -Version $localPv
    $simpleB = Get-HeimdallSimpleClientVersion -Version $expectedPv
    if ($Log) { & $Log "Client version MISMATCH: pack=$localPv (simple=$simpleA) expected=$expectedPv (simple=$simpleB)" "WARN" }
    if ($ConfirmMismatch) {
        return [bool](& $ConfirmMismatch $localPv $expectedPv)
    }
    return $false
}
