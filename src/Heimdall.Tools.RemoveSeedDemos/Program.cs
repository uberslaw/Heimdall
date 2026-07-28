using Microsoft.Data.Sqlite;

namespace Heimdall.Tools.RemoveSeedDemos;

internal static class RemoveSeedDemosProgram
{
    // Keep in sync with SeedData.DemoHostnames in IngestService.cs
    private static readonly string[] DemoHostnames =
    [
        "DEMO-SYD-01",
        "DEMO-SYD-02",
        "DEMO-LON-01",
        "DEMO-POC-01"
    ];

    private const string DemoMachinesOfferedFlag = "DemoMachinesOffered";

    public static int Main(string[] args)
    {
        if (!TryParseArgs(args, out var dbPath, out var delete))
        {
            Console.Error.WriteLine("Usage: Heimdall.Tools.RemoveSeedDemos --db <path> [--delete]");
            Console.Error.WriteLine("  Lists seed/demo machines by default; pass --delete to remove them.");
            return 2;
        }

        if (!File.Exists(dbPath))
        {
            Console.Error.WriteLine($"Database not found: {dbPath}");
            return 3;
        }

        try
        {
            var machines = ListSeedDemoMachines(dbPath);
            foreach (var (id, hostname, agentVersion) in machines)
                Console.WriteLine($"{id}|{hostname}|{agentVersion}");

            if (machines.Count == 0)
                return 0;

            if (!delete)
                return 0;

            DeleteSeedDemoMachines(dbPath);
            return 0;
        }
        catch (SqliteException ex) when (ex.SqliteErrorCode == 5) // SQLITE_BUSY
        {
            Console.Error.WriteLine("Database is locked. Stop the HeimdallApi service and retry.");
            Console.Error.WriteLine(ex.Message);
            return 4;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine(ex.Message);
            return 1;
        }
    }

    private static bool TryParseArgs(string[] args, out string dbPath, out bool delete)
    {
        dbPath = "";
        delete = false;

        for (var i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--db":
                    if (++i >= args.Length)
                        return false;
                    dbPath = args[i];
                    break;
                case "--delete":
                    delete = true;
                    break;
                case "-h":
                case "--help":
                    return false;
            }
        }

        return !string.IsNullOrWhiteSpace(dbPath);
    }

    private static string BuildWhereClause(out List<SqliteParameter> parameters)
    {
        parameters = [];
        var hostPlaceholders = new List<string>();
        for (var i = 0; i < DemoHostnames.Length; i++)
        {
            var name = $"@h{i}";
            hostPlaceholders.Add(name);
            parameters.Add(new SqliteParameter(name, DemoHostnames[i]));
        }

        return $"AgentVersion = 'seed' OR Hostname IN ({string.Join(", ", hostPlaceholders)})";
    }

    private static List<(int Id, string Hostname, string AgentVersion)> ListSeedDemoMachines(string dbPath)
    {
        var result = new List<(int, string, string)>();
        var where = BuildWhereClause(out var parameters);

        using var connection = OpenConnection(dbPath);
        using var command = connection.CreateCommand();
        command.CommandText = $@"
SELECT Id, Hostname, IFNULL(AgentVersion, '')
FROM Machines
WHERE {where}
ORDER BY Hostname;";
        command.Parameters.AddRange(parameters.ToArray());

        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            result.Add((
                reader.GetInt32(0),
                reader.GetString(1),
                reader.GetString(2)));
        }

        return result;
    }

    private static void DeleteSeedDemoMachines(string dbPath)
    {
        var where = BuildWhereClause(out var parameters);

        using var connection = OpenConnection(dbPath);
        using var transaction = connection.BeginTransaction();

        try
        {
            Execute(connection, transaction, "CREATE TABLE IF NOT EXISTS SystemFlags (Key TEXT PRIMARY KEY, Value TEXT NOT NULL);");

            Execute(connection, transaction,
                $"DELETE FROM Sessions WHERE MachineId IN (SELECT Id FROM Machines WHERE {where});",
                parameters);

            Execute(connection, transaction,
                $"DELETE FROM ProcessRuns WHERE MachineId IN (SELECT Id FROM Machines WHERE {where});",
                parameters);

            Execute(connection, transaction,
                $"DELETE FROM MachineIdentityEvents WHERE MachineId IN (SELECT Id FROM Machines WHERE {where});",
                parameters);

            Execute(connection, transaction,
                $"DELETE FROM Machines WHERE {where};",
                parameters);

            using (var flag = connection.CreateCommand())
            {
                flag.Transaction = transaction;
                flag.CommandText = "INSERT OR REPLACE INTO SystemFlags (Key, Value) VALUES (@key, @value);";
                flag.Parameters.AddWithValue("@key", DemoMachinesOfferedFlag);
                flag.Parameters.AddWithValue("@value", "1");
                flag.ExecuteNonQuery();
            }

            transaction.Commit();
        }
        catch
        {
            transaction.Rollback();
            throw;
        }
    }

    private static void Execute(SqliteConnection connection, SqliteTransaction transaction, string sql, List<SqliteParameter>? parameters = null)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = sql;
        if (parameters is not null)
        {
            foreach (var p in parameters)
                command.Parameters.Add(new SqliteParameter(p.ParameterName, p.Value));
        }

        command.ExecuteNonQuery();
    }

    private static SqliteConnection OpenConnection(string dbPath)
    {
        var builder = new SqliteConnectionStringBuilder
        {
            DataSource = dbPath,
            Mode = SqliteOpenMode.ReadWriteCreate
        };
        var connection = new SqliteConnection(builder.ConnectionString);
        connection.Open();
        return connection;
    }
}
