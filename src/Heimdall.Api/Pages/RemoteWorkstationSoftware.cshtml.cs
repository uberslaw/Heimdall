using Heimdall.Api.Data;
using Heimdall.Api.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;

namespace Heimdall.Api.Pages;

/// <summary>Admin: approve / reject / add software capability tags for the public remote pool.</summary>
public class RemoteWorkstationSoftwareModel(
    MachineSoftwareCapabilityService software,
    HeimdallDbContext db,
    StaffAccessGuard guard) : PageModel
{
    public IReadOnlyList<MachineSoftwareCapabilityService.CapabilityRow> Rows { get; private set; } = [];
    public IReadOnlyList<(int Id, string Hostname)> PoolMachines { get; private set; } = [];
    public bool Allowed { get; private set; }

    [BindProperty]
    public int CapId { get; set; }

    [BindProperty]
    public int ManualMachineId { get; set; }

    [BindProperty]
    public string ManualLabel { get; set; } = "";

    public IActionResult OnGet() =>
        RedirectToPage("/RemoteWorkstations");

    public async Task<IActionResult> OnPostDetectAsync(CancellationToken ct)
    {
        if (!await EnsureAdminAsync())
            return Page();
        var (proposed, skipped) = await software.ProposeFromCatalogAsync(ct);
        TempData["Message"] = $"Detection finished: {proposed} new pending proposal(s), {skipped} skipped.";
        return RedirectToPage();
    }

    public async Task<IActionResult> OnPostApproveAsync(CancellationToken ct)
    {
        if (!await EnsureAdminAsync())
            return Page();
        var who = guard.TryGetVerifiedEmail(HttpContext) ?? "admin";
        var result = await software.TrySetStatusAsync(CapId, MachineSoftwareCapabilityStatus.Approved, who, ct);
        TempData[result.Ok ? "Message" : "Error"] = result.Message;
        return RedirectToPage();
    }

    public async Task<IActionResult> OnPostRejectAsync(CancellationToken ct)
    {
        if (!await EnsureAdminAsync())
            return Page();
        var who = guard.TryGetVerifiedEmail(HttpContext) ?? "admin";
        var result = await software.TrySetStatusAsync(CapId, MachineSoftwareCapabilityStatus.Rejected, who, ct);
        TempData[result.Ok ? "Message" : "Error"] = result.Message;
        return RedirectToPage();
    }

    public async Task<IActionResult> OnPostDeleteAsync(CancellationToken ct)
    {
        if (!await EnsureAdminAsync())
            return Page();
        var result = await software.TryDeleteAsync(CapId, ct);
        TempData[result.Ok ? "Message" : "Error"] = result.Message;
        return RedirectToPage();
    }

    public async Task<IActionResult> OnPostAddManualAsync(CancellationToken ct)
    {
        if (!await EnsureAdminAsync())
            return Page();
        var who = guard.TryGetVerifiedEmail(HttpContext) ?? "admin";
        var result = await software.TryAddManualAsync(ManualMachineId, ManualLabel, who, ct);
        TempData[result.Ok ? "Message" : "Error"] = result.Message;
        return RedirectToPage();
    }

    private Task<bool> EnsureAdminAsync()
    {
        Allowed = guard.IsConfiguredAdmin(HttpContext);
        if (!Allowed && guard.IsDevBypassActive)
            Allowed = true;
        if (!Allowed)
            TempData["Error"] = "Admin only (Heimdall:StaffAccess:AdminEmails).";
        return Task.FromResult(Allowed);
    }

    private async Task LoadAsync(CancellationToken ct)
    {
        Rows = await software.ListAllAsync(ct);
        PoolMachines = (await db.Machines.AsNoTracking()
                .Where(m => m.TeamId != null && m.Team != null && m.Team.IsPublicFacing)
                .OrderBy(m => m.Hostname)
                .Select(m => new { m.Id, m.Hostname })
                .ToListAsync(ct))
            .Select(m => (m.Id, m.Hostname))
            .ToList();
    }
}
