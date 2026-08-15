using System;
using Microsoft.Data.Sqlite;
class P {
  static void Main(string[] args) {
    foreach (var path in args) {
      Console.WriteLine("=== " + path + " ===");
      if (!System.IO.File.Exists(path)) { Console.WriteLine("MISSING"); continue; }
      using var c = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path }.ToString());
      c.Open();
      using (var cmd = c.CreateCommand()) {
        cmd.CommandText = "SELECT name FROM sqlite_master WHERE type='table' AND name LIKE 'Tuflow%' ORDER BY 1;";
        using var r = cmd.ExecuteReader();
        var any=false;
        while (r.Read()) { Console.WriteLine("table: " + r.GetString(0)); any=true; }
        if (!any) Console.WriteLine("(no Tuflow* tables)");
      }
    }
  }
}
