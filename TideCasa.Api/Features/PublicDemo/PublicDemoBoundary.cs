using Microsoft.AspNetCore.Routing;

namespace TideCasa.Api.Features.PublicDemo;

public static class PublicDemoBoundary
{
    public static void UsePublicDemoBoundary(this WebApplication app)
    {
        // Resolve eagerly before migrations/hosted workers: unsafe configuration must never start.
        var options = app.Services.GetRequiredService<PublicDemoOptions>();
        if (!options.Enabled) return;
        app.Use(async (context, next) =>
        {
            context.Response.Headers["X-Robots-Tag"] = "noindex, nofollow, noarchive";
            // MapGroup(...).MapGet("") registers a trailing slash in the template.
            // Compare the same endpoint spelling used by the explicit allowlist.
            var pattern = (context.GetEndpoint() as RouteEndpoint)?.RoutePattern.RawText?.TrimEnd('/');
            if (pattern is null || !Routes.Contains((context.Request.Method, pattern)) || !IsDemoVenue(pattern, context))
            {
                await Results.Problem(statusCode: 403, title: "This action is unavailable in the shared fictional demo.",
                    extensions: new Dictionary<string, object?> { ["code"] = "demo_boundary" }).ExecuteAsync(context);
                return;
            }
            await next();
        });
    }

    private static bool IsDemoVenue(string pattern, HttpContext context)
    {
        if (pattern.StartsWith("/api/v1/tenants/", StringComparison.Ordinal))
            return (context.Request.RouteValues["tenant"] ?? context.Request.RouteValues["id"])?.ToString() == PublicDemoOptions.Tenant;
        if (pattern.StartsWith("/api/v1/restaurants/", StringComparison.Ordinal))
            return context.Request.RouteValues["slug"]?.ToString() == PublicDemoOptions.Tenant;
        return true;
    }

    // Exact endpoint templates keep future financial/admin endpoints denied by default.
    // Underlying stores still enforce active tenant membership and each person's real role.
    private static readonly HashSet<(string Method, string Pattern)> Routes = BuildRoutes();
    private static HashSet<(string, string)> BuildRoutes()
    {
        var routes = new HashSet<(string, string)>();
        void Add(string method, string prefix, params string[] suffixes)
        { foreach (var suffix in suffixes) routes.Add((method, prefix + suffix)); }
        Add("GET", "", "/health", "/api/v1/account", "/api/v1/tenants/{id}/access");
        Add("POST", "/api/v1/auth", "/demo-switch", "/signout");
        const string tenant = "/api/v1/tenants/{id}";
        Add("GET", tenant, "/menu", "/ordering", "/ordering/settings", "/ordering/operations", "/team", "/events",
            "/events/{eventId}/guests", "/rewards", "/rewards/employee", "/posts");
        Add("POST", tenant, "/menu/profile", "/menu/categories", "/menu/items", "/menu/categories/{entryId}/remove", "/menu/items/{entryId}/remove",
            "/ordering/tables", "/ordering/tables/{tableId}", "/ordering/settings", "/ordering/orders/{orderId}",
            "/team/members", "/team/members/{memberId}", "/team/shifts", "/team/messages", "/team/courses", "/team/courses/{courseId}",
            "/team/lessons", "/team/lessons/{lessonId}", "/team/courses/{courseId}/assignments", "/team/lessons/{lessonId}/progress",
            "/events", "/events/{eventId}", "/events/{eventId}/cancel", "/events/{eventId}/check-in", "/rewards/rules", "/rewards/rules/{ruleId}",
            "/rewards/qualifications", "/rewards/qualifications/{qualificationId}/void", "/rewards/points", "/rewards/redemptions/{rewardId}/resolve",
            "/rewards/employee-points", "/posts", "/posts/{postId}/publish", "/posts/{postId}/hide");
        Add("GET", tenant, "/delivery-location");
        Add("POST", tenant, "/delivery-location/settings", "/delivery-location/{orderId}/start", "/delivery-location/{orderId}/point", "/delivery-location/{orderId}/stop");
        Add("DELETE", tenant, "/team/shifts/{shiftId}", "/team/lessons/{lessonId}");
        const string restaurant = "/api/v1/restaurants/{slug}";
        Add("GET", restaurant, "/menu", "/tables/{token}/qr", "/events", "/events/mine", "/rewards", "/posts");
        Add("POST", restaurant, "/delivery-location", "/quote", "/orders", "/track", "/events/{eventId}/rsvp", "/rewards/join", "/rewards/redemptions/{rewardId}/request", "/rewards/points-redemptions");
        Add("GET", "/api/v1/tenants/{tenant}/media", "", "/{kind}/{id}");
        Add("HEAD", "/api/v1/tenants/{tenant}/media", "/{kind}/{id}");
        Add("GET", "/api/v1/media/photos", "/{id}");
        Add("HEAD", "/api/v1/media/photos", "/{id}");
        return routes;
    }
}
