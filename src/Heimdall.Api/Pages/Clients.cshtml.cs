using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace Heimdall.Api.Pages;

/// <summary>Legacy route — redirects to <see cref="ClientVersionModel"/> under Machines.</summary>
public class ClientsModel : PageModel
{
    public IActionResult OnGet() => RedirectToPage("/ClientVersion");

    public IActionResult OnPost() => RedirectToPage("/ClientVersion");

    public IActionResult OnPostSetPublishedVersion() => RedirectToPage("/ClientVersion");
}
