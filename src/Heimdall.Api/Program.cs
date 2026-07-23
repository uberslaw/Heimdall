using Heimdall.Api.Data;
using Heimdall.Api.Services;
using Heimdall.Shared.Contracts;
using Microsoft.EntityFrameworkCore;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddRazorPages();
builder.Services.AddDbContext<HeimdallDbContext>(options =>
{
    var dbPath = builder.Configuration.GetConnectionString("Heimdall")
                 ?? $"Data Source={Path.Combine(builder.Environment.ContentRootPath, "heimdall.db")}";
    options.UseSqlite(dbPath.StartsWith("Data Source=", StringComparison.OrdinalIgnoreCase)
        ? dbPath
        : $"Data Source={dbPath}");
});
builder.Services.AddScoped<IngestService>();
builder.Services.AddScoped<ConfigService>();
builder.Services.AddScoped<StatsQueryService>();
builder.Services.AddScoped<SocratizeQueryService>();

var app = builder.Build();

using (var scope = app.Services.CreateScope())
{
    var db = scope.ServiceProvider.GetRequiredService<HeimdallDbContext>();
    await SeedData.EnsureSeededAsync(db);
}

if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Error");
    app.UseHsts();
}

// HTTP-only is fine for local POC; enable HTTPS redirection when terminating TLS.
if (app.Configuration.GetValue("Heimdall:UseHttpsRedirection", false))
    app.UseHttpsRedirection();
app.UseStaticFiles();
app.UseRouting();
app.UseAuthorization();
app.MapRazorPages();

var apiKey = builder.Configuration["Heimdall:ApiKey"] ?? "heimdall-poc-key";

bool IsAuthorized(HttpRequest request) =>
    request.Headers.TryGetValue("X-Heimdall-Key", out var key) &&
    string.Equals(key.ToString(), apiKey, StringComparison.Ordinal);

app.MapPost("/api/ingest", async (IngestBatchDto batch, IngestService ingest, HttpRequest request) =>
{
    if (!IsAuthorized(request))
        return Results.Unauthorized();

    await ingest.IngestAsync(batch, request.HttpContext.RequestAborted);
    return Results.Accepted();
});

app.MapGet("/api/config/{hostname}", async (string hostname, ConfigService config, HttpRequest request) =>
{
    if (!IsAuthorized(request))
        return Results.Unauthorized();

    var dto = await config.ResolveForHostAsync(hostname, request.HttpContext.RequestAborted);
    return Results.Ok(dto);
});

app.MapGet("/api/health", () => Results.Ok(new { status = "ok", service = "Heimdall" }));

app.Run();
