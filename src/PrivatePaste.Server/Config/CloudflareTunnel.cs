using System.Net;

namespace PrivatePaste.Server.Config;

/// <summary>
/// Restores the visitor's IP and scheme when requests arrive through a Cloudflare tunnel. The tunnel connects to the
/// reverse proxy over plain HTTP from its own address, so without this every visitor looks like the tunnel (one shared
/// login rate-limit bucket) and every request looks like HTTP (session cookies not marked Secure).
/// </summary>
public static class CloudflareTunnel
{
    public static IApplicationBuilder UseCloudflareTunnel(this IApplicationBuilder app, ServerOptions options)
    {
        if (!options.TrustCloudflare) return app;
        return app.Use((context, next) =>
        {
            if (IPAddress.TryParse(context.Request.Headers["CF-Connecting-IP"].ToString(), out var ip))
                context.Connection.RemoteIpAddress = ip;
            // Cf-Visitor is a small JSON object such as {"scheme":"https"}.
            if (context.Request.Headers["Cf-Visitor"].ToString().Contains("\"https\"", StringComparison.Ordinal))
                context.Request.Scheme = "https";
            return next(context);
        });
    }
}
