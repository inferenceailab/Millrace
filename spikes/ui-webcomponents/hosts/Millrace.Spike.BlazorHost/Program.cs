using Microsoft.AspNetCore.Components.Web;
using Millrace.Spike.BlazorHost;
using Millrace.Spike.BlazorHost.Components;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddRazorComponents().AddInteractiveServerComponents();

// A real dashboard behind the element, so the spike measures hosting a live component rather than
// one pointed at a mock.
builder.Services.AddMillrace(m => m.UseInMemoryStorage());
builder.Services.AddMillraceDashboard();
builder.Services.AddMillraceDashboardAuthorization((_, _) => ValueTask.FromResult(true));
builder.Services.AddHostedService<SampleData>();

var app = builder.Build();

app.UseStaticFiles();
app.UseAntiforgery();
app.MapMillraceDashboard("/millrace");
app.MapRazorComponents<App>().AddInteractiveServerRenderMode();

app.Run();
