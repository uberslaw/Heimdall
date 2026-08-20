# Merge Heimdall:CodeMeter into an installed API appsettings.json when the section is missing.
# Republish copies binaries but excludes appsettings, so new keys never appear otherwise.
function Merge-HeimdallCodeMeterAppsettings {
    param(
        [Parameter(Mandatory = $true)]
        [string] $AppSettingsPath,
        [switch] $EnableIfRuntimePresent
    )

    if (-not (Test-Path -LiteralPath $AppSettingsPath)) {
        Write-Warning "Skip CodeMeter merge: $AppSettingsPath not found"
        return $false
    }

    $cmuCandidates = @(
        'C:\Program Files\CodeMeter\Runtime\bin\cmu32.exe',
        'C:\Program Files (x86)\CodeMeter\Runtime\bin\cmu32.exe'
    )
    $cmu = $cmuCandidates | Where-Object { Test-Path -LiteralPath $_ } | Select-Object -First 1
    $enable = [bool]$EnableIfRuntimePresent -and [bool]$cmu

    $raw = Get-Content -LiteralPath $AppSettingsPath -Raw -Encoding UTF8
    $config = $raw | ConvertFrom-Json
    if (-not $config.Heimdall) {
        $config | Add-Member -NotePropertyName Heimdall -NotePropertyValue ([pscustomobject]@{})
    }
    if ($null -ne $config.Heimdall.CodeMeter) {
        return $false
    }

    $servers = @(
        [pscustomobject]@{ Fqdn = 'azlicp01.global.arup.com'; Serial = '140-3520273628' },
        [pscustomobject]@{ Fqdn = 'bnevmlic01.global.arup.com'; Serial = '140-3520273628' },
        [pscustomobject]@{ Fqdn = 'bnevmlic01.global.arup.com'; Serial = '2-2309914' }
    )
    $product = {
        param($code)
        [pscustomobject]@{
            ProductCode   = $code
            TotalLicenses = 32
            Servers       = $servers
        }
    }

    $codeMeter = [pscustomobject]@{
        Enabled             = $enable
        Cmu32Path           = $(if ($cmu) { $cmu } else { $cmuCandidates[0] })
        PollSeconds         = 60
        InitialDelaySeconds = 5
        QueryTimeoutSeconds = 90
        Hpc                 = & $product 926
        Classic             = & $product 920
    }
    $config.Heimdall | Add-Member -NotePropertyName CodeMeter -NotePropertyValue $codeMeter -Force
    $json = $config | ConvertTo-Json -Depth 12
    Set-Content -LiteralPath $AppSettingsPath -Value $json -Encoding UTF8
    return $true
}
