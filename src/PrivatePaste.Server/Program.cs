using System.Text.Json.Serialization;
using System.Threading.RateLimiting;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.DataProtection.KeyManagement;
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

// Persist the keys behind the session cookie and antiforgery tokens so a redeploy doesn't log the web UI out.
// They are encrypted at rest with a key derived from PP_DB_KEY, like everything else under /data.
builder.Services.AddDataProtection()
    .SetApplicationName("PrivatePaste")
    .PersistKeysToFileSystem(new DirectoryInfo(Path.Combine(serverOptions.DataDir, "dp-keys")));
builder.Services.Configure<KeyManagementOptions>(o => o.XmlEncryptor = new DbKeyXmlEncryptor(serverOptions));

builder.Services.ConfigureHttpJsonOptions(o =>
    o.SerializerOptions.Converters.Add(new JsonStringEnumConverter(System.Text.Json.JsonNamingPolicy.CamelCase)));

builder.Services.AddAuthentication(AuthSchemes.DeviceToken)
    .AddScheme<AuthenticationSchemeOptions, DeviceTokenAuthHandler>(AuthSchemes.DeviceToken, null)
    .AddCookie(AuthSchemes.Cookies, o =>
    {
        o.LoginPath = "/login";
        o.Cookie.HttpOnly = true;
        o.Cookie.SecurePolicy = CookieSecurePolicy.SameAsRequest;
        // Strict SameSite is defense in depth; cookie-authenticated unsafe API calls (including POST /api/clips/image)
        // additionally require the antiforgery header (RequireAntiforgeryForCookieAuth).
        o.Cookie.SameSite = SameSiteMode.Strict;
        o.ExpireTimeSpan = TimeSpan.FromDays(30);
        o.SlidingExpiration = true;
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

builder.Services.AddAntiforgery(o => o.HeaderName = CookieAntiforgery.HeaderName);
builder.Services.AddRazorPages(o =>
{
    o.Conventions.AuthorizeFolder("/", AuthSchemes.OwnerPolicy);
    o.Conventions.AllowAnonymousToPage("/Login");
});

builder.Services.AddRateLimiter(o =>
{
    o.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
    // Only attempts (POST) count; the login page itself (GET) is shared with the API policy name but never limited.
    o.AddPolicy("login", ctx => HttpMethods.IsPost(ctx.Request.Method)
        ? RateLimitPartition.GetFixedWindowLimiter(
            LoginPartition.KeyFor(ctx.Connection.RemoteIpAddress),
            _ => new FixedWindowRateLimiterOptions
            {
                PermitLimit = 5,
                Window = TimeSpan.FromMinutes(1),
                QueueLimit = 0,
            })
        : RateLimitPartition.GetNoLimiter("not-an-attempt"));
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

app.Use((context, next) =>
{
    // No page of this app is meant to be framed (clickjacking). Set before antiforgery, which would add SAMEORIGIN.
    context.Response.Headers.XFrameOptions = "DENY";
    return next(context);
});
app.UseForwardedHeaders();
app.UseWebSockets(new WebSocketOptions { KeepAliveInterval = TimeSpan.FromSeconds(30) });
app.UseStaticFiles();
app.UseAuthentication();
app.UseAuthorization();
app.UseRateLimiter();

app.MapHealthChecks("/health");
app.MapAuthEndpoints();
app.MapDeviceEndpoints();
app.MapClipEndpoints();
app.MapWebSocketEndpoint();
app.MapRazorPages();

app.Run();

public partial class Program;
