using Heimdall.Api.Data;
using Heimdall.Api.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;

namespace Heimdall.Api.Pages;

public class MachineUtilDrilldownModel(HeimdallDbContext db, MachineUtilisationService util) : PageModel
{
    [BindProperty(SupportsGet = true)]
    public string? Hostname { get; set; }

    [BindProperty(SupportsGet = true)]
    public int? MachineId { get; set; }

    [BindProperty(SupportsGet = true)]
    public string Period { get; set; } = "7d";

    [BindProperty(SupportsGet = true)]
    public string Metric { get; set; } = "active";

    public MachineUtilisationService.UtilDrilldown? Drilldown { get; private set; }

    public async Task<IActionResult> OnGetAsync(CancellationToken ct)
    {
        Period = MachineUtilisationService.NormalizePeriod(Period);
        Metric = MachineUtilisationService.NormalizeMetric(Metric);

        int? id = MachineId;
        if (id is null && !string.IsNullOrWhiteSpace(Hostname))
        {
            var host = Hostname.Trim();
            id = await db.Machines.AsNoTracking()
                .Where(m => m.Hostname == host)
                .Select(m => (int?)m.Id)
                .FirstOrDefaultAsync(ct);
        }

        if (id is null)
            return NotFound();

        Drilldown = await util.GetDrilldownAsync(id.Value, Period, Metric, ct);
        if (Drilldown is null)
            return NotFound();

        return Page();
    }
}
