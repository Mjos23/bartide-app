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
        var diagnostics = configuration["ReverseProxy:Diagnostics"] == "true"
            ? app.ApplicationServices.GetService<ILoggerFactory>()?.CreateLogger("TideCasa.Hosting.Ingress") : null;
        return app.Use(async (context, next) =>
        {
            void Reject(string reason)
            {
                // Opt-in deployment diagnostics: no credentials, cookies, bodies or client header values.
                diagnostics?.LogWarning("Ingress rejected: {Reason}; transport peer {Peer}; client header count {ClientCount}; protocol header count {ProtocolCount}; protocol is HTTPS {IsHttps}",
                    reason, context.Connection.RemoteIpAddress, context.Request.Headers["do-connecting-ip"].Count,
                    context.Request.Headers["X-Forwarded-Proto"].Count, context.Request.Headers["X-Forwarded-Proto"] == "https");
                context.Response.StatusCode = 400;
            }
            if (context.Request.Path == "/health") { await next(); return; }
            var peer = context.Connection.RemoteIpAddress;
            if (peer is null || !PlatformPeer(peer)) { Reject("transport_peer"); return; }
            IPAddress? client;
            var internalHop = false;
            if (api && context.Request.Headers.TryGetValue(TokenHeader, out var supplied) && supplied.Count == 1
                && supplied[0] is { Length: <= 200 } presented
                && CryptographicOperations.FixedTimeEquals(expected, SHA256.HashData(Encoding.UTF8.GetBytes(presented))))
            {
                // Only our Web component knows this token. Caller-supplied forwarding
                // headers are never enough to impersonate the internal forwarding hop.
                client = ReadAddress(context.Request.Headers[ClientHeader]);
                if (client is null) { Reject("internal_client"); return; }
                internalHop = true;
            }
            else
            {
                // DigitalOcean adds do-connecting-ip at its ingress boundary. This mode
                // must not be used on a host that exposes the component port directly.
                client = ReadAddress(context.Request.Headers["do-connecting-ip"]);
                var protocol = context.Request.Headers["X-Forwarded-Proto"];
                if (client is null || protocol.Count != 1 || protocol[0] != "https")
                { Reject(client is null ? "edge_client" : "edge_protocol"); return; }
            }
            context.Request.Headers.Remove(TokenHeader);
            context.Request.Headers.Remove(ClientHeader);
            context.Request.Headers.Remove("X-Forwarded-For");
            context.Request.Headers.Remove("X-Forwarded-Proto");
            context.Request.Headers.Remove("X-Forwarded-Host");
            context.Connection.RemoteIpAddress = client;
            context.Request.Scheme = "https";
            if (diagnostics is not null)
            {
                var normalized = client.IsIPv4MappedToIPv6 ? client.MapToIPv4() : client;
                diagnostics.LogInformation("Ingress accepted; transport peer {Peer}; client fingerprint {ClientFingerprint}; internal hop {InternalHop}",
                    peer, Convert.ToHexString(SHA256.HashData(normalized.GetAddressBytes()))[..16], internalHop);
            }
            await next();
        });
    }

    private static IPAddress? ReadAddress(Microsoft.Extensions.Primitives.StringValues values) =>
        values.Count == 1 && IPAddress.TryParse(values[0], out var address) ? address : null;

    private static bool PlatformPeer(IPAddress address)
    {
        if (address.IsIPv4MappedToIPv6) address = address.MapToIPv4();
        if (IPAddress.IsLoopback(address)) return true;
        var bytes = address.GetAddressBytes();
        // App Platform ingress was observed on RFC 6598 shared address space.
        // This allowance applies only in the provider-specific deployment mode.
        return bytes.Length == 4 ? bytes[0] == 10 || bytes[0] == 172 && bytes[1] is >= 16 and <= 31 || bytes[0] == 192 && bytes[1] == 168
            || bytes[0] == 100 && bytes[1] is >= 64 and <= 127
            : (bytes[0] & 0xfe) == 0xfc;
    }
}
