using Microsoft.AspNetCore.Components.Web;
using Microsoft.AspNetCore.Components.WebAssembly.Hosting;
using Millrace.Dashboard.Ui.Blazor.App;

var builder = WebAssemblyHostBuilder.CreateDefault(args);
builder.RootComponents.Add<App>("#app");
builder.RootComponents.Add<HeadOutlet>("head::after");

var apiBase = DashboardClient.ApiBaseFrom(builder.HostEnvironment.BaseAddress);
builder.Services.AddScoped(_ => new DashboardClient(new HttpClient(), apiBase));

await builder.Build().RunAsync();
