using Npgsql;

namespace TideCasa.Blazor.Services;

public static class PublicDemoWeb
{
    public static bool Enabled(IConfiguration configuration) => configuration["PublicDemo:Enabled"] == "true";
    public static void Validate(IConfiguration configuration, IHostEnvironment environment)
    {
        if (!Enabled(configuration)) return;
        var database = new NpgsqlConnectionStringBuilder(configuration.GetConnectionString("Application"));
        if (configuration["Storage:Provider"] != "PostgreSql" || configuration["Storage:PostgresSchema"] != "tide_demo_gulf_lantern"
            || database.Username?.Split('.')[0] != "tide_demo_web" || configuration["DataProtection:Provider"] != "PostgreSql"
            || configuration["SampleBar:Enabled"] != "true" || !File.Exists(configuration["SampleBar:FixturePath"]))
            throw new InvalidOperationException("Public demo requires its dedicated database, key storage and fictional fixture.");
        var api = new Uri(configuration["Api:BaseUrl"] ?? "");
        if (!(api.AbsoluteUri == "http://api:8080/" || environment.IsDevelopment() && api.IsLoopback))
            throw new InvalidOperationException("Public demo must use its isolated internal API.");
    }
    public static void UsePublicDemoWeb(this WebApplication app)
    {
        if (!Enabled(app.Configuration)) return;
        app.Use(async (context, next) =>
        {
            var path = context.Request.Path.Value ?? "/";
            if (path is "/" or "/signin") { context.Response.Redirect("/sample-bar"); return; }
            if (path.StartsWith("/owner", StringComparison.OrdinalIgnoreCase) || path.StartsWith("/start", StringComparison.OrdinalIgnoreCase)
                || path.StartsWith("/account/referrals", StringComparison.OrdinalIgnoreCase)
                || path.StartsWith("/workspace/", StringComparison.OrdinalIgnoreCase) && (path.Contains("/billing", StringComparison.OrdinalIgnoreCase) || path.Contains("/payments", StringComparison.OrdinalIgnoreCase))
                || path.StartsWith("/purchase", StringComparison.OrdinalIgnoreCase) || path is "/signup" or "/forgot-password" or "/reset-password" or "/verify-email" or "/book-a-demo")
            { context.Response.StatusCode = 404; return; }
            await next();
        });
    }
}
