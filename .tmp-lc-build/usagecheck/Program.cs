using Microsoft.Data.Sqlite;
using var conn = new SqliteConnection(@"Data Source=C:\ProgramData\Heimdall\heimdall.db");
conn.Open();
using var cmd = conn.CreateCommand();
cmd.CommandText = @"SELECT Hostname, FriendlyName, AgentVersion, LastSeenUtc, LastIp FROM Machines WHERE Hostname LIKE '%J6XYXL4%' OR FriendlyName LIKE '%Megatron%' OR FriendlyName LIKE '%egatron%'";
using var r = cmd.ExecuteReader();
while (r.Read()) Console.WriteLine($"{r[0]} | {r[1]} | ver={r[2]} | seen={r[3]} | ip={r[4]}");
