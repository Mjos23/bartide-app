using System.Globalization;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Http.Features;
using TideCasa.Blazor.Features.Authentication;
using TideCasa.Blazor.Services;
using TideCasa.Contracts;

namespace TideCasa.Blazor.Features.Ordering;

public static partial class RestaurantOrderingFlow
{
    public static IEndpointRouteBuilder MapRestaurantOrdering(this IEndpointRouteBuilder endpoints)
    {
        MapCustomerOrders(endpoints);
        endpoints.MapGet("/ordering/qr/{slug}/{table}", QrAsync);
        endpoints.MapPost("/ordering/manage/{tenantId}/tables", CreateAsync).RequireAuthorization();
        endpoints.MapPost("/ordering/manage/{tenantId}/tables/{tableId}", SetAsync).RequireAuthorization();
        return endpoints;
    }

    private static async Task<IResult> QrAsync(string slug, string table, HttpContext context, RestaurantOrderingClient api)
    {
        if (!Identifier().IsMatch(slug) || !Identifier().IsMatch(table)) return Results.NotFound();
        var bytes = await api.GetQrAsync(slug, table, context.RequestAborted);
        if (bytes is null) return Results.NotFound();
        // Render only as an image, never inject provider SVG into a component's HTML.
        context.Response.Headers["Content-Security-Policy"] = "default-src 'none'; sandbox";
        context.Response.Headers["X-Content-Type-Options"] = "nosniff";
        context.Response.Headers["Referrer-Policy"] = "no-referrer";
        context.Response.Headers.CacheControl = "no-store";
        return Results.Bytes(bytes, "image/svg+xml");
    }

    private static Task<IResult> CreateAsync(string tenantId, HttpContext context, IAntiforgery antiforgery, RestaurantOrderingClient api) =>
        ManageAsync(tenantId, null, context, antiforgery, api);

    private static Task<IResult> SetAsync(string tenantId, string tableId, HttpContext context, IAntiforgery antiforgery, RestaurantOrderingClient api) =>
        ManageAsync(tenantId, tableId, context, antiforgery, api);

    private static async Task<IResult> ManageAsync(string tenantId, string? tableId, HttpContext context, IAntiforgery antiforgery, RestaurantOrderingClient api)
    {
        if (!Identifier().IsMatch(tenantId) || (tableId is not null && !Identifier().IsMatch(tableId))) return Results.NotFound();
        if (context.Items.ContainsKey(AuthFlow.UnavailableItem)) return AuthFlow.ServiceUnavailable();
        if (context.User.Identity?.IsAuthenticated != true || context.Items[AuthFlow.TokenItem] is not string token) return Results.Unauthorized();
        if (!SameOrigin(context.Request) || !context.Request.HasFormContentType) return Results.BadRequest();
        var body = context.Features.Get<IHttpMaxRequestBodySizeFeature>();
        if (body is { IsReadOnly: false }) body.MaxRequestBodySize = 8 * 1024;
        IFormCollection form;
        try
        {
            await antiforgery.ValidateRequestAsync(context);
            form = await context.Request.ReadFormAsync(context.RequestAborted);
            if (form.Files.Count != 0 || form.Count > 5 || form.Any(field => field.Value.Count != 1)) return Results.BadRequest();
        }
        catch (Exception error) when (error is AntiforgeryValidationException or InvalidDataException or BadHttpRequestException)
        { return Redirect(tenantId, "expired"); }
        if (!int.TryParse(form["version"], NumberStyles.None, CultureInfo.InvariantCulture, out var version) || version < 0) return Redirect(tenantId, "invalid");
        RestaurantApiResult<RestaurantOrderingWorkspace> result;
        if (tableId is null)
        {
            var label = form["label"].ToString().Trim();
            if (label.Length is < 1 or > 40 || label.Any(char.IsControl)) return Redirect(tenantId, "invalid");
            result = await api.CreateTableAsync(tenantId, new(version, label), token, context.RequestAborted);
        }
        else
        {
            if (!bool.TryParse(form["enabled"], out var enabled)) return Redirect(tenantId, "invalid");
            result = await api.SetTableAsync(tenantId, tableId, new(version, enabled), token, context.RequestAborted);
        }
        return Redirect(tenantId, result.Succeeded ? "saved" : result.Code == "stale_settings" ? "changed"
            : (int)result.Status is 401 or 403 ? "denied" : result.Uncertain ? "unconfirmed" : "invalid");
    }

    public static string? Notice(string? value) => value switch
    {
        "saved" => "Your table settings are saved.",
        "changed" => "Settings changed while this page was open. Review the current tables before trying again.",
        "expired" => "That form expired. Review the current tables and try again.",
        "denied" => "Your account cannot change this workspace’s table settings.",
        "unconfirmed" => "We couldn’t confirm that change. Check the current tables before trying again.",
        "invalid" => "The change was not saved. Use a unique table name and check that ordering is enabled for this business.",
        _ => null
    };

    private static IResult Redirect(string tenantId, string notice) => Results.LocalRedirect("/workspace/" + Uri.EscapeDataString(tenantId) + "/ordering?notice=" + notice);

    private static bool SameOrigin(HttpRequest request)
    {
        if (!Uri.TryCreate(request.Scheme + "://" + request.Host, UriKind.Absolute, out var expected)) return false;
        var origin = request.Headers.Origin.ToString();
        var value = string.IsNullOrEmpty(origin) ? request.Headers.Referer.ToString() : origin;
        return Uri.TryCreate(value, UriKind.Absolute, out var source) && string.IsNullOrEmpty(source.UserInfo)
            && source.Scheme == expected.Scheme && source.IdnHost.Equals(expected.IdnHost, StringComparison.OrdinalIgnoreCase)
            && source.Port == expected.Port && (string.IsNullOrEmpty(origin) || source.AbsolutePath == "/")
            && request.Headers["Sec-Fetch-Site"] != "cross-site";
    }

    [GeneratedRegex("^[A-Za-z0-9_-]{1,128}$", RegexOptions.CultureInvariant)]
    private static partial Regex Identifier();
}
