using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using NOF.Hosting;
using NOF.Hosting.AspNetCore;
using Quantum.Platform;
using Quantum.Platform.Application;
using Quantum.Platform.Application.Handlers;
using Quantum.Platform.Authentication;
using Quantum.Platform.Contract;
using Quantum.Platform.UI.Components;

var builder = NOFWebApplicationBuilder.Create(args);

builder.AddApplicationPart(typeof(IQuantumPlatformService).Assembly);
builder.AddApplicationPart(typeof(RegisterUser).Assembly);
builder.AddRpcServer<QuantumPlatformService>();
builder.Services.AddQuantumPlatformApplication();
builder.Services.AddQuantumPlatformServices(builder.Configuration);
builder.AddQuantumPlatformAuthentication();
builder.AddQuantumPlatformPostgreSql();
builder.Services.Configure<BootstrapAdminOptions>(
    builder.Configuration.GetSection(BootstrapAdminOptions.SectionName));
builder.Services.AddInitializationStep<BootstrapAdminInitializationStep>();
builder.Services.AddHealthChecks();
builder.Services.AddRazorComponents()
    .AddInteractiveWebAssemblyComponents();

var app = await builder.BuildAsync();

app.MapHealthChecks("/health/live", new HealthCheckOptions { Predicate = static _ => false });
if (app.Environment.IsDevelopment())
{
    app.UseWebAssemblyDebugging();
}

app.UseDefaultFiles();
app.UseStaticFiles();
app.UseAuthentication();
app.UseAuthorization();
app.UseAntiforgery();
app.MapStaticAssets();
app.MapRazorComponents<App>()
    .AddInteractiveWebAssemblyRenderMode()
    .AddAdditionalAssemblies(typeof(Routes).Assembly);
app.MapGet("/api/status", () => Results.Ok(new
{
    service = "Quantum Platform",
    protocol = "JSON-RPC 2.0",
    endpoint = "/rpc",
    portal = "/portal/"
}));

await app.RunAsync();
