using System.Runtime.InteropServices;
using Marbots.Runtime;
using Marbots.Server.Api;
using Marbots.Server.Components;
using Marbots.Server.Services;
using Microsoft.AspNetCore.DataProtection;

var builder = WebApplication.CreateBuilder(args);
// Serve wwwroot + framework assets when running from source in any environment (no-op for published output).
builder.WebHost.UseStaticWebAssets();

var options = builder.Configuration.GetSection("Marbots").Get<MarbotsOptions>() ?? new MarbotsOptions();
options.DataDirectory = Path.GetFullPath(options.DataDirectory, builder.Environment.ContentRootPath);
// Like Claude Code's --dangerously-skip-permissions: start with approvals skipped (can be turned off in Settings).
if (args.Contains("--dangerously-skip-approvals")) options.DangerouslySkipApprovals = true;
Directory.CreateDirectory(options.DataDirectory);

var dp = builder.Services.AddDataProtection()
    .SetApplicationName("Marbots")
    .PersistKeysToFileSystem(new DirectoryInfo(Path.Combine(options.DataDirectory, "keys")));
if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows)) dp.ProtectKeysWithDpapi();

builder.Services.AddMarbotsRuntime(options);
builder.Services.AddOpenApi();
builder.Services.ConfigureHttpJsonOptions(o =>
{
    o.SerializerOptions.TypeInfoResolverChain.Insert(0, Marbots.Abstractions.MarbotsJsonContext.Default);
    o.SerializerOptions.Converters.Add(new System.Text.Json.Serialization.JsonStringEnumConverter());
});
builder.Services.AddRazorComponents().AddInteractiveServerComponents();
builder.Services.AddScoped<UiText>();
builder.Services.AddSingleton<MarkdownRenderer>();
builder.Services.AddSingleton<UsageService>();

var app = builder.Build();

if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/error", createScopeForErrors: true);
}
app.UseMiddleware<ApiKeyMiddleware>();
app.UseAntiforgery();
app.MapStaticAssets();
app.MapOpenApi();
app.MapMarbotsApi();
app.MapA2a();
app.MapRazorComponents<App>().AddInteractiveServerRenderMode();

app.Run();

public partial class Program;
