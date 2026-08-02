using Heimdall.Api.Data;

using Heimdall.Api.Services;

using Microsoft.AspNetCore.Mvc;

using Microsoft.AspNetCore.Mvc.RazorPages;



namespace Heimdall.Api.Pages;



/// <summary>

/// Staff sign-in landing page. When Windows auth is enabled, access is tied to the browser's Windows login;

/// registered emails must match that identity (see WindowsStaffIdentityService). Auto sign-in when one

/// candidate email is in a Remote Access Group.

/// </summary>

public class StaffAccessModel(

    RemoteAccessGroupService groups,

    StaffAccessGuard guard,

    WindowsStaffIdentityService identity) : PageModel

{

    public string? SignedInEmail { get; private set; }

    public List<RemoteAccessGroup> MyGroups { get; private set; } = [];

    public string? WindowsUser { get; private set; }

    public bool DevBypassActive { get; private set; }

    public bool WindowsAuthRequired { get; private set; }



    [BindProperty]

    public string Email { get; set; } = "";



    public async Task<IActionResult> OnGetAsync()

    {

        DevBypassActive = guard.IsDevBypassActive;

        WindowsAuthRequired = guard.IsWindowsAuthRequired;



        if (!await guard.EnsureWindowsAuthAsync(HttpContext))

            return new EmptyResult();



        WindowsUser = identity.GetWindowsPrincipalName(HttpContext);



        SignedInEmail = guard.TryGetVerifiedEmail(HttpContext);

        if (SignedInEmail is not null)

        {

            MyGroups = await groups.FindGroupsForEmailAsync(SignedInEmail, HttpContext.RequestAborted);

            return Page();

        }



        var autoEmail = await guard.TryResolveEmailFromWindowsAsync(HttpContext, groups, HttpContext.RequestAborted);

        if (autoEmail is not null)

        {

            StaffAuthService.SignIn(HttpContext, autoEmail);

            var autoGroups = await groups.FindGroupsForEmailAsync(autoEmail, HttpContext.RequestAborted);

            if (autoGroups.Count == 1)

                return RedirectToPage("/Staff", new { id = autoGroups[0].Id });



            TempData["Message"] = $"Signed in as {autoEmail} (matched your Windows login). Pick a group below.";

            return RedirectToPage();

        }



        if (WindowsUser is not null && guard.IsWindowsAuthRequired)

            Email = identity.GetCandidateEmails(HttpContext).FirstOrDefault() ?? "";



        return Page();

    }



    public async Task<IActionResult> OnPostSignInAsync()

    {

        DevBypassActive = guard.IsDevBypassActive;

        WindowsAuthRequired = guard.IsWindowsAuthRequired;



        if (!await guard.EnsureWindowsAuthAsync(HttpContext))

            return new EmptyResult();



        WindowsUser = identity.GetWindowsPrincipalName(HttpContext);



        if (string.IsNullOrWhiteSpace(Email) || !Email.Contains('@'))

        {

            TempData["Error"] = "Enter a valid email address.";

            return RedirectToPage();

        }



        if (!guard.CanSignInWithEmail(HttpContext, Email))

        {

            var who = WindowsStaffIdentityService.FormatDisplayName(WindowsUser);

            TempData["Error"] =

                $"That email does not match your Windows login ({who}). You can only sign in as yourself — ask an admin to register the email that matches your account.";

            return RedirectToPage();

        }



        var matches = await groups.FindGroupsForEmailAsync(Email, HttpContext.RequestAborted);

        if (matches.Count == 0)

        {

            TempData["Error"] =

                $"No Remote Access Group found for {Email.Trim()}. Ask an admin to add this email under Admin → Remote Access Groups.";

            return RedirectToPage();

        }



        StaffAuthService.SignIn(HttpContext, Email);



        if (matches.Count == 1)

            return RedirectToPage("/Staff", new { id = matches[0].Id });



        TempData["Message"] = $"Signed in as {Email.Trim()}. You belong to {matches.Count} groups — pick one below.";

        return RedirectToPage();

    }



    public IActionResult OnPostSignOut()

    {

        StaffAuthService.SignOut(HttpContext);

        return RedirectToPage();

    }

}


