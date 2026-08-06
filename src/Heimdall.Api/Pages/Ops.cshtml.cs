using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace Heimdall.Api.Pages;

/// <summary>Legacy /Ops URL — redirects to <see cref="FleetModel"/> preserving query (e.g. ?tab=).</summary>
public class OpsModel : PageModel
{
    public IActionResult OnGet() => RedirectToFleet();

    public IActionResult OnPost() => RedirectToFleet();

    private IActionResult RedirectToFleet()
    {
        var qs = Request.QueryString.HasValue ? Request.QueryString.Value : "";
        return Redirect("/Fleet" + qs);
    }
}
