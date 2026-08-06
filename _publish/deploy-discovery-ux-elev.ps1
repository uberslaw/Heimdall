$ErrorActionPreference = "Stop"
$installDir = "$env:ProgramFiles\Heimdall\Api"
$stage = "C:\Heimdall\_publish\Api"
$log = "C:\Heimdall\_publish\approve-all-multiselect-deploy.log"
function L($m) { Add-Content $log "$(Get-Date -Format o) $m"; Write-Host $m }
try {
  Remove-Item $log -ErrorAction SilentlyContinue
  L "WhoAmI=$([Security.Principal.WindowsIdentity]::GetCurrent().Name) IsAdmin=$([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)"
  L "Stopping..."
  Stop-Service HeimdallApi -Force
  Start-Sleep -Seconds 2
  L "Copying from stage (excluding appsettings)..."
  robocopy $stage $installDir /E /XO /NFL /NDL /NJH /NJS /nc /ns /np /XF appsettings.json appsettings.Development.json | Out-Null
  Copy-Item (Join-Path $stage "Heimdall.Api.dll") (Join-Path $installDir "Heimdall.Api.dll") -Force
  Copy-Item (Join-Path $stage "Heimdall.Api.exe") (Join-Path $installDir "Heimdall.Api.exe") -Force
  Copy-Item (Join-Path $stage "Heimdall.Shared.dll") (Join-Path $installDir "Heimdall.Shared.dll") -Force
  $cssSrc = Join-Path $stage "wwwroot\css\site.css"
  $cssDst = Join-Path $installDir "wwwroot\css\site.css"
  if (Test-Path $cssSrc) { Copy-Item $cssSrc $cssDst -Force }
  L "Starting..."
  Start-Service HeimdallApi
  Start-Sleep -Seconds 4
  $s = Get-Service HeimdallApi
  $dll = Get-Item (Join-Path $installDir "Heimdall.Api.dll")
  L "OK Status=$($s.Status) DllWrite=$($dll.LastWriteTime)"
  exit 0
} catch {
  L "FAIL $_"
  try { Start-Service HeimdallApi } catch { L "restart also failed: $_" }
  exit 1
}
