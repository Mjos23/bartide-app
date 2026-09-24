using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace TideCasa.Api.Infrastructure.Payments;

// Contains only bounded identifiers, never Stripe's message, response body or request credentials.
internal sealed class StripeTransportException(string kind, int? status = null, string? type = null,
    string? code = null, string? declineCode = null, string? requestId = null) : Exception("Payment provider request could not be confirmed.")
{
    internal string Kind { get; } = kind;
    internal int? Status { get; } = status;
    internal string? ErrorType { get; } = type;
    internal string? Code { get; } = code;
    internal string? DeclineCode { get; } = declineCode;
    internal string? RequestId { get; } = requestId;
}

/// <summary>One managed client per configured credential; all context is assigned to individual requests.</summary>
internal sealed class StripeHttpTransport : IDisposable
{
    internal const string ApiVersion = "2026-08-26.dahlia";
    internal const string ApiOrigin = "https://api.stripe.com";
    internal const int MaximumResponseBytes = 1024 * 1024;
    private readonly HttpClient http;
    private readonly Uri origin;
    private readonly AuthenticationHeaderValue authorization;
    private readonly TimeSpan timeout;

    internal StripeHttpTransport(string key, string apiBase = ApiOrigin, bool allowLoopback = false,
        HttpMessageHandler? handler = null, TimeSpan? requestTimeout = null)
    {
        origin = new Uri(apiBase, UriKind.Absolute);
        if (origin.UserInfo.Length != 0 || origin.AbsolutePath != "/" || origin.Query.Length != 0 || origin.Fragment.Length != 0
            || !(origin.Scheme == "https" && origin.IdnHost == "api.stripe.com" && origin.IsDefaultPort
                || allowLoopback && origin.Scheme == "http" && origin.Host == "127.0.0.1"))
            throw new InvalidOperationException("Unexpected payment provider destination.");
        if (!Regex.IsMatch(key, "^rk_(test|live)_[A-Za-z0-9_]+$", RegexOptions.CultureInvariant))
            throw new InvalidOperationException("Invalid payment provider configuration.");
        authorization = new("Basic", Convert.ToBase64String(Encoding.ASCII.GetBytes(key + ":")));
        timeout = requestTimeout ?? TimeSpan.FromSeconds(20);
        http = new HttpClient(handler ?? new SocketsHttpHandler
        {
            AllowAutoRedirect = false, UseCookies = false, ConnectTimeout = TimeSpan.FromSeconds(5),
            PooledConnectionLifetime = TimeSpan.FromMinutes(5), MaxResponseHeadersLength = 32
        }) { Timeout = Timeout.InfiniteTimeSpan };
    }

    internal Task<JsonElement> GetAsync(string path, string? account, CancellationToken ct) => SendAsync(HttpMethod.Get, path, null, null, account, null, false, ct);
    internal async Task<JsonElement> PostFormAsync(string path, IEnumerable<KeyValuePair<string, string>> fields,
        string? account, string? key, CancellationToken ct, bool retryUncertainWrite = false)
    {
        using var content = new FormUrlEncodedContent(fields);
        return await SendAsync(HttpMethod.Post, path, await content.ReadAsByteArrayAsync(ct), "application/x-www-form-urlencoded", account, key, retryUncertainWrite, ct);
    }
    internal Task<JsonElement> PostJsonAsync(string path, object body, string? key, CancellationToken ct) =>
        SendAsync(HttpMethod.Post, path, JsonSerializer.SerializeToUtf8Bytes(body), "application/json", null, key, false, ct);

