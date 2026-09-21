using System.Net;
using System.Security.Cryptography;
using System.Text;

namespace TideCasa.Hosting;

/// <summary>Enabled only for components whose sole public network entry is App Platform ingress.</summary>
public static class AppPlatformIngress
{
    public const string TokenHeader = "X-Tide-Internal-Proxy";
    public const string ClientHeader = "X-Tide-Client-IP";

    public static IApplicationBuilder UseAppPlatformIngress(this IApplicationBuilder app, IConfiguration configuration, bool api)
    {
        if (configuration["ReverseProxy:Provider"] != "DigitalOceanAppPlatform") return app;
        var token = configuration["ReverseProxy:InternalToken"] ?? "";
        if (token.Length < 43 || token.Length > 200 || token.Any(char.IsWhiteSpace))
            throw new InvalidOperationException("App Platform requires a private shared internal proxy token.");
        var expected = SHA256.HashData(Encoding.UTF8.GetBytes(token));
        return app.Use(async (context, next) =>
        {
            if (context.Request.Path == "/health") { await next(); return; }
            var peer = context.Connection.RemoteIpAddress;
            if (peer is null || !PrivatePeer(peer)) { context.Response.StatusCode = 400; return; }
            IPAddress? client;
            if (api && context.Request.Headers.TryGetValue(TokenHeader, out var supplied) && supplied.Count == 1
                && supplied[0] is { Length: <= 200 } presented
                && CryptographicOperations.FixedTimeEquals(expected, SHA256.HashData(Encoding.UTF8.GetBytes(presented))))
            {
                // Only our Web component knows this token. Caller-supplied forwarding
                // headers are never enough to impersonate the internal forwarding hop.
                client = ReadAddress(context.Request.Headers[ClientHeader]);
                if (client is null) { context.Response.StatusCode = 400; return; }
            }
            else
            {
                // DigitalOcean adds do-connecting-ip at its ingress boundary. This mode
                // must not be used on a host that exposes the component port directly.
                client = ReadAddress(context.Request.Headers["do-connecting-ip"]);
                var protocol = context.Request.Headers["X-Forwarded-Proto"];
                if (client is null || protocol.Count != 1 || protocol[0] != "https")
                { context.Response.StatusCode = 400; return; }
            }
            context.Request.Headers.Remove(TokenHeader);
            context.Request.Headers.Remove(ClientHeader);
            context.Request.Headers.Remove("X-Forwarded-For");
            context.Request.Headers.Remove("X-Forwarded-Proto");
            context.Request.Headers.Remove("X-Forwarded-Host");
            context.Connection.RemoteIpAddress = client;
            context.Request.Scheme = "https";
            await next();
        });
    }

    private static IPAddress? ReadAddress(Microsoft.Extensions.Primitives.StringValues values) =>
        values.Count == 1 && IPAddress.TryParse(values[0], out var address) ? address : null;

    private static bool PrivatePeer(IPAddress address)
    {
        if (address.IsIPv4MappedToIPv6) address = address.MapToIPv4();
        if (IPAddress.IsLoopback(address)) return true;
        var bytes = address.GetAddressBytes();
        return bytes.Length == 4 ? bytes[0] == 10 || bytes[0] == 172 && bytes[1] is >= 16 and <= 31 || bytes[0] == 192 && bytes[1] == 168
            : (bytes[0] & 0xfe) == 0xfc;
    }
}
