using TemporalOpsBlazor.Components;
using TemporalOpsBlazor.Models;
using TemporalOpsBlazor.Services;

var builder = WebApplication.CreateBuilder(args);

builder.Services
    .AddRazorComponents()
    .AddInteractiveServerComponents();

builder.Services.Configure<TemporalConnectionSettings>(builder.Configuration.GetSection("Temporal"));
builder.Services.AddSingleton<TemporalClientProvider>();

var useMock = builder.Configuration.GetValue("Temporal:UseMock", false);
if (useMock)
{
    builder.Services.AddSingleton<ITemporalOperationsService, MockTemporalOperationsService>();
}
else
{
    builder.Services.AddSingleton<ITemporalOperationsService, TemporalOperationsService>();
}

var app = builder.Build();

if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Error", createScopeForErrors: true);
    app.UseHsts();
}

app.UseHttpsRedirection();
app.UseStaticFiles();
app.UseAntiforgery();

app.MapRazorComponents<App>()
    .AddInteractiveServerRenderMode();

app.Run();
