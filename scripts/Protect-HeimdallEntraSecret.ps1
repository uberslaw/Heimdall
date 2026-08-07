#Requires -RunAsAdministrator
<#
.SYNOPSIS
  Store Heimdall Entra (Graph) app credentials in a DPAPI-encrypted ProgramData file.

.DESCRIPTION
  Writes %ProgramData%\Heimdall\secrets\entra.json with:
    - TenantId / ClientId / DefaultNetBiosDomain (plain — not highly sensitive)
    - ClientSecretProtected (DPAPI LocalMachine ciphertext — not plain text)

  The HeimdallApi Windows service (LocalSystem) can decrypt LocalMachine DPAPI on this host.
  The file is ACL'd to SYSTEM + Administrators only. Never commit this file to git.

.PARAMETER TenantId
  Entra directory (tenant) ID (GUID).

.PARAMETER ClientId
  App registration application (client) ID (GUID).

.PARAMETER ClientSecret
  Client secret value (plain). Prefer -ClientSecretSecure. Not written to disk in clear text.

.PARAMETER ClientSecretSecure
  SecureString form of the client secret (recommended).

.PARAMETER DefaultNetBiosDomain
  Optional domain stamp for PersonTeam when Graph has no onPremisesDomainName (default ARUP).

.EXAMPLE
  .\Protect-HeimdallEntraSecret.ps1 -TenantId '...' -ClientId '...' -ClientSecretSecure (Read-Host -AsSecureString 'Client secret')
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [ValidatePattern('(?i)^[0-9a-f]{8}-([0-9a-f]{4}-){3}[0-9a-f]{12}$')]
    [string] $TenantId,

    [Parameter(Mandatory = $true)]
    [ValidatePattern('(?i)^[0-9a-f]{8}-([0-9a-f]{4}-){3}[0-9a-f]{12}$')]
    [string] $ClientId,

    [Parameter(Mandatory = $false)]
    [string] $ClientSecret,

    [Parameter(Mandatory = $false)]
    [SecureString] $ClientSecretSecure,

    [Parameter(Mandatory = $false)]
    [string] $DefaultNetBiosDomain = "ARUP"
)

$ErrorActionPreference = "Stop"

if (-not $ClientSecretSecure -and [string]::IsNullOrWhiteSpace($ClientSecret)) {
    $ClientSecretSecure = Read-Host -AsSecureString -Prompt "Entra client secret"
}
if (-not $ClientSecretSecure) {
    $ClientSecretSecure = ConvertTo-SecureString -String $ClientSecret -AsPlainText -Force
}

$bstr = [Runtime.InteropServices.Marshal]::SecureStringToBSTR($ClientSecretSecure)
try {
    $plainSecret = [Runtime.InteropServices.Marshal]::PtrToStringBSTR($bstr)
}
finally {
    [Runtime.InteropServices.Marshal]::ZeroFreeBSTR($bstr) | Out-Null
}

if ([string]::IsNullOrWhiteSpace($plainSecret)) {
    throw "Client secret is empty."
}

Add-Type -AssemblyName System.Security
$secretBytes = [Text.Encoding]::UTF8.GetBytes($plainSecret)
$protectedBytes = [Security.Cryptography.ProtectedData]::Protect(
    $secretBytes,
    $null,
    [Security.Cryptography.DataProtectionScope]::LocalMachine)
$protectedB64 = [Convert]::ToBase64String($protectedBytes)

# Best-effort clear of plain secret from this process
$plainSecret = $null
$secretBytes = $null
[GC]::Collect()

$secretsDir = Join-Path $env:ProgramData "Heimdall\secrets"
New-Item -ItemType Directory -Force -Path $secretsDir | Out-Null
$path = Join-Path $secretsDir "entra.json"

$doc = [ordered]@{
    TenantId               = $TenantId.Trim()
    ClientId               = $ClientId.Trim()
    ClientSecretProtected  = $protectedB64
    DefaultNetBiosDomain   = if ([string]::IsNullOrWhiteSpace($DefaultNetBiosDomain)) { $null } else { $DefaultNetBiosDomain.Trim() }
}

$json = ($doc | ConvertTo-Json -Depth 5)
# UTF-8 no BOM
[IO.File]::WriteAllText($path, $json, [Text.UTF8Encoding]::new($false))

# ACL: SYSTEM + Administrators only (remove inherited Users read if present)
try {
    $acl = Get-Acl -LiteralPath $path
    $acl.SetAccessRuleProtection($true, $false)
    $acl.Access | ForEach-Object { [void]$acl.RemoveAccessRule($_) }
    $system = New-Object System.Security.AccessControl.FileSystemAccessRule(
        "NT AUTHORITY\SYSTEM", "FullControl", "Allow")
    $admins = New-Object System.Security.AccessControl.FileSystemAccessRule(
        "BUILTIN\Administrators", "FullControl", "Allow")
    $acl.AddAccessRule($system)
    $acl.AddAccessRule($admins)
    Set-Acl -LiteralPath $path -AclObject $acl
}
catch {
    Write-Warning "Could not tighten ACL on $path : $($_.Exception.Message)"
}

Write-Host "Wrote DPAPI-protected Entra secrets: $path"
Write-Host "Restart the API service to load them:"
Write-Host "  Restart-Service HeimdallApi"
Write-Host ""
Write-Host "Do not put ClientSecret in appsettings.json or git."
