using Microsoft.Data.Sqlite;
using var c = new SqliteConnection(@"Data Source=C:\ProgramData\Heimdall\heimdall.db;Mode=ReadOnly");
c.Open();
using var cmd = c.CreateCommand();
cmd.CommandText = @"
SELECT m.LastIp, m.FriendlyName, r.State, r.ProcessFirstSeenUtc, r.SampleCount, r.UpdatedUtc,
       (SELECT MAX(SampledAtUtc) FROM FleetMetricSnapshots s WHERE s.MachineId=m.Id) AS lastSnap
FROM TuflowBehaviourRuns r JOIN Machines m ON m.Id=r.MachineId
WHERE m.Id IN (23,24,25) ORDER BY m.LastIp;";
using var r = cmd.ExecuteReader();
while (r.Read())
  Console.WriteLine($"{r[0]} {r[1]} state={r[2]} firstSeen={r[3]} samples={r[4]} runUpd={r[5]} lastSnap={r[6]}");
