using System.Xml.Linq;

namespace TideCasa.Blazor.Services;

public sealed record PublicPageSeo(string Title, string Description, string Canonical);

// Only reviewed public marketing pages belong in this catalog. Never derive a
// canonical origin from request headers or include account/tenant identifiers.
public static class PublicSeo
{
    private static readonly PublicPageSeo Tide = new(
        "Tide Casa | Custom Apps for Small Businesses",
        "Tide Casa builds and hosts custom apps for small businesses. Explore menus, rewards, team tools and events, then book a personal walkthrough.",
        "https://tide.casa/");
    private static readonly PublicPageSeo Bar = new(
        "BarTide | Apps for Bars and Restaurants | Tide Casa",
        "BarTide by Tide Casa brings menus, customer rewards, team tools and events into one app for bars and restaurants. Explore the sample and book a demo.",
        "https://bar.tide.casa/");
    private static readonly PublicPageSeo Sample = new(
        "Explore the BarTide Restaurant App | Tide Casa",
        "Try BarTide's illustrative restaurant app: menus, rewards, team tools and events. Sample orders are simulated; restaurant payment checkout is not enabled.",
        "https://bar.tide.casa/enhanced-demo");
    private static readonly PublicPageSeo Demo = new(
        "Book a Small Business App Demo | Tide Casa",
        "Book a personal Tide Casa or BarTide walkthrough. Discuss your small business, explore the app tools and agree on the features that fit your needs.",
        "https://tide.casa/book-a-demo");
    private static readonly PublicPageSeo Careers = new(
        "Sales Engineer Opportunities | Tide Casa",
        "Help small businesses get their own apps with Tide Casa. Learn about the sales engineer role, referral commissions and application process.",
        "https://tide.casa/careers");
    private static readonly PublicPageSeo Terms = new(
        "App Pricing, Maintenance and Service Terms | Tide Casa",
        "Review Tide Casa and BarTide app setup, monthly maintenance, build and launch steps, cancellation terms and optional app-store submission support.",
        "https://tide.casa/service-terms");

    public static bool IsPublicHost(string host) =>
        host.Equals("tide.casa", StringComparison.OrdinalIgnoreCase)
        || host.Equals("bar.tide.casa", StringComparison.OrdinalIgnoreCase);

    public static PublicPageSeo? Get(string host, string path) => path.TrimEnd('/').ToLowerInvariant() switch
    {
        "" => host.Equals("bar.tide.casa", StringComparison.OrdinalIgnoreCase) ? Bar : Tide,
        "/restaurant" => Bar,
        "/enhanced-demo" => Sample,
        "/book-a-demo" or "/contact" => Demo,
        "/careers" => Careers,
        "/service-terms" => Terms,
        _ => null
    };

    // Keep the local preview navigable; production links use the preferred URL.
    public static string Link(string currentUrl, string path)
    {
        var uri = new Uri(currentUrl);
        return IsPublicHost(uri.Host) ? (path == "/" ? Tide.Canonical : Get(uri.Host, path)?.Canonical ?? path) : path;
    }

    public static void UsePublicSeo(this WebApplication app)
    {
        app.Use(async (context, next) =>
        {
            context.Response.OnStarting(() =>
            {
                var path = context.Request.Path.Value ?? "/";
                if (!IsPublicHost(context.Request.Host.Host)
                    || (Get(context.Request.Host.Host, path) is null && !Path.HasExtension(path))
                    || context.Response.StatusCode >= 400)
                    context.Response.Headers["X-Robots-Tag"] = "noindex, nofollow";
                return Task.CompletedTask;
            });
            await next();
        });
    }

    public static void MapPublicSeo(this WebApplication app)
    {
        app.MapGet("/robots.txt", (HttpContext context) =>
        {
            if (!IsPublicHost(context.Request.Host.Host))
                return Results.Text("User-agent: *\nDisallow: /\n", "text/plain");
            var origin = context.Request.Host.Host.Equals("bar.tide.casa", StringComparison.OrdinalIgnoreCase)
                ? "https://bar.tide.casa" : "https://tide.casa";
            const string rules = "Allow: /\nDisallow: /api/\nDisallow: /_blazor\nDisallow: /private-media/\nDisallow: /health\n";
            // OAI-SearchBot governs search. Do not change the separate GPTBot
            // training policy as part of this search-discovery change.
            return Results.Text("User-agent: *\n" + rules + "\nUser-agent: OAI-SearchBot\n" + rules
                + "\nSitemap: " + origin + "/sitemap.xml\n", "text/plain");
        });
        app.MapGet("/sitemap.xml", (HttpContext context) =>
        {
            PublicPageSeo[] pages = context.Request.Host.Host.Equals("bar.tide.casa", StringComparison.OrdinalIgnoreCase)
                ? [Bar, Sample] : [Tide, Demo, Careers, Terms];
            XNamespace ns = "http://www.sitemaps.org/schemas/sitemap/0.9";
            var document = new XDocument(new XElement(ns + "urlset",
                pages.Select(page => new XElement(ns + "url", new XElement(ns + "loc", page.Canonical)))));
            return Results.Text(document.ToString(), "application/xml");
        });
    }
}
