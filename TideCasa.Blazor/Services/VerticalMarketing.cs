using System.Reflection;
namespace TideCasa.Blazor.Services;

// Public, immutable marketing documents. No workspace or account data is read here.
public static class VerticalMarketing
{
    private static readonly IReadOnlyDictionary<string, string> Pages = new[] { "fit", "driver", "collect" }
        .ToDictionary(name => name, name => {
            using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream($"TideCasa.Blazor.MarketingPages.{name}.html")
                ?? throw new InvalidOperationException("Missing public marketing document.");
            using var reader = new StreamReader(stream);
            return reader.ReadToEnd();
        });
    public static string CollectHome(string previewForms) => Pages["collect"].Replace("@@COLLECT_PREVIEW@@", previewForms);
    public static void UseVerticalMarketing(this WebApplication app)
    {
        app.Use(async (context, next) => {
            var host = context.Request.Host.Host.ToLowerInvariant();
            var path = context.Request.Path.Value?.TrimEnd('/') ?? "";
            var name = path == "" ? host switch { "fit.tide.casa" => "fit", "driver.tide.casa" => "driver", _ => null }
                : app.Environment.IsDevelopment() ? path switch { "/fitness" => "fit", "/delivery" => "driver", _ => null } : null;
            if (name is null || !HttpMethods.IsGet(context.Request.Method)) { await next(); return; }
            context.Response.ContentType = "text/html; charset=utf-8";
            context.Response.Headers.CacheControl = "public, max-age=300";
            context.Response.Headers.XContentTypeOptions = "nosniff";
            context.Response.Headers["Referrer-Policy"] = "strict-origin-when-cross-origin";
            context.Response.Headers["Content-Security-Policy"] = "default-src 'none'; script-src 'self'; style-src 'self'; img-src 'self' data:; font-src 'self'; base-uri 'none'; frame-ancestors 'none'; form-action 'self'";
            await context.Response.WriteAsync(Pages[name]);
        });
    }
}
