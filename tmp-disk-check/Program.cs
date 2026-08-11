using Microsoft.Data.Sqlite;
using var conn = new SqliteConnection(@"Data Source=C:\ProgramData\Heimdall\heimdall.db");
conn.Open();
using var cmd = conn.CreateCommand();
cmd.CommandText = "SELECT AgentVersion, COUNT(*) FROM Machines GROUP BY AgentVersion ORDER BY 2 DESC";
using var r = cmd.ExecuteReader();
while (r.Read()) Console.WriteLine($"{r.GetValue(0)} => {r.GetValue(1)}");
