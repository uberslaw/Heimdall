#Requires -RunAsAdministrator
$ErrorActionPreference = "Stop"
$log = "C:\Heimdall\_publish\approve-all-deploy.log"
function L($m) { Add-Content $log "$(Get-Date -Format o) $m"; Write-Host $m }
Remove-Item $log -ErrorAction SilentlyContinue

$src = "C:\Heimdall\src\Heimdall.Api\bin\Release\net10.0"
$dest = "C:\Program Files\Heimdall\Api"
$stage = "C:\Heimdall\_publish\Api"
$settingsSrc = "C:\Heimdall\_publish\appsettings.restore.json"

L "Stopping HeimdallApi..."
Stop-Service HeimdallApi -Force
$deadline = (Get-Date).AddSeconds(45)
while ((Get-Service HeimdallApi).Status -ne "Stopped") {
  if ((Get-Date) -ge $deadline) { throw "Timeout stopping service" }
  Start-Sleep -Seconds 1
}

L "Staging publish folder (no appsettings overwrite)..."
New-Item -ItemType Directory -Force -Path $stage | Out-Null
robocopy $src $stage /E /NFL /NDL /NJH /NJS /nc /ns /np /XF appsettings.json appsettings.Development.json | Out-Null

L "Deploying binaries..."
$files = @(
  "Heimdall.Api.dll","Heimdall.Api.exe","Heimdall.Api.pdb",
  "Heimdall.Shared.dll","Heimdall.Shared.pdb",
  "Heimdall.Api.deps.json","Heimdall.Api.runtimeconfig.json"
)
foreach ($f in $files) {
  $from = Join-Path $src $f
  if (Test-Path $from) {
    Copy-Item $from (Join-Path $dest $f) -Force
    L "Copied $f"
  }
}
if (Test-Path (Join-Path $src "wwwroot")) {
  Copy-Item (Join-Path $src "wwwroot\*") (Join-Path $dest "wwwroot") -Recurse -Force
  L "Copied wwwroot"
}

L "Writing appsettings.json (Urls 5080 + ProgramData DB)..."
Copy-Item $settingsSrc (Join-Path $dest "appsettings.json") -Force

L "Starting HeimdallApi..."
Start-Service HeimdallApi
Start-Sleep -Seconds 4
$s = Get-Service HeimdallApi
L "Status=$($s.Status)"

$listen5080 = Get-NetTCPConnection -State Listen -ErrorAction SilentlyContinue | Where-Object { $_.LocalPort -eq 5080 }
$listen5000 = Get-NetTCPConnection -State Listen -ErrorAction SilentlyContinue | Where-Object { $_.LocalPort -eq 5000 }
L "Listen5080=$([bool]$listen5080) Listen5000=$([bool]$listen5000)"

try {
  $h = Invoke-WebRequest -Uri "http://127.0.0.1:5080/api/health" -UseBasicParsing -TimeoutSec 10
  L "Health5080 $($h.StatusCode): $($h.Content)"
} catch {
  L "Health5080 failed: $($_.Exception.Message)"
}

try {
  $h2 = Invoke-WebRequest -Uri "http://127.0.0.1:5000/api/health" -UseBasicParsing -TimeoutSec 3
  L "Health5000 still up: $($h2.Content)"
} catch {
  L "Health5000 refused (expected if Urls fixed)"
}

L "Done."
