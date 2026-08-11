using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace Heimdall.Api.Pages;

/// <summary>Track Software was removed — App lists are the apply/ignore tracking UI.</summary>
public class TrackSoftwareModel : PageModel
{
    public IActionResult OnGet() =>
        RedirectToPage("/AppLists", new { tab = "create" });
}
