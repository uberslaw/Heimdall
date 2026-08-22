using Heimdall.Api.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace Heimdall.Api.Pages;

public class ApiHealthModel(ApiHealthService health, StaffAccessGuard guard) : PageModel
{
    public bool Allowed { get; private set; }
    public ApiHealthService.ApiHealthDashboard? Dashboard { get; private set; }

    public async Task<IActionResult> OnGetAsync(CancellationToken ct)
    {
        Allowed = guard.IsConfiguredAdmin(HttpContext);
        if (!Allowed)
            return Page();

        Dashboard = await health.BuildDashboardAsync(ct);
        return Page();
    }
}
