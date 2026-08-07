using Heimdall.Api.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace Heimdall.Api.Pages;

public class AuthModel(
    DirectoryAuthSettingsService authSettings,
    EntraGraphService graph,
    EntraSecretStore secrets) : PageModel
{
    public DirectoryAuthSettings Settings { get; private set; } = new(true, false);
    public bool EntraSecretsConfigured => graph.IsConfigured;
    public bool EntraSecretsFilePresent => secrets.FileExists;
    public string SecretsPath => secrets.SecretsPath;
    public EntraProbeResult? LastProbe { get; private set; }

    [BindProperty]
    public bool ManualCsvMembershipEnabled { get; set; } = true;

    [BindProperty]
    public bool EntraGraphMembershipEnabled { get; set; }

    public async Task OnGetAsync()
    {
        await LoadAsync();
    }

    public async Task<IActionResult> OnPostSaveAsync()
    {
        await authSettings.SaveAsync(
            new DirectoryAuthSettings(ManualCsvMembershipEnabled, EntraGraphMembershipEnabled),
            HttpContext.RequestAborted);
        TempData["Message"] = "Auth settings saved.";
        return RedirectToPage();
    }

    public async Task<IActionResult> OnPostProbeEntraAsync()
    {
        await LoadAsync();
        LastProbe = await graph.ProbeAsync(HttpContext.RequestAborted);
        TempData["Message"] = LastProbe.Message;
        if (!LastProbe.GroupReadOk)
            TempData["Error"] = LastProbe.TokenOk
                ? "Credentials OK — Graph group permission still missing or denied."
                : "Entra probe did not fully succeed.";
        return Page();
    }

    private async Task LoadAsync()
    {
        Settings = await authSettings.GetAsync(HttpContext.RequestAborted);
        ManualCsvMembershipEnabled = Settings.ManualCsvMembershipEnabled;
        EntraGraphMembershipEnabled = Settings.EntraGraphMembershipEnabled;
    }
}
