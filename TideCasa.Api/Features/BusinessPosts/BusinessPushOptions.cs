using System.Net;
using System.Security.Cryptography;
using System.Text.RegularExpressions;

namespace TideCasa.Api.Features.BusinessPosts;

public sealed class BusinessPushOptions
{
    public bool Enabled { get; }
    public bool Ready { get; }
    public string PublicKey { get; } = "";
    public string PrivateKey { get; } = "";
    public string Subject { get; } = "";
    public Uri? DevelopmentOrigin { get; }
    public int DispatchMilliseconds { get; } = 5000;

    public BusinessPushOptions(IConfiguration config, IHostEnvironment environment)
    {
        Enabled = config.GetValue<bool>("WebPush:Enabled");
        PublicKey = config["WebPush:VapidPublicKey"] ?? "";
        PrivateKey = config["WebPush:VapidPrivateKey"] ?? "";
        Subject = config["WebPush:VapidSubject"] ?? "";
        var origin = config["WebPush:DevelopmentPushOrigin"] ?? "";
        if (origin.Length > 0)
        {
            if (!environment.IsDevelopment() || !config.GetValue<bool>("WebPush:AllowDevelopmentLoopback") ||
                !Uri.TryCreate(origin, UriKind.Absolute, out var uri) || uri.Scheme != "http" || uri.UserInfo.Length != 0 ||
                uri.Query.Length != 0 || uri.Fragment.Length != 0 || uri.AbsolutePath != "/" ||
                !IPAddress.TryParse(uri.Host, out var address) || !IPAddress.IsLoopback(address)) return;
            DevelopmentOrigin = uri;
        }
        if (environment.IsDevelopment()) DispatchMilliseconds = Math.Clamp(config.GetValue<int?>("WebPush:DevelopmentDispatchMilliseconds") ?? 5000, 250, 5000);
        if (!Enabled) return;
        try
        {
            var point = Decode(PublicKey, 65); var secret = Decode(PrivateKey, 32);
            using var key = ECDsa.Create(new ECParameters { Curve = ECCurve.NamedCurves.nistP256, D = secret });
            var actual = key.ExportParameters(false);
            if (point[0] != 4 || !point.AsSpan(1, 32).SequenceEqual(actual.Q.X) || !point.AsSpan(33, 32).SequenceEqual(actual.Q.Y)) return;
            if (Subject.Length > 254 || Subject.Any(c => char.IsControl(c) || c is '"' or '\\') ||
                !Uri.TryCreate(Subject, UriKind.Absolute, out var subject) || subject.Scheme is not ("mailto" or "https") ||
                (subject.Scheme == "mailto" && !Subject.Contains('@')) || (subject.Scheme == "https" && subject.Host.Length == 0)) return;
            Ready = true;
        }
        catch (Exception error) when (error is FormatException or ArgumentException or CryptographicException or PlatformNotSupportedException) { }
    }

    public bool ValidEndpoint(string? endpoint)
    {
        if (endpoint is null || endpoint.Length is < 16 or > 2048 || endpoint.Any(c => char.IsWhiteSpace(c) || char.IsControl(c) || c == '\\') ||
            !Uri.TryCreate(endpoint, UriKind.Absolute, out var uri) || uri.UserInfo.Length != 0 || uri.Fragment.Length != 0 || uri.AbsolutePath.Length < 2) return false;
        if (DevelopmentOrigin is not null && uri.GetLeftPart(UriPartial.Authority) == DevelopmentOrigin.GetLeftPart(UriPartial.Authority)) return true;
        if (uri.Scheme != "https" || uri.Port != 443 || uri.Host != uri.IdnHost || uri.Host.EndsWith('.')) return false;
        var host = uri.Host.ToLowerInvariant();
        return host is "fcm.googleapis.com" or "web.push.apple.com" or "updates.push.services.mozilla.com" ||
            host.EndsWith(".push.services.mozilla.com", StringComparison.Ordinal) || host.EndsWith(".notify.windows.com", StringComparison.Ordinal);
    }

    public static void ValidateSubscriptionKeys(string publicKey, string auth)
    {
        try
        {
            var point = Decode(publicKey, 65); _ = Decode(auth, 16);
            if (point[0] != 4) throw new FormatException();
            using var key = ECDiffieHellman.Create(new ECParameters { Curve = ECCurve.NamedCurves.nistP256, Q = new ECPoint { X = point[1..33], Y = point[33..65] } });
        }
        catch (Exception error) when (error is FormatException or ArgumentException or CryptographicException or PlatformNotSupportedException)
        { throw new BusinessPostFailure("This browser notification subscription is invalid."); }
    }

    private static byte[] Decode(string? value, int length)
    {
        if (value is null || value.Length > 100 || !Regex.IsMatch(value, "^[A-Za-z0-9_-]+$")) throw new FormatException();
        var bytes = Convert.FromBase64String(value.Replace('-', '+').Replace('_', '/') + new string('=', (4 - value.Length % 4) % 4));
        if (bytes.Length != length) throw new FormatException();
        return bytes;
    }
}
