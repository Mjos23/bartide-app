using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text.Json;
using Lib.Net.Http.WebPush;
using Lib.Net.Http.WebPush.Authentication;
using System.Data.Common;

namespace TideCasa.Api.Features.BusinessPosts;

internal sealed record PushDelivery(string PostId, string SubscriptionId, int Generation, string LeaseKey, int Attempts,
    string Endpoint, string PublicKey, string Auth, string Slug, string Title, string Body);
internal sealed record PushDeliveryResult(int Status, int? RetryAfterSeconds = null);

public sealed class BusinessPushSender : IDisposable
{
    private readonly BusinessPushOptions options;
    private readonly HttpClient http;
    private readonly PushServiceClient client;
    private readonly VapidAuthentication? authentication;

    public BusinessPushSender(BusinessPushOptions options)
    {
        this.options = options;
        var handler = new SocketsHttpHandler { AllowAutoRedirect = false, UseCookies = false, UseProxy = false,
            ConnectTimeout = TimeSpan.FromSeconds(8), MaxResponseHeadersLength = 16, PooledConnectionLifetime = TimeSpan.FromMinutes(5) };
        handler.ConnectCallback = async (context, ct) =>
        {
            var allowLoopback = options.DevelopmentOrigin is not null && context.DnsEndPoint.Host == options.DevelopmentOrigin.Host && context.DnsEndPoint.Port == options.DevelopmentOrigin.Port;
            var addresses = await Dns.GetHostAddressesAsync(context.DnsEndPoint.Host, ct);
            foreach (var address in addresses)
            {
                if (!(allowLoopback && IPAddress.IsLoopback(address)) && !PublicAddress(address)) continue;
                var socket = new Socket(address.AddressFamily, SocketType.Stream, ProtocolType.Tcp);
                try { await socket.ConnectAsync(new IPEndPoint(address, context.DnsEndPoint.Port), ct); return new NetworkStream(socket, ownsSocket: true); }
                catch { socket.Dispose(); }
            }
            throw new HttpRequestException("Push service is unreachable.");
        };
        http = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(20), MaxResponseContentBufferSize = 16 * 1024 };
        client = new PushServiceClient(http) { AutoRetryAfter = false, DefaultTimeToLive = 3600 };
        if (options.Ready) authentication = new VapidAuthentication(options.PublicKey, options.PrivateKey) { Subject = options.Subject };
    }

    internal async Task<PushDeliveryResult> SendAsync(PushDelivery delivery, CancellationToken ct)
    {
        if (!options.Ready || authentication is null || !options.ValidEndpoint(delivery.Endpoint)) return new(400);
        try
        {
            BusinessPushOptions.ValidateSubscriptionKeys(delivery.PublicKey, delivery.Auth);
            var subscription = new PushSubscription { Endpoint = delivery.Endpoint, Keys = new Dictionary<string, string> { ["p256dh"] = delivery.PublicKey, ["auth"] = delivery.Auth } };
            // Keep the encrypted push record below browser service payload limits; full text lives in the feed.
            var body = string.Concat(delivery.Body.EnumerateRunes().Take(180).Select(rune => rune.ToString()));
            var payload = JsonSerializer.Serialize(new { title = delivery.Title, body, url = "/updates/" + Uri.EscapeDataString(delivery.Slug) + "#post-" + delivery.PostId, tag = "post-" + delivery.PostId, icon = "/app-icons/icon-192.png" });
            var message = new PushMessage(payload) { Topic = BusinessPostsStore.Hash(delivery.PostId)[..32], TimeToLive = 3600, Urgency = PushMessageUrgency.Normal };
            await client.RequestPushMessageDeliveryAsync(subscription, message, authentication, VapidAuthenticationScheme.Vapid, ct);
            return new(201);
        }
        catch (PushServiceClientException error)
        {
            var retry = error.Headers?.RetryAfter;
            var seconds = retry?.Delta?.TotalSeconds ?? (retry?.Date is { } date ? (date - DateTimeOffset.UtcNow).TotalSeconds : (double?)null);
            return new((int)error.StatusCode, seconds is null ? null : (int)Math.Clamp(seconds.Value, 30, 86400));
        }
        catch (Exception error) when (error is HttpRequestException or TaskCanceledException) { return new(503); }
        catch (Exception error) when (error is BusinessPostFailure or ArgumentException or FormatException or CryptographicException) { return new(400); }
    }

    private static bool PublicAddress(IPAddress address)
    {
        if (address.IsIPv4MappedToIPv6) address = address.MapToIPv4();
        if (IPAddress.IsLoopback(address) || address.Equals(IPAddress.Any) || address.Equals(IPAddress.IPv6Any)) return false;
        var bytes = address.GetAddressBytes();
        if (address.AddressFamily == AddressFamily.InterNetwork)
            return bytes[0] is not (0 or 10 or 127) && bytes[0] < 224 && !(bytes[0] == 169 && bytes[1] == 254) &&
                !(bytes[0] == 172 && bytes[1] is >= 16 and <= 31) && !(bytes[0] == 192 && bytes[1] == 168) && !(bytes[0] == 100 && bytes[1] is >= 64 and <= 127);
        return !address.IsIPv6LinkLocal && !address.IsIPv6SiteLocal && !address.IsIPv6Multicast && (bytes[0] & 0xfe) != 0xfc;
    }
    public void Dispose() { authentication?.Dispose(); http.Dispose(); }
}

public sealed class BusinessPushDispatcher(BusinessPostsStore store, BusinessPushOptions options, BusinessPushSender sender, ILogger<BusinessPushDispatcher> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await store.CancelIneligibleDeliveries(stoppingToken);
                if (options.Ready)
                    for (var count = 0; count < 20 && !stoppingToken.IsCancellationRequested; count++)
                    {
                        var delivery = await store.ClaimDelivery(stoppingToken);
                        if (delivery is null) break;
                        if (!await store.DeliveryStillEligible(delivery, stoppingToken)) continue;
                        var result = await sender.SendAsync(delivery, stoppingToken);
                        await store.CompleteDelivery(delivery, result, stoppingToken);
                    }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            catch (Exception error) when (error is DbException or JsonException or InvalidOperationException or HttpRequestException)
            { logger.LogWarning("Business notification delivery will retry after a temporary failure."); }
            try { await Task.Delay(options.DispatchMilliseconds, stoppingToken); }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
        }
    }
}
