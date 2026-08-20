using Heimdall.Api.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace Heimdall.Api.Pages;

/// <summary>
/// Admin → Access lists: view/edit named email allowlists (Full Flood, Flood Live, …).
/// Site admins remain appsettings-only.
/// </summary>
public class AccessListsModel(
    AccessAllowlistService allowlists,
    StaffAccessGuard guard,
    WindowsStaffIdentityService identity) : PageModel
{
    public bool Allowed { get; private set; }
    public string? WindowsPrincipal { get; private set; }
    public IReadOnlyList<string> CandidateEmails { get; private set; } = [];
    public IReadOnlyList<string> ConfiguredAdminEmails { get; private set; } = [];
    public IReadOnlyList<AccessListPanelVm> Panels { get; private set; } = [];

    [BindProperty]
    public string ListId { get; set; } = "";

    [BindProperty]
    public string? EmailsInput { get; set; }

    public async Task<IActionResult> OnGetAsync()
    {
        // Negotiate challenge so IsConfiguredAdmin can see DOMAIN\user → email candidates.
        if (!await guard.EnsureWindowsAuthAsync(HttpContext))
            return new EmptyResult();

        if (!EnsureAdmin())
            return Page();

        LoadPanels();
        return Page();
    }

    public async Task<IActionResult> OnPostSaveAsync(CancellationToken ct)
    {
        if (!await guard.EnsureWindowsAuthAsync(HttpContext))
            return new EmptyResult();

        if (!EnsureAdmin())
            return Page();

        var def = AccessAllowlistCatalog.TryGet(ListId);
        if (def is null || !def.Editable)
        {
            TempData["Error"] = "That access list cannot be edited here.";
            LoadPanels();
            return Page();
        }

        var emails = AccessAllowlistService.ParseEmailLines(EmailsInput);
        await allowlists.SaveEmailsAsync(def.Id, emails, ct);
        TempData["Message"] = $"{def.Title} list saved ({emails.Count} email{(emails.Count == 1 ? "" : "s")}). Takes effect immediately.";
        return RedirectToPage();
    }

    private bool EnsureAdmin()
    {
        WindowsPrincipal = identity.GetWindowsPrincipalName(HttpContext);
        CandidateEmails = identity.GetCandidateEmails(HttpContext);
        ConfiguredAdminEmails = guard.Options.AdminEmails ?? [];

        Allowed = guard.IsConfiguredAdmin(HttpContext);
        if (!Allowed)
        {
            if (WindowsPrincipal is null)
            {
                TempData["Error"] =
                    "Windows login was not detected for this request. Open the page again and accept the Windows auth prompt if shown, " +
                    "or use the same PC account that matches AdminEmails.";
            }
            else
            {
                TempData["Error"] =
                    "Admin only (Heimdall:StaffAccess:AdminEmails). " +
                    "Your Windows login did not match a configured admin email.";
            }
        }

        return Allowed;
    }

    private void LoadPanels()
    {
        Panels = AccessAllowlistCatalog.All
            .Select(d =>
            {
                var emails = allowlists.GetEmails(d.Id);
                return new AccessListPanelVm(
                    d.Id,
                    d.Title,
                    d.GrantsDescription,
                    d.Editable,
                    d.ConfigPath,
                    string.Join(Environment.NewLine, emails),
                    emails.Count,
                    allowlists.HasDbOverride(d.Id));
            })
            .ToList();
    }

    public sealed record AccessListPanelVm(
        string Id,
        string Title,
        string GrantsDescription,
        bool Editable,
        string ConfigPath,
        string EmailsText,
        int EmailCount,
        bool FromDatabase);
}
