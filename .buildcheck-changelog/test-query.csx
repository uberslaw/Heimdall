using Heimdall.Api.Data;
using Microsoft.EntityFrameworkCore;

var options = new DbContextOptionsBuilder<HeimdallDbContext>()
    .UseSqlite("Data Source=C:\\ProgramData\\Heimdall\\heimdall.db")
    .Options;
await using var db = new HeimdallDbContext(options);
var count = await db.AppListAuditLogs.AsNoTracking().CountAsync();
var logs = await db.AppListAuditLogs.AsNoTracking()
    .OrderByDescending(l => l.Id)
    .Take(5)
    .ToListAsync();
Console.WriteLine($"Count={count}, fetched={logs.Count}");
foreach (var l in logs)
    Console.WriteLine($"{l.Id} {l.Utc:u} {l.Action}");
