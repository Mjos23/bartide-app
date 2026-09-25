using TideCasa.Blazor.Features.Authentication;

namespace TideCasa.Blazor.Services;

public static class CustomerExperience
{
    public const string Host = "order.tide.casa";
    public const string AccountPath = "/customer/account";
    public static bool IsHost(string host) => host.Equals(Host, StringComparison.OrdinalIgnoreCase);
    public static bool IsCustomerPath(string? value)
    {
        var path = (value ?? "").Split('?', '#')[0];
        return path is "/customer" or "/restaurants" or "/nearby"
            || path.StartsWith("/customer/", StringComparison.OrdinalIgnoreCase)
            || path.StartsWith("/order/", StringComparison.OrdinalIgnoreCase)
            || path.StartsWith("/menu/", StringComparison.OrdinalIgnoreCase)
            || path.StartsWith("/bar/", StringComparison.OrdinalIgnoreCase)
            || path.StartsWith("/rewards/", StringComparison.OrdinalIgnoreCase)
            || path.StartsWith("/events/", StringComparison.OrdinalIgnoreCase);
    }
    public static bool IsEntry(string url, string? returnTo)
    {
        var uri = new Uri(url);
        return IsHost(uri.Host) || IsCustomerPath(uri.AbsolutePath) || IsCustomerPath(AuthFlow.SafeReturnPath(returnTo));
    }
    public static string ReturnPath(string? input, bool customer)
    {
        var path = AuthFlow.SafeReturnPath(input);
        return customer && path == "/account" ? AccountPath : path;
    }
    public static string AuthPage(string page, bool customer) => customer ? "/customer" + page : page;
    public static string SignIn(string? returnTo = null) => AuthLink("/signin", returnTo);
    public static string SignUp(string? returnTo = null) => AuthLink("/signup", returnTo);
    private static string AuthLink(string page, string? returnTo) => AuthPage(page, true) + "?return_to="
        + Uri.EscapeDataString(ReturnPath(returnTo, true));
    public static string Home(string url) => IsHost(new Uri(url).Host) ? "/" : "/customer";
    public static string Entrance(string url) => PublicSeo.IsPublicHost(new Uri(url).Host) ? "https://order.tide.casa/" : "/customer";
}
