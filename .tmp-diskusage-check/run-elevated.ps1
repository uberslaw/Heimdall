$ErrorActionPreference = "Stop"
Set-Location C:\Heimdall\.tmp-diskusage-check
dotnet build -c Release Check.csproj | Out-Host
dotnet run -c Release --no-build --project Check.csproj *>&1 | Tee-Object -FilePath mft-result.txt
