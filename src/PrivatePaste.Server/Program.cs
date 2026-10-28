using PrivatePaste.Server.Config;
using PrivatePaste.Server.Storage;

var builder = WebApplication.CreateBuilder(args);

if (string.IsNullOrEmpty(builder.Configuration["ASPNETCORE_URLS"]) && string.IsNullOrEmpty(builder.Configuration["urls"]))
    builder.WebHost.UseUrls("http://+:8080");

builder.Services.AddSingleton(ServerOptions.FromConfiguration(builder.Configuration));
builder.Services.AddSingleton<PrivatePasteDb>();
builder.Services.AddHealthChecks();

var app = builder.Build();

try
{
    app.Services.GetRequiredService<PrivatePasteDb>().Initialize();
}
catch (DatabaseKeyMismatchException ex)
{
    app.Logger.LogCritical(ex, "{Message}", ex.Message);
    Environment.Exit(1);
}

app.MapHealthChecks("/health");

app.Run();

public partial class Program;
