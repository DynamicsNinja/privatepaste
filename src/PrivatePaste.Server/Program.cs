using System.Text.Json.Serialization;
using System.Threading.RateLimiting;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.HttpOverrides;
using PrivatePaste.Server.Api;
using PrivatePaste.Server.Auth;
using PrivatePaste.Server.Clips;
using PrivatePaste.Server.Config;
using PrivatePaste.Server.Images;
using PrivatePaste.Server.Realtime;
using PrivatePaste.Server.Storage;

var builder = WebApplication.CreateBuilder(args);

if (string.IsNullOrEmpty(builder.Configuration["ASPNETCORE_URLS"]) && string.IsNullOrEmpty(builder.Configuration["urls"]))
    builder.WebHost.UseUrls("http://+:8080");

var serverOptions = ServerOptions.FromConfiguration(builder.Configuration);
var maxBodyBytes = serverOptions.MaxImageBytes + 1024 * 1024;

builder.WebHost.ConfigureKestrel(o => o.Limits.MaxRequestBodySize = maxBodyBytes);
builder.Services.Configure<FormOptions>(o => o.MultipartBodyLengthLimit = maxBodyBytes);

builder.Services.AddSingleton(serverOptions);
builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddSingleton<PrivatePasteDb>();
builder.Services.AddSingleton<ClipRepository>();
builder.Services.AddSingleton<BlobStore>();
builder.Services.AddSingleton<ImageProcessor>();
builder.Services.AddSingleton<PasswordVerifier>();
builder.Services.AddSingleton<DeviceRepository>();
builder.Services.AddSingleton<ConnectionHub>();
builder.Services.AddSingleton<IClipBroadcaster>(sp => sp.GetRequiredService<ConnectionHub>());
builder.Services.AddSingleton<ClipService>();
builder.Services.AddHealthChecks();

builder.Services.ConfigureHttpJsonOptions(o =>
    o.SerializerOptions.Converters.Add(new JsonStringEnumConverter(System.Text.Json.JsonNamingPolicy.CamelCase)));

builder.Services.AddAuthentication(AuthSchemes.DeviceToken)
    .AddScheme<AuthenticationSchemeOptions, DeviceTokenAuthHandler>(AuthSchemes.DeviceToken, null)
    .AddCookie(AuthSchemes.Cookies, o =>
    {
        o.Events.OnRedirectToLogin = ctx =>
        {
            if (ctx.Request.Path.StartsWithSegments("/api"))
                ctx.Response.StatusCode = StatusCodes.Status401Unauthorized;
            else
                ctx.Response.Redirect(ctx.RedirectUri);
            return Task.CompletedTask;
        };
        o.Events.OnRedirectToAccessDenied = ctx =>
        {
            if (ctx.Request.Path.StartsWithSegments("/api"))
                ctx.Response.StatusCode = StatusCodes.Status403Forbidden;
            else
                ctx.Response.Redirect(ctx.RedirectUri);
            return Task.CompletedTask;
        };
    });
builder.Services.AddAuthorization(o => o.AddPolicy(AuthSchemes.OwnerPolicy, p => p
    .AddAuthenticationSchemes(AuthSchemes.DeviceToken, AuthSchemes.Cookies)
    .RequireAuthenticatedUser()));

builder.Services.AddRateLimiter(o =>
{
    o.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
    o.AddPolicy("login", ctx => RateLimitPartition.GetFixedWindowLimiter(
        ctx.Connection.RemoteIpAddress?.ToString() ?? "unknown",
        _ => new FixedWindowRateLimiterOptions
        {
            PermitLimit = 5,
            Window = TimeSpan.FromMinutes(1),
            QueueLimit = 0,
        }));
});

// The container is only reachable through the reverse proxy (Dokploy's Traefik), so its forwarded headers are trusted.
builder.Services.Configure<ForwardedHeadersOptions>(o =>
{
    o.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto;
    o.KnownIPNetworks.Clear();
    o.KnownProxies.Clear();
    o.ForwardLimit = 1;
});

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

// Hash the password now rather than on the first login attempt.
app.Services.GetRequiredService<PasswordVerifier>();

app.UseForwardedHeaders();
app.UseWebSockets(new WebSocketOptions { KeepAliveInterval = TimeSpan.FromSeconds(30) });
app.UseAuthentication();
app.UseAuthorization();
app.UseRateLimiter();

app.MapHealthChecks("/health");
app.MapAuthEndpoints();
app.MapDeviceEndpoints();
app.MapClipEndpoints();
app.MapWebSocketEndpoint();

app.Run();

public partial class Program;
