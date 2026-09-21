using System.Net;
using TideCasa.Hosting;

namespace TideCasa.Blazor.Services;

public sealed class AppPlatformApiHandler(IConfiguration configuration, IHttpContextAccessor context) : DelegatingHandler
{
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        if (configuration["ReverseProxy:Provider"] == "DigitalOceanAppPlatform")
        {
            var target = request.RequestUri;
            var expected = new Uri(configuration["Api:BaseUrl"] ?? "http://api:8080/");
            if (target is null || target.Scheme != expected.Scheme || target.Host != expected.Host || target.Port != expected.Port)
                throw new HttpRequestException("Unexpected internal API destination.");
            var address = context.HttpContext?.Connection.RemoteIpAddress;
            if (request.Headers.TryGetValues("X-Forwarded-For", out var existing))
            {
                var values = existing.Take(2).ToArray();
                if (values.Length == 1 && IPAddress.TryParse(values[0], out var parsed)) address = parsed;
            }
            // Background/circuit requests without HTTP context share a conservative bucket.
            address ??= IPAddress.Loopback;
            request.Headers.Remove("X-Forwarded-For");
            request.Headers.Remove(AppPlatformIngress.TokenHeader);
            request.Headers.Remove(AppPlatformIngress.ClientHeader);
            request.Headers.Add(AppPlatformIngress.TokenHeader, configuration["ReverseProxy:InternalToken"]!);
            request.Headers.Add(AppPlatformIngress.ClientHeader, address.ToString());
        }
        return base.SendAsync(request, cancellationToken);
    }
}
