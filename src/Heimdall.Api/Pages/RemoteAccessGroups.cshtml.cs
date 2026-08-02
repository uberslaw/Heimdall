using Heimdall.Api.Data;
using Heimdall.Api.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;

namespace Heimdall.Api.Pages;

public class RemoteAccessGroupsModel(HeimdallDbContext db, RemoteAccessGroupService groups) : PageModel
{
    public List<RemoteAccessGroup> Groups { get; private set; } = [];
    public RemoteAccessGroup? EditingGroup { get; private set; }
    public List<string> AllMachineHostnames { get; private set; } = [];
    public List<string> DiscoveredProcessNames { get; private set; } = [];

    [BindProperty]
    public int? EditingGroupId { get; set; }

    [BindProperty]
    public string GroupName { get; set; } = "";

    [BindProperty]
    public int GroupId { get; set; }

    [BindProperty]
    public string? EmailsInput { get; set; }

    [BindProperty]
    public int StaffId { get; set; }

    [BindProperty]
    public List<string> SelectedMachines { get; set; } = [];

    [BindProperty]
    public int GroupMachineId { get; set; }

    [BindProperty]
    public bool FavoritesOnly { get; set; }

    [BindProperty]
    public string? FavoriteProcessInput { get; set; }

    [BindProperty]
    public int FavoriteId { get; set; }

    public async Task OnGetAsync(int? edit)
    {
        await LoadAsync(edit);
    }

    public async Task<IActionResult> OnPostCreateGroupAsync()
    {
        if (string.IsNullOrWhiteSpace(GroupName))
        {
            TempData["Error"] = "Group name is required.";
            return RedirectToPage();
        }

        var group = await groups.CreateGroupAsync(GroupName, HttpContext.RequestAborted);
        TempData["Message"] = $"Created group “{group.Name}”. Add staff emails and machines below.";
        return RedirectToPage(null, new { edit = group.Id });
    }

    public async Task<IActionResult> OnPostRenameGroupAsync()
    {
        if (string.IsNullOrWhiteSpace(GroupName))
        {
            TempData["Error"] = "Group name is required.";
            return RedirectToPage(null, new { edit = GroupId });
        }

        await groups.RenameGroupAsync(GroupId, GroupName, HttpContext.RequestAborted);
        TempData["Message"] = "Group renamed.";
        return RedirectToPage(null, new { edit = GroupId });
    }

    public async Task<IActionResult> OnPostDeleteGroupAsync()
    {
        var ok = await groups.DeleteGroupAsync(GroupId, HttpContext.RequestAborted);
        TempData["Message"] = ok ? "Group deleted." : null;
        TempData["Error"] = ok ? null : "Group not found.";
        return RedirectToPage();
    }

    public async Task<IActionResult> OnPostAddStaffAsync()
    {
        var emails = RemoteAccessGroupService.SplitMultiValue(EmailsInput).ToList();
        if (emails.Count == 0)
        {
            TempData["Error"] = "Enter one or more staff emails (comma, semicolon, or newline separated).";
            return RedirectToPage(null, new { edit = GroupId });
        }

        var added = await groups.AddStaffEmailsAsync(GroupId, emails, HttpContext.RequestAborted);
        TempData["Message"] = added == 0
            ? "No new staff emails added (already present or invalid)."
            : $"Added {added} staff email(s).";
        return RedirectToPage(null, new { edit = GroupId });
    }

    public async Task<IActionResult> OnPostRemoveStaffAsync()
    {
        await groups.RemoveStaffAsync(StaffId, HttpContext.RequestAborted);
        return RedirectToPage(null, new { edit = GroupId });
    }

    public async Task<IActionResult> OnPostAddMachinesAsync()
    {
        if (SelectedMachines.Count == 0)
        {
            TempData["Error"] = "Select one or more machines to add.";
            return RedirectToPage(null, new { edit = GroupId });
        }

        var added = await groups.AddMachinesAsync(GroupId, SelectedMachines, HttpContext.RequestAborted);
        TempData["Message"] = added == 0
            ? "No new machines added (already present)."
            : $"Added {added} machine(s).";
        return RedirectToPage(null, new { edit = GroupId });
    }

    public async Task<IActionResult> OnPostRemoveMachineAsync()
    {
        await groups.RemoveMachineAsync(GroupMachineId, HttpContext.RequestAborted);
        return RedirectToPage(null, new { edit = GroupId });
    }

    public async Task<IActionResult> OnPostSetFavoritesOnlyAsync()
    {
        await groups.SetFavoritesOnlyAsync(GroupId, FavoritesOnly, HttpContext.RequestAborted);
        return RedirectToPage(null, new { edit = GroupId });
    }

    public async Task<IActionResult> OnPostAddFavoriteAsync()
    {
        var names = RemoteAccessGroupService.SplitMultiValue(FavoriteProcessInput).ToList();
        if (names.Count == 0)
        {
            TempData["Error"] = "Enter one or more process names (e.g. Revit, acad).";
            return RedirectToPage(null, new { edit = GroupId });
        }

        foreach (var name in names)
            await groups.AddFavoriteAsync(GroupId, name, HttpContext.RequestAborted);

        TempData["Message"] = $"Added {names.Count} favourite process(es).";
        return RedirectToPage(null, new { edit = GroupId });
    }

    public async Task<IActionResult> OnPostRemoveFavoriteAsync()
    {
        await groups.RemoveFavoriteAsync(FavoriteId, HttpContext.RequestAborted);
        return RedirectToPage(null, new { edit = GroupId });
    }

    private async Task LoadAsync(int? editId)
    {
        Groups = await groups.ListGroupsAsync(HttpContext.RequestAborted);
        AllMachineHostnames = await db.Machines.AsNoTracking()
            .OrderBy(m => m.Hostname)
            .Select(m => m.Hostname)
            .ToListAsync(HttpContext.RequestAborted);

        // Process names recently seen in ProcessRuns — convenience suggestions for the favourites input.
        var processNames = await db.ProcessRuns.AsNoTracking()
            .Select(p => p.ProcessName)
            .Distinct()
            .ToListAsync(HttpContext.RequestAborted);
        DiscoveredProcessNames = processNames
            .OrderBy(p => p, StringComparer.OrdinalIgnoreCase)
            .Take(200)
            .ToList();

        if (editId is int id)
        {
            EditingGroup = await groups.GetGroupAsync(id, HttpContext.RequestAborted);
            if (EditingGroup is not null)
            {
                EditingGroupId = EditingGroup.Id;
                GroupName = EditingGroup.Name;
                GroupId = EditingGroup.Id;
                FavoritesOnly = EditingGroup.FavoritesOnly;
            }
        }
    }
}
