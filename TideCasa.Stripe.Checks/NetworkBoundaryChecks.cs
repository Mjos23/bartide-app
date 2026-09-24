using System.Net;
using System.Text;
using TideCasa.Api.Infrastructure.Payments;

internal static class NetworkBoundaryChecks
{
    internal static async Task RunAsync(Action<string, bool> check)
    {
        const string key = "rk_test_synthetic_network_000000";
        foreach (var mode in new[] { "get-recovery", "get-exhausted", "merchant-uncertain", "billing-recovery" })
        {
            var calls = new List<(string Path, string? Key, string Body)>();
            using var client = new StripeHttpTransport(key, handler: new Handler(async (request, token) =>
            {
                calls.Add((request.RequestUri!.PathAndQuery, request.Headers.TryGetValues("Idempotency-Key", out var keys) ? keys.Single() : null,
                    request.Content is null ? "" : await request.Content.ReadAsStringAsync(token)));
                if (calls.Count == 1 || mode == "get-exhausted") throw new HttpRequestException("PRIVATE_NETWORK_MARKER " + key);
                return new(HttpStatusCode.OK) { Content = new StringContent("{\"object\":\"fixture\"}") };
            }));
            StripeTransportException? failure = null;
            try
            {
                if (mode.StartsWith("get-")) await client.GetAsync("/v1/account", null, CancellationToken.None);
                else await client.PostFormAsync("/v1/checkout/sessions", new Dictionary<string, string> { ["amount"] = "1234" },
                    mode == "merchant-uncertain" ? "acct_syntheticmerchant" : null, "saved.original.key", CancellationToken.None, mode == "billing-recovery");
            }
            catch (StripeTransportException error) { failure = error; }
            var expectedFailure = mode is "get-exhausted" or "merchant-uncertain";
            check("Network failure retry policy " + mode, calls.Count == (mode == "merchant-uncertain" ? 1 : 2)
                && (expectedFailure ? failure?.Kind == "network" : failure is null));
            check("Network retries retain the original request " + mode, calls.All(c => c == calls[0])
                && (failure is null || !failure.ToString().Contains("PRIVATE_NETWORK_MARKER") && !failure.ToString().Contains(key)));
        }
        foreach (var mode in new[] { "disconnect", "deadline", "caller-cancel" })
        {
            var calls = 0;
            using var cancel = new CancellationTokenSource();
            using var client = new StripeHttpTransport(key, handler: new Handler((request, token) =>
            {
                calls++; return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                { Content = new StreamContent(new InterruptedStream(mode == "disconnect")) });
            }), requestTimeout: TimeSpan.FromMilliseconds(mode == "deadline" ? 40 : 5000));
            if (mode == "caller-cancel") cancel.CancelAfter(40);
            var kind = "none";
            try { await client.GetAsync("/v1/account", null, cancel.Token); }
            catch (StripeTransportException error) { kind = error.Kind; }
            catch (OperationCanceledException) { kind = "caller"; }
            check("Response-body interruption remains bounded " + mode, calls == 1 && kind == (mode == "disconnect" ? "network" : mode == "deadline" ? "timeout" : "caller"));
        }
    }

    private sealed class InterruptedStream(bool disconnect) : Stream
    {
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            if (disconnect) throw new IOException("PRIVATE_STREAM_MARKER");
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken); return 0;
        }
        public override void Flush() => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
