#Requires -RunAsAdministrator
$ErrorActionPreference = "Stop"
$tmp = "C:\ProgramData\Heimdall\logs\close-stale-sessions"
New-Item -ItemType Directory -Force -Path $tmp | Out-Null
$proj = Join-Path $tmp "close.csproj"
$prog = Join-Path $tmp "Program.cs"
@"
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup><OutputType>Exe</OutputType><TargetFramework>net10.0</TargetFramework><ImplicitUsings>enable</ImplicitUsings></PropertyGroup>
  <ItemGroup><PackageReference Include="Microsoft.Data.Sqlite" Version="9.0.0" /></ItemGroup>
</Project>
"@ | Set-Content -LiteralPath $proj -Encoding UTF8
@"
using Microsoft.Data.Sqlite;
var db = @"C:\ProgramData\Heimdall\heimdall.db";
await using var conn = new SqliteConnection($"Data Source={db}");
await conn.OpenAsync();
await using var cmd = conn.CreateCommand();
cmd.CommandText = @"
UPDATE Sessions
SET State = 2,
    EndedAtUtc = COALESCE(EndedAtUtc, LastObservedUtc)
WHERE State != 2
  AND LastObservedUtc < datetime('now', '-1 day');
SELECT changes();
";
var n = Convert.ToInt32(await cmd.ExecuteScalarAsync());
File.WriteAllText(@"C:\ProgramData\Heimdall\logs\close-stale-sessions-result.txt", $"closed={n} at={DateTime.Now:o}");
Console.WriteLine($"closed={n}");
"@ | Set-Content -LiteralPath $prog -Encoding UTF8
Set-Location $tmp
dotnet run -c Release --verbosity quiet | Tee-Object -FilePath (Join-Path $tmp "out.txt")
