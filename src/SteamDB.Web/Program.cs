using ApexCharts;
using MudBlazor.Services;
using SteamDB.Infrastructure;
using SteamDB.Web.Components;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddRazorComponents()
    .AddInteractiveServerComponents();
builder.Services.AddMudServices();
builder.Services.AddApexCharts();
builder.Services.AddSteamAnalytics(builder.Environment.ContentRootPath);

var app = builder.Build();
await app.Services.PrepareSteamDbAsync();

if (!app.Environment.IsDevelopment())
    app.UseExceptionHandler("/Error", createScopeForErrors: true);

app.UseAntiforgery();
app.MapStaticAssets();
app.MapRazorComponents<App>()
    .AddInteractiveServerRenderMode();

app.Run();
