using Heimdall.Api.Data;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;

namespace Heimdall.Api.Pages;

public class AppListChangelogModel(HeimdallDbContext db) : PageModel
{
    public const int PageSize = 50;

    public IReadOnlyList<AppListAuditLog> Logs { get; private set; } = [];

    [FromQuery(Name = "page")]
    public int PageNumber { get; set; } = 1;
    public int TotalCount { get; private set; }
    public int TotalPages => TotalCount == 0 ? 1 : (int)Math.Ceiling(TotalCount / (double)PageSize);

    public async Task OnGetAsync()
    {
        if (PageNumber < 1)
            PageNumber = 1;
        TotalCount = await db.AppListAuditLogs.AsNoTracking().CountAsync(HttpContext.RequestAborted);
        if (PageNumber > TotalPages)
            PageNumber = TotalPages;

        var skip = (PageNumber - 1) * PageSize;
        // SQLite can't translate ORDER BY over DateTimeOffset (see SessionDrilldownService).
        // Audit rows are append-only with AUTOINCREMENT Id, so Id order matches reverse-chronological.
        Logs = await db.AppListAuditLogs.AsNoTracking()
            .OrderByDescending(l => l.Id)
            .Skip(skip)
            .Take(PageSize)
            .ToListAsync(HttpContext.RequestAborted);
    }
}
