using System.Globalization;

namespace TideCasa.Blazor.Services;

public static class DriverWorkFormat
{
    public static string Money(int cents) => (cents / 100m).ToString("C2", CultureInfo.GetCultureInfo("en-US"));
    public static int NetworkFee(int cents) => (cents * 5 + 50) / 100;
    public static string Time(string value) => DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.None, out var date)
        ? date.ToUniversalTime().ToString("MMM d, yyyy HH:mm 'UTC'", CultureInfo.InvariantCulture) : value;
    public static string Status(string value) => value switch
    {
        "pending" or "offered" => "Offer awaiting driver response", "active" or "accepted" => "Active agreement", "declined" => "Declined", "ended" => "Ended",
        "pending_approval" => "Awaiting owner approval", "creating" => "Preparing checkout", "open" => "Awaiting payment",
        "paid" => "Credited to driver Stripe account", "review" => "Needs review", "refunded" => "Refunded", "disputed" => "Disputed", "expired" => "Checkout expired", _ => value
    };
    public static string? HostedUrl(string? value, string host) => Uri.TryCreate(value, UriKind.Absolute, out var uri)
        && uri.Scheme == "https" && uri.IdnHost.Equals(host, StringComparison.OrdinalIgnoreCase) && uri.UserInfo.Length == 0 && uri.IsDefaultPort
        ? uri.AbsoluteUri : null;
}
