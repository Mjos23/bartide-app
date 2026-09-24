using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace TideCasa.Api.Infrastructure.Payments;

internal sealed class StripeSignatureException() : Exception("Invalid payment notification signature.");

internal static class StripeWebhookSignature
{
    internal static void Verify(ReadOnlySpan<byte> payload, string header, string secret, DateTimeOffset? now = null)
    {
        if (header.Length is < 1 or > 4096 || payload.Length > 256 * 1024 || !secret.StartsWith("whsec_", StringComparison.Ordinal)) throw new StripeSignatureException();
        string? timestamp = null; var signatures = new List<byte[]>();
        foreach (var part in header.Split(','))
        {
            var field = part.Trim().Split('=', 2);
            if (field.Length != 2 || field[0].Length == 0 || field[1].Length == 0) throw new StripeSignatureException();
            if (field[0] == "t")
            {
                if (timestamp is not null || field[1].Length > 12 || !field[1].All(char.IsAsciiDigit)) throw new StripeSignatureException();
                timestamp = field[1];
            }
            else if (field[0] == "v1")
            {
                if (signatures.Count >= 8 || field[1].Length != 64 || !field[1].All(char.IsAsciiHexDigit)) throw new StripeSignatureException();
                signatures.Add(Convert.FromHexString(field[1]));
            }
        }
        if (!long.TryParse(timestamp, NumberStyles.None, CultureInfo.InvariantCulture, out var seconds)
            || signatures.Count == 0 || Math.Abs((now ?? DateTimeOffset.UtcNow).ToUnixTimeSeconds() - seconds) > 300) throw new StripeSignatureException();
        using var hmac = IncrementalHash.CreateHMAC(HashAlgorithmName.SHA256, Encoding.UTF8.GetBytes(secret));
        hmac.AppendData(Encoding.ASCII.GetBytes(timestamp + ".")); hmac.AppendData(payload);
        var expected = hmac.GetHashAndReset(); var accepted = false;
        foreach (var signature in signatures) accepted |= CryptographicOperations.FixedTimeEquals(expected, signature);
        if (!accepted) throw new StripeSignatureException();
    }
}
