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
    WindowsStaffIdentityService identity,
    ILogger<AccessListsModel> logger) : PageModel
{
    public bool Allowed { get; private set; }
    public string? WindowsPrincipal { get; private set; }
    public IReadOnlyList<string> CandidateEmails { get; private set; } = [];
    public IReadOnlyList<string> ConfiguredAdminEmails { get; private set; } = [];
    public IReadOnlyList<AccessListPanelVm> Panels { get; private set; } = [];

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

    /// <summary>Save Full Flood team list (same admin bar as Flood Live).</summary>
    public Task<IActionResult> OnPostSaveFloodFullAsync(string? emailsInput, CancellationToken ct) =>
        SaveEditableListAsync(AccessAllowlistCatalog.FloodFull, emailsInput, ct);

    /// <summary>Save Flood Live-only list.</summary>
    public Task<IActionResult> OnPostSaveFloodLiveAsync(string? emailsInput, CancellationToken ct) =>
        SaveEditableListAsync(AccessAllowlistCatalog.FloodLive, emailsInput, ct);

    private async Task<IActionResult> SaveEditableListAsync(string listId, string? emailsInput, CancellationToken ct)
    {
        if (!await guard.EnsureWindowsAuthAsync(HttpContext))
            return new EmptyResult();

        if (!EnsureAdmin())
            return Page();

        var def = AccessAllowlistCatalog.TryGet(listId);
        if (def is null || !def.Editable)
        {
            TempData["Error"] = "That access list cannot be edited here.";
            LoadPanels();
            return Page();
        }

        try
        {
            var emails = AccessAllowlistService.ParseEmailLines(emailsInput);
            await allowlists.SaveEmailsAsync(def.Id, emails, ct);
            TempData["Message"] =
                $"{def.Title} list saved ({emails.Count} email{(emails.Count == 1 ? "" : "s")}). Takes effect immediately.";
            return RedirectToPage();
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to save access list {ListId}", def.Id);
            TempData["Error"] = $"Could not save {def.Title}: {ex.Message}";
            LoadPanels();
            return Page();
        }
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
                var saveHandler = d.Id switch
                {
                    AccessAllowlistCatalog.FloodFull => "SaveFloodFull",
                    AccessAllowlistCatalog.FloodLive => "SaveFloodLive",
                    _ => null
                };
                return new AccessListPanelVm(
                    d.Id,
                    d.Title,
                    d.GrantsDescription,
                    d.Editable,
                    d.ConfigPath,
                    string.Join(Environment.NewLine, emails),
                    emails.Count,
                    allowlists.HasDbOverride(d.Id),
                    saveHandler);
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
        bool FromDatabase,
        string? SaveHandler);
}
