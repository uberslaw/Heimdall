using Heimdall.Agent;
using Heimdall.Agent.Services;

var builder = Host.CreateApplicationBuilder(args);
builder.Services.AddWindowsService(options => options.ServiceName = "HeimdallAgent");

var apiBase = builder.Configuration["Heimdall:ApiBaseUrl"] ?? "http://localhost:5080";
builder.Services.AddHttpClient<HeimdallApiClient>(client =>
{
    client.BaseAddress = new Uri(apiBase);
    client.Timeout = TimeSpan.FromMinutes(15);
});

builder.Services.AddHostedService<Worker>();

var host = builder.Build();
host.Run();
