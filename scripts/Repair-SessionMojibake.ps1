<#
.SYNOPSIS
  Preview / repair session & process usernames corrupted by ANSI-as-UTF16 WTS mojibake.

.DESCRIPTION
  Older Heimdall agents called WTSQuerySessionInformationA and read the buffer with
  PtrToStringUni, turning ASCII names like Christopher.Owen into CJK mojibake.

  This script lists suspect rows, can rewrite recoverable Domain/Username fields in place,
  or delete unrecoverable junk rows. Does not wipe all history.

.EXAMPLE
  .\Repair-SessionMojibake.ps1 -WhatIf
  .\Repair-SessionMojibake.ps1 -Repair
  .\Repair-SessionMojibake.ps1 -DeleteUnrecoverable
#>
[CmdletBinding(SupportsShouldProcess = $true)]
param(
    [string]$DbPath = (Join-Path $env:ProgramData "Heimdall\heimdall.db"),
    [switch]$Repair,
    [switch]$DeleteUnrecoverable
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

if (-not (Test-Path -LiteralPath $DbPath)) {
    throw "Database not found: $DbPath"
}

Add-Type -AssemblyName System.Data
# Use Microsoft.Data.Sqlite from a nearby build if present; else System.Data.SQLite is uncommon.
# Prefer `dotnet` exec of a tiny repair — fall back to sqlite3 CLI if available.

function Test-LooksLikeAccount([string]$value) {
    if ([string]::IsNullOrWhiteSpace($value) -or $value.Length -gt 256) { return $false }
    foreach ($c in $value.ToCharArray()) {
        if (-not (
            [char]::IsAsciiLetterOrDigit($c) -or
            $c -in @('.', '-', '_', '$', '\', '@')
        )) { return $false }
    }
    return $true
}

function Repair-AnsiMisread([string]$value) {
    if ([string]::IsNullOrEmpty($value)) { return $null }
    if (Test-LooksLikeAccount $value) { return $null }

    $hasCjk = $false
    $bytes = New-Object System.Collections.Generic.List[byte]
    foreach ($ch in $value.ToCharArray()) {
        $code = [int][char]$ch
        if ($code -ge 0x80 -and $code -le 0xFFFF) {
            if ($code -ge 0x2E80) { $hasCjk = $true }
            [void]$bytes.Add([byte]($code -band 0xFF))
            [void]$bytes.Add([byte](($code -shr 8) -band 0xFF))
        }
        elseif (($code -ge 0x20 -and $code -lt 0x7F) -or $code -eq 92) {
            [void]$bytes.Add([byte]$code)
        }
        elseif ($code -eq 0) { break }
        else { return $null }
    }

    if (-not $hasCjk -or $bytes.Count -lt 2) { return $null }
    $recovered = [Text.Encoding]::GetEncoding(28591).GetString($bytes.ToArray()).TrimEnd([char]0).Trim()
    if (Test-LooksLikeAccount $recovered) { return $recovered }
    return $null
}

function Test-Suspect([string]$value) {
    if ([string]::IsNullOrEmpty($value)) { return $false }
    if (Test-LooksLikeAccount $value) { return $false }
    foreach ($ch in $value.ToCharArray()) {
        if ([int][char]$ch -ge 0x2E80) { return $true }
    }
    return $false
}

$sqlite3 = Get-Command sqlite3 -ErrorAction SilentlyContinue
if (-not $sqlite3) {
    Write-Host @"
sqlite3 CLI not found on PATH.

Manual SQL (inspect only) against $DbPath:

  SELECT Id, Domain, Username, SessionType, ActiveSeconds
  FROM Sessions
  WHERE Username GLOB '*[一-龥]*' OR IFNULL(Domain,'') GLOB '*[一-龥]*';

  SELECT Id, Username FROM ProcessRuns
  WHERE Username GLOB '*[一-龥]*';

After deploying the fixed agent + API, open sessions may self-heal on next ingest.
For ended mojibake rows, re-run this script on a machine with sqlite3, or delete by Id after review:

  DELETE FROM Sessions WHERE Id IN (...);
  DELETE FROM ProcessRuns WHERE Id IN (...);

Install sqlite3 (e.g. winget install SQLite.SQLite) and re-run for automated repair.
"@
    exit 1
}

function Invoke-Sql([string]$sql) {
    & sqlite3 -batch -separator "`t" $DbPath $sql
}

Write-Host "Database: $DbPath"
Write-Host "Scanning Sessions / ProcessRuns for CJK-looking account fields..."

$sessionRows = @(Invoke-Sql "SELECT Id, IFNULL(Domain,''), Username FROM Sessions;")
$processRows = @(Invoke-Sql "SELECT Id, Username FROM ProcessRuns;")

$toRepairSessions = @()
$toDeleteSessions = @()
foreach ($line in $sessionRows) {
    if ([string]::IsNullOrWhiteSpace($line)) { continue }
    $parts = $line -split "`t", 3
    if ($parts.Count -lt 3) { continue }
    $id = [int]$parts[0]
    $domain = $parts[1]
    $user = $parts[2]
    $suspect = (Test-Suspect $user) -or (Test-Suspect $domain)
    if (-not $suspect) { continue }

    $ru = Repair-AnsiMisread $user
    $rd = if ($domain) { Repair-AnsiMisread $domain } else { $null }
    if ($ru -or $rd) {
        $toRepairSessions += [pscustomobject]@{
            Id = $id
            Domain = $domain
            Username = $user
            NewDomain = $(if ($rd) { $rd } else { $domain })
            NewUsername = $(if ($ru) { $ru } else { $user })
        }
    }
    else {
        $toDeleteSessions += [pscustomobject]@{ Id = $id; Domain = $domain; Username = $user }
    }
}

$toRepairProcesses = @()
$toDeleteProcesses = @()
foreach ($line in $processRows) {
    if ([string]::IsNullOrWhiteSpace($line)) { continue }
    $parts = $line -split "`t", 2
    if ($parts.Count -lt 2) { continue }
    $id = [int]$parts[0]
    $user = $parts[1]
    if (-not (Test-Suspect $user)) { continue }
    $ru = Repair-AnsiMisread $user
    if ($ru) {
        $toRepairProcesses += [pscustomobject]@{ Id = $id; Username = $user; NewUsername = $ru }
    }
    else {
        $toDeleteProcesses += [pscustomobject]@{ Id = $id; Username = $user }
    }
}

Write-Host ""
Write-Host "Sessions recoverable: $($toRepairSessions.Count)"
$toRepairSessions | Format-Table -AutoSize | Out-String | Write-Host
Write-Host "Sessions unrecoverable (candidates for delete): $($toDeleteSessions.Count)"
$toDeleteSessions | Format-Table -AutoSize | Out-String | Write-Host
Write-Host "ProcessRuns recoverable: $($toRepairProcesses.Count)"
$toRepairProcesses | Format-Table -AutoSize | Out-String | Write-Host
Write-Host "ProcessRuns unrecoverable: $($toDeleteProcesses.Count)"
$toDeleteProcesses | Format-Table -AutoSize | Out-String | Write-Host

if ($Repair) {
    foreach ($row in $toRepairSessions) {
        $sql = "UPDATE Sessions SET Domain='$(($row.NewDomain) -replace "'","''")', Username='$(($row.NewUsername) -replace "'","''")' WHERE Id=$($row.Id);"
        if ($PSCmdlet.ShouldProcess("Sessions Id=$($row.Id)", "Repair to $($row.NewDomain)\$($row.NewUsername)")) {
            Invoke-Sql $sql | Out-Null
        }
    }
    foreach ($row in $toRepairProcesses) {
        $sql = "UPDATE ProcessRuns SET Username='$(($row.NewUsername) -replace "'","''")' WHERE Id=$($row.Id);"
        if ($PSCmdlet.ShouldProcess("ProcessRuns Id=$($row.Id)", "Repair to $($row.NewUsername)")) {
            Invoke-Sql $sql | Out-Null
        }
    }
    Write-Host "Repair pass complete."
}

if ($DeleteUnrecoverable) {
    foreach ($row in $toDeleteSessions) {
        if ($PSCmdlet.ShouldProcess("Sessions Id=$($row.Id) ($($row.Domain)\$($row.Username))", "DELETE")) {
            Invoke-Sql "DELETE FROM Sessions WHERE Id=$($row.Id);" | Out-Null
        }
    }
    foreach ($row in $toDeleteProcesses) {
        if ($PSCmdlet.ShouldProcess("ProcessRuns Id=$($row.Id) ($($row.Username))", "DELETE")) {
            Invoke-Sql "DELETE FROM ProcessRuns WHERE Id=$($row.Id);" | Out-Null
        }
    }
    Write-Host "Delete pass complete."
}

if (-not $Repair -and -not $DeleteUnrecoverable) {
    Write-Host "Dry run only. Re-run with -Repair and/or -DeleteUnrecoverable (add -WhatIf first if desired)."
}