    private async Task<JsonElement> SendAsync(HttpMethod method, string path, byte[]? body, string? contentType,
        string? account, string? key, bool retryUncertainWrite, CancellationToken ct)
    {
        // Paths are authored by providers. No redirects, pagination URLs or webhook URLs can select a host.
        if (!(path.StartsWith("/v1/", StringComparison.Ordinal) || path.StartsWith("/v2/", StringComparison.Ordinal))
            || path.Contains("..", StringComparison.Ordinal) || path.Contains('\\') || path.Contains('#') || path.Any(char.IsControl)
            || path.Contains("%2f", StringComparison.OrdinalIgnoreCase) || path.Contains("%5c", StringComparison.OrdinalIgnoreCase)
            || path.Contains("%2e", StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException("Invalid payment provider path.");
        var target = new Uri(origin, path);
        if (target.Scheme != origin.Scheme || target.Host != origin.Host || target.Port != origin.Port)
            throw new InvalidOperationException("Unexpected payment provider destination.");
        if (account is not null && !Regex.IsMatch(account, "^acct_[A-Za-z0-9]{6,80}$")) throw new InvalidOperationException("Invalid payment account context.");
        if (key is not null && (key.Length is < 1 or > 255 || key.Any(c => c < 33 || c > 126))) throw new InvalidOperationException("Invalid payment request key.");
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(timeout);
        try
        {
            for (var attempt = 0; ; attempt++)
            {
                using var request = new HttpRequestMessage(method, target);
                request.Headers.Authorization = authorization;
                request.Headers.Add("Stripe-Version", ApiVersion);
                request.Headers.UserAgent.ParseAdd("TideCasa-StripeHttp/1.0");
                if (account is not null) request.Headers.Add("Stripe-Account", account);
                if (key is not null) request.Headers.Add("Idempotency-Key", key);
                if (body is not null)
                {
                    request.Content = new ByteArrayContent(body);
                    request.Content.Headers.ContentType = new MediaTypeHeaderValue(contentType!);
                }
                HttpResponseMessage response;
                try { response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, deadline.Token); }
                catch (HttpRequestException) when (attempt == 0 && (method == HttpMethod.Get || key is not null && retryUncertainWrite))
                { await Task.Delay(TimeSpan.FromMilliseconds(250), deadline.Token); continue; }
                using (response)
                {
                    var requestId = response.Headers.TryGetValues("Request-Id", out var ids) ? SafeCode(ids.FirstOrDefault()) : null;
                    var status = (int)response.StatusCode;
                    var bytes = await ReadBoundedAsync(response.Content, deadline.Token);
                    JsonElement json;
                    try
                    {
                        using var document = JsonDocument.Parse(bytes, new JsonDocumentOptions { MaxDepth = 48 });
                        if (document.RootElement.ValueKind != JsonValueKind.Object) throw new JsonException();
                        StripeJson.RejectDuplicateProperties(document.RootElement);
                        json = document.RootElement.Clone();
                    }
                    catch (JsonException) { throw new StripeTransportException("invalid_response", status, requestId: requestId); }
                    if (response.IsSuccessStatusCode) return json;
                    var error = StripeJson.P(json, "error");
                    var code = SafeCode(StripeJson.S(error, "code"));
                    var mayRetry = method == HttpMethod.Get || key is not null;
                    var retryable = status == 429 || status == 409 && code == "idempotency_key_in_use"
                        || status >= 500 && (method == HttpMethod.Get || retryUncertainWrite);
                    var retryAfter = response.Headers.RetryAfter?.Delta
                        ?? (response.Headers.RetryAfter?.Date is { } date ? date - DateTimeOffset.UtcNow : TimeSpan.FromMilliseconds(250));
                    var explicitlyNoRetry = response.Headers.TryGetValues("Stripe-Should-Retry", out var retry) && retry.Contains("false");
                    // Long Retry-After delays are left to durable recovery; never retry earlier than Stripe asks.
                    if (attempt == 0 && mayRetry && retryable && !explicitlyNoRetry && retryAfter <= TimeSpan.FromSeconds(2))
                    { await Task.Delay(retryAfter > TimeSpan.Zero ? retryAfter : TimeSpan.Zero, deadline.Token); continue; }
                    throw new StripeTransportException(status switch
                    {
                        400 => "validation", 401 => "authentication", 402 => "declined", 403 => "permission", 404 => "missing",
                        409 => "conflict", 429 => "rate_limited", >= 500 => "provider_unavailable", _ => "unexpected_status"
                    }, status, SafeCode(StripeJson.S(error, "type")), code, SafeCode(StripeJson.S(error, "decline_code")), requestId);
                }
            }
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested) { throw new StripeTransportException("timeout"); }
        catch (HttpRequestException) { throw new StripeTransportException("network"); }
        catch (IOException) { throw new StripeTransportException("network"); }
    }

    private static string? SafeCode(string? value) => value is { Length: <= 128 } && Regex.IsMatch(value, "^[A-Za-z0-9_]+$") ? value : null;
    private static async Task<byte[]> ReadBoundedAsync(HttpContent content, CancellationToken ct)
    {
        if (content.Headers.ContentLength > MaximumResponseBytes) throw new StripeTransportException("response_too_large");
        await using var stream = await content.ReadAsStreamAsync(ct);
        using var output = new MemoryStream(); var buffer = new byte[16384];
        for (;;)
        {
            var count = await stream.ReadAsync(buffer, ct);
            if (count == 0) return output.ToArray();
            if (output.Length + count > MaximumResponseBytes) throw new StripeTransportException("response_too_large");
            output.Write(buffer, 0, count);
        }
    }
    public void Dispose() => http.Dispose();
}
