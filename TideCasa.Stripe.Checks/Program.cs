using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using TideCasa.Api.Infrastructure.Payments;

if (args.FirstOrDefault() == "--sandbox-preflight")
{
    Environment.ExitCode = await SandboxPreflight.RunAsync(args[1..]);
    return;
}

var results = new List<object>();
void Check(string label, bool pass)
{
    results.Add(new { check = label, passed = pass }); Console.WriteLine((pass ? "PASS " : "FAIL ") + label);
    if (!pass) throw new InvalidOperationException(label);
}
async Task<StripeTransportException> Failure(Func<Task> action)
{ try { await action(); } catch (StripeTransportException e) { return e; } throw new InvalidOperationException("Expected provider failure."); }
const string key = "rk_test_synthetic_000000000";
const string secret = "whsec_synthetic_rotation_000000000";
HttpResponseMessage Response(int status = 200, string json = "{\"id\":\"fixture\"}") => new((HttpStatusCode)status) { Content = new StringContent(json, Encoding.UTF8, "application/json") };
var ct = CancellationToken.None;
try
{
    await SandboxPreflightChecks.RunAsync(Check);
    await NetworkBoundaryChecks.RunAsync(Check);
    var wire = new ConcurrentBag<Wire>();
    using (var client = new StripeHttpTransport(key, handler: new Handler(async (request, token) =>
    {
        var entry = new Wire(request.RequestUri!.PathAndQuery, request.Headers.Authorization!.ToString(),
            request.Headers.GetValues("Stripe-Version").Single(), request.Headers.TryGetValues("Stripe-Account", out var account) ? account.Single() : null,
            request.Headers.TryGetValues("Idempotency-Key", out var idempotency) ? idempotency.Single() : null,
            request.Content?.Headers.ContentType?.MediaType, request.Content is null ? "" : await request.Content.ReadAsStringAsync(token));
        wire.Add(entry); await Task.Yield(); return Response();
    })))
    {
        await Task.WhenAll(Enumerable.Range(0, 32).Select(i => client.PostFormAsync("/v1/checkout/sessions", new Dictionary<string, string>
            { ["metadata[tenant]"] = "one & two+/é", ["amount"] = "1234", ["enabled"] = "false" }, "acct_tenant" + i, "saved." + i, ct)));
        await client.GetAsync("/v1/account", null, ct);
        await client.PostJsonAsync("/v2/core/accounts", new { display_name = "Cafe & é", enabled = false, optional = (string?)null }, "account.saved", ct);
        Check("Basic authorization contains the server key and empty password on every request", wire.All(w => w.Authorization == "Basic " + Convert.ToBase64String(Encoding.ASCII.GetBytes(key + ":"))));
        Check("Reviewed API version on every request", wire.All(w => w.Version == "2026-08-26.dahlia"));
        Check("Concurrent tenants retain their own account and idempotency key", wire.Where(w => w.Account is not null).All(w => w.Account == "acct_tenant" + w.Key![6..]) && wire.Count(w => w.Account is not null) == 32);
        Check("Platform requests do not inherit tenant headers", wire.Where(w => w.Path != "/v1/checkout/sessions").All(w => w.Account is null));
        Check("v1 form encoding preserves nested keys and special characters", wire.Where(w => w.Account is not null).All(w => w.ContentType == "application/x-www-form-urlencoded" && w.Body == "metadata%5Btenant%5D=one+%26+two%2B%2F%C3%A9&amount=1234&enabled=false"));
        var jsonWire = wire.Single(w => w.Path.StartsWith("/v2/")); using var json = JsonDocument.Parse(jsonWire.Body);
        Check("v2 JSON preserves booleans and explicit null", jsonWire.ContentType == "application/json" && json.RootElement.GetProperty("enabled").ValueKind == JsonValueKind.False && json.RootElement.GetProperty("optional").ValueKind == JsonValueKind.Null && json.RootElement.GetProperty("display_name").GetString() == "Cafe & é");
        foreach (var path in new[] { "https://evil.invalid/v1/account", "//evil.invalid/v1/account", "/v1/../account", "/v1/%2e%2e/account", "/v1/accounts\\anything", "/v1/account#fragment" })
        {
            var before = wire.Count; var rejected = false;
            try { await client.GetAsync(path, null, ct); } catch (InvalidOperationException) { rejected = true; }
            Check("Reject untrusted request path " + path, rejected && wire.Count == before);
        }
    }
    foreach (var (status, kind) in new[] { (400, "validation"), (401, "authentication"), (402, "declined"), (403, "permission"), (404, "missing"), (409, "conflict"), (429, "rate_limited"), (500, "provider_unavailable"), (503, "provider_unavailable") })
    {
        var calls = 0;
        using var client = new StripeHttpTransport(key, handler: new Handler((r, t) =>
        {
            calls++; var response = Response(status, "{\"error\":{\"type\":\"card_error\",\"code\":\"card_declined\",\"decline_code\":\"insufficient_funds\",\"message\":\"PRIVATE_PROVIDER_BODY\"}}");
            response.Headers.Add("Request-Id", "req_synthetic123"); return Task.FromResult(response);
        }));
        var error = await Failure(() => client.PostFormAsync("/v1/checkout/sessions", [], "acct_tenantA", "saved.key", ct));
        Check("Structured private error " + status, error.Kind == kind && error.Status == status && error.ErrorType == "card_error" && error.Code == "card_declined" && error.DeclineCode == "insufficient_funds" && error.RequestId == "req_synthetic123");
        Check("No provider message or credentials in error " + status, !error.ToString().Contains("PRIVATE_PROVIDER_BODY") && !error.ToString().Contains(key));
        Check("Only throttling retries the merchant write " + status, calls == (status == 429 ? 2 : 1));
    }
    foreach (var malformed in new[] { "<html>PRIVATE_PROVIDER_BODY</html>", "[]", "{\"livemode\":true,\"livemode\":false}", "{\"nested\":{\"id\":1,\"id\":2}}", new string('[', 60) + new string(']', 60) })
    {
        using var client = new StripeHttpTransport(key, handler: new Handler((r, t) => Task.FromResult(Response(200, malformed))));
        Check("Malformed, ambiguous or deep JSON fails safely", (await Failure(() => client.GetAsync("/v1/account", null, ct))).Kind == "invalid_response");
    }
    foreach (var knownLength in new[] { true, false })
    {
        using var client = new StripeHttpTransport(key, handler: new Handler((r, t) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            { Content = knownLength ? new ByteArrayContent(new byte[StripeHttpTransport.MaximumResponseBytes + 1]) : new UnknownLengthContent(new byte[StripeHttpTransport.MaximumResponseBytes + 1]) })));
        Check("Bound response size " + (knownLength ? "declared" : "streamed"), (await Failure(() => client.GetAsync("/v1/account", null, ct))).Kind == "response_too_large");
    }
    var attempts = new List<(string? Key, string Body)>();
    using (var client = new StripeHttpTransport(key, handler: new Handler(async (request, token) =>
    {
        attempts.Add((request.Headers.GetValues("Idempotency-Key").Single(), await request.Content!.ReadAsStringAsync(token)));
        var response = attempts.Count == 1 ? Response(429, "{\"error\":{\"code\":\"rate_limit\"}}") : Response();
        response.Headers.RetryAfter = new(System.TimeSpan.Zero); return response;
    })))
    {
        await client.PostFormAsync("/v1/checkout/sessions", new Dictionary<string, string> { ["amount"] = "1234" }, null, "original.key", ct);
        Check("Rate-limit retry uses the exact saved key and body", attempts.Count == 2 && attempts[0] == attempts[1] && attempts[0].Key == "original.key");
    }
    foreach (var mode in new[] { "no-key", "long-retry-after", "stripe-no-retry", "idempotent-service", "get-retry" })
    {
        var count = 0;
        using var client = new StripeHttpTransport(key, handler: new Handler((r, t) =>
        {
            count++; var response = Response(mode is "idempotent-service" or "get-retry" ? 503 : 429, "{\"error\":{\"type\":\"api_error\"}}");
            if (mode == "long-retry-after") response.Headers.RetryAfter = new(TimeSpan.FromSeconds(60));
            if (mode == "stripe-no-retry") response.Headers.Add("Stripe-Should-Retry", "false");
            return Task.FromResult(response);
        }));
        await Failure(() => mode == "get-retry" ? client.GetAsync("/v1/account", null, ct)
            : client.PostFormAsync("/v1/checkout/sessions", [], null, mode == "no-key" ? null : "saved.key", ct, mode == "idempotent-service"));
        Check("Bounded retry policy " + mode, count == (mode is "idempotent-service" or "get-retry" ? 2 : 1));
    }
    using (var timeoutClient = new StripeHttpTransport(key, handler: new Handler(async (r, token) => { await Task.Delay(5000, token); return Response(); }), requestTimeout: TimeSpan.FromMilliseconds(25)))
        Check("Provider timeout is an uncertain result", (await Failure(() => timeoutClient.GetAsync("/v1/account", null, ct))).Kind == "timeout");
    using (var cancelClient = new StripeHttpTransport(key, handler: new Handler(async (r, token) => { await Task.Delay(5000, token); return Response(); })))
    {
        using var cancel = new CancellationTokenSource(25); var canceled = false;
        try { await cancelClient.GetAsync("/v1/account", null, cancel.Token); } catch (OperationCanceledException) { canceled = true; }
        Check("Caller cancellation stays distinct from provider timeout", canceled);
    }
    foreach (var origin in new[] { "http://api.stripe.com", "https://api.stripe.com.evil.invalid", "https://api.stripe.com:444", "https://name@api.stripe.com", "http://127.0.0.1:23456" })
    {
        var rejected = false; try { using var invalid = new StripeHttpTransport(key, origin); } catch (InvalidOperationException) { rejected = true; }
        Check("Reject unapproved origin " + origin, rejected);
    }
    var listener = new TcpListener(IPAddress.Loopback, 0); listener.Start();
    try
    {
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        var server = Task.Run(async () =>
        {
            using var peer = await listener.AcceptTcpClientAsync(); await using var stream = peer.GetStream();
            var buffer = new byte[8192]; var received = 0;
            while (!Encoding.ASCII.GetString(buffer, 0, received).Contains("\r\n\r\n", StringComparison.Ordinal))
            {
                if (received == buffer.Length) throw new IOException("Fixture request headers exceeded the bound.");
                var count = await stream.ReadAsync(buffer.AsMemory(received));
                if (count == 0) throw new IOException("Fixture request ended before its headers.");
                received += count;
            }
            await stream.WriteAsync(Encoding.ASCII.GetBytes("HTTP/1.1 302 Found\r\nLocation: http://127.0.0.1:" + port + "/followed\r\nContent-Type: application/json\r\nContent-Length: 2\r\nConnection: close\r\n\r\n{}"));
        });
        using var client = new StripeHttpTransport(key, "http://127.0.0.1:" + port, true);
        var error = await Failure(() => client.GetAsync("/v1/account", null, ct)); await server;
        Check("Real HTTP handler refuses provider redirect", error.Status == 302 && !listener.Pending());
    }
    finally { listener.Stop(); }

    var now = DateTimeOffset.FromUnixTimeSeconds(1800000000); var payload = Encoding.UTF8.GetBytes("{\"id\":\"evt_synthetic\",\"name\":\"Café\"}\n");
    string Signature(long timestamp, byte[] bytes, string signingSecret = secret) => Convert.ToHexStringLower(HMACSHA256.HashData(Encoding.UTF8.GetBytes(signingSecret), Encoding.ASCII.GetBytes(timestamp + ".").Concat(bytes).ToArray()));
    var valid = Signature(now.ToUnixTimeSeconds(), payload); var stamp = now.ToUnixTimeSeconds();
    bool Accepted(byte[] bytes, string header, string signingSecret = secret)
    { try { StripeWebhookSignature.Verify(bytes, header, signingSecret, now); return true; } catch (StripeSignatureException) { return false; } }
    Check("HMAC validates exact UTF8 payload bytes", Accepted(payload, $"t={stamp},v1={valid}"));
    Check("Signature rotation accepts any valid v1", Accepted(payload, $"t={stamp},v1={new string('0', 64)},v1={valid}"));
    Check("Unsupported signature versions do not replace v1", !Accepted(payload, $"t={stamp},v0={valid}"));
    Check("Changed whitespace invalidates signature", !Accepted(payload[..^1], $"t={stamp},v1={valid}"));
    Check("Endpoint secrets remain isolated", !Accepted(payload, $"t={stamp},v1={valid}", "whsec_other_endpoint_secret"));
    foreach (var offset in new[] { -301, -300, 300, 301 })
        Check("Timestamp tolerance " + offset, Accepted(payload, $"t={stamp + offset},v1={Signature(stamp + offset, payload)}") == (Math.Abs(offset) <= 300));
    foreach (var header in new[] { "", $"t={stamp},t={stamp},v1={valid}", $"t={stamp},v1=xyz", $"t={stamp},v1={valid},v1=bad", "t=9999999999999999999,v1=" + valid, new string('x', 4097) })
        Check("Reject malformed signature header", !Accepted(payload, header));
    Check("Signed duplicate JSON fields are rejected before interpretation", DuplicateRejected());
    bool DuplicateRejected() { try { using var doc = JsonDocument.Parse("{\"livemode\":true,\"livemode\":false}"); StripeJson.RejectDuplicateProperties(doc.RootElement); return false; } catch (JsonException) { return true; } }
}
finally
{
    if (args.Length == 1) File.WriteAllText(args[0], JsonSerializer.Serialize(results, new JsonSerializerOptions { WriteIndented = true }));
}
Console.WriteLine($"Completed {results.Count} transport and signature checks.");

record Wire(string Path, string Authorization, string Version, string? Account, string? Key, string? ContentType, string Body);
sealed class Handler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send) : HttpMessageHandler
{ protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) => send(request, ct); }
sealed class UnknownLengthContent(byte[] bytes) : HttpContent
{
    protected override bool TryComputeLength(out long length) { length = 0; return false; }
    protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context) => stream.WriteAsync(bytes).AsTask();
    protected override Task<Stream> CreateContentReadStreamAsync() => Task.FromResult<Stream>(new MemoryStream(bytes));
}
