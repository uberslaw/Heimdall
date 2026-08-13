using Heimdall.Api.Services;
using Heimdall.Shared;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace Heimdall.Api.Pages;

public class DiagnosticsModel(DiagnosticBundleService diagnostics) : PageModel
{
    public string ApiLogsDir { get; private set; } = HeimdallLogPaths.ApiLogsDir;
    public string OpsLogsDir { get; private set; } = HeimdallLogPaths.OpsLogsDir;
    public string LogsRoot { get; private set; } = HeimdallLogPaths.LogsRoot;
    public string DumpRoot { get; private set; } = HeimdallLogPaths.DiagnosticsDumpRoot;

    public string? LastBundleDirectory { get; private set; }
    public string? LastZipPath { get; private set; }

    public IActionResult OnPostCollect()
    {
        try
        {
            var result = diagnostics.Collect();
            LastBundleDirectory = result.BundleDirectory;
            LastZipPath = result.ZipPath;
            TempData["Message"] = result.Message;
            TempData["BundleDirectory"] = result.BundleDirectory;
            TempData["ZipPath"] = result.ZipPath;
        }
        catch (Exception ex)
        {
            TempData["Error"] = "Collect failed: " + ex.Message;
        }

        return Page();
    }

    public void OnGet()
    {
        LastBundleDirectory = TempData["BundleDirectory"] as string;
        LastZipPath = TempData["ZipPath"] as string;
    }
}
