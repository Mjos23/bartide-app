using System.Net;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using TideCasa.Blazor.Services;
using TideCasa.Hosting;

internal static class AppPlatformIngressChecks
{
    // Synthetic only. No deployment token is read or retained by these checks.
    private const string Token = "synthetic-test-token-not-a-production-secret-0001";

    public static async Task RunAsync()
    {
        var passed = 0;
        void Check(string label, bool condition)
        {
            if (!condition) throw new InvalidOperationException(label);
            passed++;
            Console.WriteLine("PASS " + label);
        }
        IConfiguration Config(bool enabled = true, string token = Token) => new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ReverseProxy:Provider"] = enabled ? "DigitalOceanAppPlatform" : null,
                ["ReverseProxy:InternalToken"] = token,
                ["Api:BaseUrl"] = "http://api:8080/"
            }).Build();
        using var services = new ServiceCollection().BuildServiceProvider();
        RequestDelegate Pipeline(bool api, bool enabled = true, string token = Token)
        {
            var app = new ApplicationBuilder(services);
            app.UseAppPlatformIngress(Config(enabled, token), api);
            app.Run(context => { context.Items["arrived"] = true; return Task.CompletedTask; });
            return app.Build();
        }
        static DefaultHttpContext Request(string? peer = "10.1.2.3", string path = "/signin")
        {
            var context = new DefaultHttpContext();
            context.Request.Path = path;
            context.Request.Scheme = "http";
            context.Connection.RemoteIpAddress = peer is null ? null : IPAddress.Parse(peer);
            return context;
        }
        static void Edge(DefaultHttpContext context, string client = "203.0.113.7")
        {
            context.Request.Headers["do-connecting-ip"] = client;
            context.Request.Headers["X-Forwarded-Proto"] = "https";
        }
        static bool Arrived(DefaultHttpContext context) => context.Items.ContainsKey("arrived");
        var web = Pipeline(false);
        var api = Pipeline(true);
        foreach (var peer in new[] { "127.0.0.1", "::1", "10.1.2.3", "172.16.0.2", "172.31.255.254", "192.168.1.2", "fd00::2", "::ffff:10.1.2.3", "100.64.0.0", "100.127.255.255", "::ffff:100.127.9.223" })
        {
            var request = Request(peer); Edge(request);
            request.Request.Headers["X-Forwarded-For"] = "198.51.100.99";
            request.Request.Headers["X-Forwarded-Host"] = "attacker.example";
            request.Request.Headers[AppPlatformIngress.TokenHeader] = "untrusted-public-value";
            request.Request.Headers[AppPlatformIngress.ClientHeader] = "198.51.100.100";
            await web(request);
            Check("Ingress normalizes HTTPS and client from " + peer, Arrived(request)
                && request.Request.Scheme == "https" && request.Connection.RemoteIpAddress!.ToString() == "203.0.113.7"
                && !request.Request.Headers.ContainsKey("X-Forwarded-For")
                && !request.Request.Headers.ContainsKey("X-Forwarded-Host")
                && !request.Request.Headers.ContainsKey("X-Forwarded-Proto")
                && !request.Request.Headers.ContainsKey(AppPlatformIngress.TokenHeader)
                && !request.Request.Headers.ContainsKey(AppPlatformIngress.ClientHeader));
        }
        foreach (var peer in new string?[] { null, "198.51.100.2", "172.15.0.2", "172.32.0.2", "192.169.0.2", "2001:db8::2", "100.63.255.255", "100.128.0.0", "::ffff:100.128.0.0" })
        {
            var request = Request(peer); Edge(request);
            request.Request.Headers[AppPlatformIngress.TokenHeader] = Token;
            request.Request.Headers[AppPlatformIngress.ClientHeader] = "203.0.113.7";
            await api(request);
            Check("Direct non-private or unknown peer cannot claim ingress " + (peer ?? "null"), !Arrived(request) && request.Response.StatusCode == 400);
        }
        foreach (var scenario in new[] { "missing-client", "missing-https", "http", "multi-client", "multi-protocol", "bad-client" })
        {
            var request = Request(); Edge(request);
            switch (scenario)
            {
                case "missing-client": request.Request.Headers.Remove("do-connecting-ip"); break;
                case "missing-https": request.Request.Headers.Remove("X-Forwarded-Proto"); break;
                case "http": request.Request.Headers["X-Forwarded-Proto"] = "http"; break;
                case "multi-client": request.Request.Headers["do-connecting-ip"] = new[] { "203.0.113.7", "198.51.100.1" }; break;
                case "multi-protocol": request.Request.Headers["X-Forwarded-Proto"] = new[] { "https", "http" }; break;
                case "bad-client": request.Request.Headers["do-connecting-ip"] = "203.0.113.7, 198.51.100.1"; break;
            }
            await web(request);
            Check("Ambiguous external ingress rejected: " + scenario, !Arrived(request) && request.Response.StatusCode == 400);
        }
        var internalRequest = Request();
        internalRequest.Request.Headers[AppPlatformIngress.TokenHeader] = Token;
        internalRequest.Request.Headers[AppPlatformIngress.ClientHeader] = "2001:db8::42";
        await api(internalRequest);
        Check("Authenticated Web-to-API forwarding carries IPv6 over the private transport", Arrived(internalRequest)
            && internalRequest.Request.IsHttps && internalRequest.Connection.RemoteIpAddress!.ToString() == "2001:db8::42"
            && !internalRequest.Request.Headers.ContainsKey(AppPlatformIngress.TokenHeader));
        foreach (var badToken in new[] { "", "wrong-token", Token + "changed" })
        {
            var request = Request();
            request.Request.Headers[AppPlatformIngress.TokenHeader] = badToken;
            request.Request.Headers[AppPlatformIngress.ClientHeader] = "203.0.113.7";
            await api(request);
            Check("Unauthenticated internal hop rejected (case " + passed + ")", !Arrived(request) && request.Response.StatusCode == 400);
        }
        var missingInternalClient = Request();
        missingInternalClient.Request.Headers[AppPlatformIngress.TokenHeader] = Token;
        await api(missingInternalClient);
        Check("Internal credentials require a valid client address", !Arrived(missingInternalClient) && missingInternalClient.Response.StatusCode == 400);
        var webInternal = Request();
        webInternal.Request.Headers[AppPlatformIngress.TokenHeader] = Token;
        webInternal.Request.Headers[AppPlatformIngress.ClientHeader] = "203.0.113.7";
        await web(webInternal);
        Check("Web does not accept the internal API credential as public ingress", !Arrived(webInternal));
        var hook = Request(path: "/api/v1/webhooks/stripe/service"); Edge(hook);
        await api(hook);
        Check("Public webhook ingress works without the internal credential", Arrived(hook) && hook.Request.IsHttps);
        var health = Request(null, "/health"); await api(health);
        Check("Platform health probes do not need browser ingress headers", Arrived(health));
        var local = Request("198.51.100.2"); await Pipeline(false, false)(local);
        Check("Existing local hosting is unaffected when this provider is disabled", Arrived(local) && !local.Request.IsHttps);
        foreach (var invalid in new[] { "", "short", new string('x', 201), Token + " " })
        {
            var rejected = false;
            try { Pipeline(false, token: invalid); } catch (InvalidOperationException) { rejected = true; }
            Check("Incomplete shared credential fails at startup (case " + passed + ")", rejected);
        }

        var accessor = new HttpContextAccessor { HttpContext = Request("203.0.113.30") };
        var terminal = new RecordingHandler();
        using var client = new HttpClient(new AppPlatformApiHandler(Config(), accessor) { InnerHandler = terminal });
        using var outbound = new HttpRequestMessage(HttpMethod.Get, "http://api:8080/api/v1/me");
        outbound.Headers.Add("X-Forwarded-For", "2001:db8::55");
        outbound.Headers.Add(AppPlatformIngress.TokenHeader, "untrusted-value");
        await client.SendAsync(outbound);
        Check("API client attaches shared credential and captured circuit address", terminal.Calls == 1
            && terminal.Token == Token && terminal.Address == "2001:db8::55" && !terminal.ForwardedFor);
        accessor.HttpContext = null;
        await client.GetAsync("http://api:8080/api/v1/background");
        Check("Requests without a client context share a conservative rate-limit bucket", terminal.Address == "127.0.0.1");
        foreach (var target in new[] { "https://attacker.example/", "http://api:8081/", "https://api:8080/", "http://api.attacker.example:8080/" })
        {
            var before = terminal.Calls;
            var rejected = false;
            try { await client.GetAsync(target); } catch (HttpRequestException) { rejected = true; }
            Check("Internal credentials never leave the configured API origin: " + target, rejected && terminal.Calls == before);
        }
        var localTerminal = new RecordingHandler();
        using var localClient = new HttpClient(new AppPlatformApiHandler(Config(false), accessor) { InnerHandler = localTerminal });
        await localClient.GetAsync("http://127.0.0.1:51002/health");
        Check("Local client sends no platform credential", localTerminal.Calls == 1 && localTerminal.Token is null);
        Console.WriteLine(System.Text.Json.JsonSerializer.Serialize(new { passed, completed = true, boundary = "in-process; actual App Platform ingress still requires hosted checks" }));
    }

    private sealed class RecordingHandler : HttpMessageHandler
    {
        public int Calls { get; private set; }
        public string? Token { get; private set; }
        public string? Address { get; private set; }
        public bool ForwardedFor { get; private set; }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Calls++;
            Token = request.Headers.TryGetValues(AppPlatformIngress.TokenHeader, out var tokens) ? tokens.Single() : null;
            Address = request.Headers.TryGetValues(AppPlatformIngress.ClientHeader, out var addresses) ? addresses.Single() : null;
            ForwardedFor = request.Headers.Contains("X-Forwarded-For");
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK));
        }
    }
}
