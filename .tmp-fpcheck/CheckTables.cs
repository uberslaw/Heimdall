using Microsoft.Data.Sqlite;
foreach (var path in new[]{
  Environment.ExpandEnvironmentVariables(@"%ProgramData%\Heimdall\heimdall.db"),
  Environment.ExpandEnvironmentVariables(@"%ProgramData%\Heimdall\heimdall-dev.db")})
{
  Console.WriteLine("=== " + path + " ===");
  using var c = new SqliteConnection("Data Source=" + path);
  c.Open();
  using var cmd = c.CreateCommand();
  cmd.CommandText = "SELECT name FROM sqlite_master WHERE type='table' AND name LIKE 'Tuflow%' ORDER BY 1";
  using var r = cmd.ExecuteReader();
  while (r.Read()) Console.WriteLine(r.GetString(0));
}
