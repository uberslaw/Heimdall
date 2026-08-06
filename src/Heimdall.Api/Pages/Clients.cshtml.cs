using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace Heimdall.Api.Pages;

/// <summary>Legacy route — redirects to <see cref="ClientVersionModel"/> under Machines.</summary>
public class ClientsModel : PageModel
{
    public IActionResult OnGet() => Redirect("/Fleet?tab=clients");

    public IActionResult OnPost() => Redirect("/Fleet?tab=clients");

    public IActionResult OnPostSetPublishedVersion() => Redirect("/Fleet?tab=clients");
}
