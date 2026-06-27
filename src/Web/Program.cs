using Microsoft.AspNetCore.Components.WebAssembly.Hosting;
using MudBlazor.Services;
using SdkInfoApp.Web.Services;

var builder = WebAssemblyHostBuilder.CreateDefault(args);
builder.RootComponents.Add<SdkInfoApp.Web.App>("#app");

builder.Services.AddMudServices();

// BaseAddress is relative so it works both locally and on gh-pages
builder.Services.AddScoped(sp =>
{
    var baseUri = new Uri(builder.HostEnvironment.BaseAddress);
    return new HttpClient { BaseAddress = baseUri };
});

builder.Services.AddScoped<SnapshotService>();
builder.Services.AddScoped<ThemeService>();

await builder.Build().RunAsync();
