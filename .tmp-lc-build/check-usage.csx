using System;
using Microsoft.Data.Sqlite;
var cs = "Data Source=C:\\ProgramData\\Heimdall\\heimdall.db";
using var conn = new SqliteConnection(cs);
conn.Open();
using (var cmd = conn.CreateCommand()) {
  cmd.CommandText = "SELECT name FROM sqlite_master WHERE type='table' AND name='SiteUsageEvents'";
  Console.WriteLine("table=" + (cmd.ExecuteScalar() ?? "MISSING"));
}
using (var cmd = conn.CreateCommand()) {
  cmd.CommandText = "SELECT EventType, COUNT(*), MAX(OccurredUtc) FROM SiteUsageEvents GROUP BY EventType";
  using var r = cmd.ExecuteReader();
  while (r.Read()) Console.WriteLine($"type={r.GetString(0)} count={r.GetInt64(1)} max={r.GetString(2)}");
}
using (var cmd = conn.CreateCommand()) {
  cmd.CommandText = "SELECT COUNT(*) FROM SiteUsageEvents";
  Console.WriteLine("total=" + cmd.ExecuteScalar());
}
using (var cmd = conn.CreateCommand()) {
  cmd.CommandText = "SELECT OccurredUtc, EventType, UserName, Path, DurationSeconds FROM SiteUsageEvents ORDER BY Id DESC LIMIT 10";
  using var r = cmd.ExecuteReader();
  while (r.Read()) Console.WriteLine($"{r[0]} | {r[1]} | {r[2]} | {r[3]} | dur={r[4]}");
}
