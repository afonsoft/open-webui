using Microsoft.AspNetCore.Components.Web;
using Microsoft.AspNetCore.Components.WebAssembly.Hosting;
using OpenWebUI.Client;
using OpenWebUI.Client.Services;

var builder = WebAssemblyHostBuilder.CreateDefault(args);
builder.RootComponents.Add<App>("#app");
builder.RootComponents.Add<HeadOutlet>("head::after");

builder.Services.AddScoped(sp => new HttpClient
{
    BaseAddress = new Uri(builder.HostEnvironment.BaseAddress),
});

builder.Services.AddScoped<BrowserStorage>();
builder.Services.AddScoped<AuthService>();
builder.Services.AddScoped<ApiService>();
builder.Services.AddScoped<ChatStreamService>();
builder.Services.AddScoped<MarkdownService>();
builder.Services.AddScoped<ThemeService>();
builder.Services.AddScoped<ChatListState>();
builder.Services.AddScoped<RealtimeService>();
builder.Services.AddScoped<ToastService>();
builder.Services.AddScoped<ChatNotificationsService>();
builder.Services.AddScoped<LocalizationService>();

var host = builder.Build();
await host.Services.GetRequiredService<LocalizationService>().InitializeAsync();
await host.RunAsync();
