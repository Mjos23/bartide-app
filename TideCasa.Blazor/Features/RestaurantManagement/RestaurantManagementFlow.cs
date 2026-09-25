using System.Globalization;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Http.Features;
using TideCasa.Blazor.Features.Authentication;
using TideCasa.Blazor.Services;
using TideCasa.Contracts;

namespace TideCasa.Blazor.Features.RestaurantManagement;

public static partial class RestaurantManagementFlow
{
    public static IEndpointRouteBuilder MapRestaurantManagement(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapPost("/restaurant-management/{tenantId}/{operation}", SaveAsync).RequireAuthorization();
        endpoints.MapPost("/restaurant-management/{tenantId}/{kind}/{entryId}/remove", RemoveAsync).RequireAuthorization();
        endpoints.MapPost("/restaurant-management/{tenantId}/orders/{orderId}", ChangeOrderAsync).RequireAuthorization();
        return endpoints;
    }

    private static async Task<IResult> SaveAsync(string tenantId, string operation, HttpContext context, IAntiforgery antiforgery, RestaurantManagementClient api)
    {
        if (!Identifier().IsMatch(tenantId) || operation is not ("profile" or "directory" or "categories" or "items" or "settings")) return Results.NotFound();
        var read = await ReadAsync(context, antiforgery);
        if (read.Failure is { } failedRead) return failedRead;
        try
        {
            var form = read.Form!;
            var version = Version(form);
            if (operation == "directory")
            {
                double? Coordinate(string key, double limit)
                {
                    var text = Text(form, key, 32);
                    if (text.Length == 0) return null;
                    if (!double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var number)
                        || !double.IsFinite(number) || Math.Abs(number) > limit) throw new FormFailure("location");
                    return number;
                }
                var location = new RestaurantDirectoryLocation(Checkbox(form, "listed"), Text(form, "address", 300), Coordinate("latitude", 90), Coordinate("longitude", 180));
                return Response(tenantId, "menu", await api.SaveDirectoryAsync(tenantId, new(version, location), read.Token!, context.RequestAborted));
            }
            if (operation == "profile")
            {
                var website = Text(form, "website", 2048);
                if (website.Length > 0 && (!Uri.TryCreate(website, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps || uri.UserInfo.Length != 0))
                    throw new FormFailure("website");
                var profile = new RestaurantProfile(Text(form, "name", 160, true), Text(form, "area", 160), Text(form, "tagline", 250),
                    Text(form, "hours", 500, multiline: true), website, Text(form, "service_note", 1000, multiline: true));
                return Response(tenantId, "menu", await api.SaveProfileAsync(tenantId, new(version, profile), read.Token!, context.RequestAborted));
            }
            if (operation == "categories")
            {
                var category = new RestaurantCategory(EntryId(form, "id"), Text(form, "name", 80, true));
                return Response(tenantId, "menu", await api.SaveCategoryAsync(tenantId, new(version, category), read.Token!, context.RequestAborted));
            }
            if (operation == "items")
            {
                var price = Amount(form, "price", 1_000_000, true);
                var priceLabel = Text(form, "price_label", 160);
                if (price is null && priceLabel.Length == 0) throw new FormFailure("price");
                var item = new RestaurantMenuItem(EntryId(form, "id"), EntryId(form, "category_id"), Text(form, "name", 160, true),
                    Text(form, "description", 1000, multiline: true), price, priceLabel.Length == 0 ? null : priceLabel, Checkbox(form, "available"));
                var photo = form.ContainsKey("photo_id") ? Text(form, "photo_id", 36) : null;
                if (!string.IsNullOrEmpty(photo) && !Guid.TryParseExact(photo, "D", out _)) throw new FormFailure("invalid");
                return Response(tenantId, "menu", await api.SaveItemAsync(tenantId, new(version, item, photo), read.Token!, context.RequestAborted));
            }
            var zipText = Text(form, "delivery_zips", 500, multiline: true);
            var zips = zipText.Split([',', ' ', '\r', '\n', '\t'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Distinct(StringComparer.Ordinal).ToArray();
            if (zips.Length > 50 || zips.Any(zip => !Zip().IsMatch(zip))) throw new FormFailure("delivery");
            if (!int.TryParse(form["delivery_capacity"], NumberStyles.None, CultureInfo.InvariantCulture, out var capacity) || capacity is < 1 or > 30) throw new FormFailure("delivery");
            var settings = new SaveRestaurantOrderingSettingsRequest(version, Checkbox(form, "accepting_orders"), Checkbox(form, "pickup_enabled"),
                Checkbox(form, "delivery_enabled"), Checkbox(form, "pay_staff_enabled"), Checkbox(form, "tips_enabled"),
                Amount(form, "tax_percent", 2500, true), Amount(form, "delivery_fee", 5000)!.Value,
                Amount(form, "delivery_minimum", 100000)!.Value, capacity, zips,
                Text(form, "pickup_instructions", 500, multiline: true), Text(form, "payment_instructions", 500, multiline: true), Checkbox(form, "delivery_workflow_enabled"), Text(form, "contact_phone", 30));
            return Response(tenantId, "menu", await api.SaveSettingsAsync(tenantId, settings, read.Token!, context.RequestAborted));
        }
        catch (FormFailure failure) { return Redirect(tenantId, "menu", failure.Notice); }
    }

    private static async Task<IResult> RemoveAsync(string tenantId, string kind, string entryId, HttpContext context, IAntiforgery antiforgery, RestaurantManagementClient api)
    {
        if (!Identifier().IsMatch(tenantId) || !MenuId().IsMatch(entryId) || kind is not ("categories" or "items")) return Results.NotFound();
        var read = await ReadAsync(context, antiforgery);
        if (read.Failure is { } failedRead) return failedRead;
        try
        {
            if (!Checkbox(read.Form!, "confirm_remove")) throw new FormFailure("confirm-remove");
            var request = new RemoveRestaurantEntryRequest(Version(read.Form!));
            var result = kind == "categories"
                ? await api.RemoveCategoryAsync(tenantId, entryId, request, read.Token!, context.RequestAborted)
                : await api.RemoveItemAsync(tenantId, entryId, request, read.Token!, context.RequestAborted);
            return Response(tenantId, "menu", result);
        }
        catch (FormFailure failure) { return Redirect(tenantId, "menu", failure.Notice); }
    }

    private static async Task<IResult> ChangeOrderAsync(string tenantId, string orderId, HttpContext context, IAntiforgery antiforgery, RestaurantManagementClient api)
    {
        if (!Identifier().IsMatch(tenantId) || !Guid.TryParseExact(orderId, "D", out _)) return Results.NotFound();
        var read = await ReadAsync(context, antiforgery);
        if (read.Failure is { } failedRead) return failedRead;
        try
        {
            var form = read.Form!;
            var action = Text(form, "action", 40, true);
            if (action is not ("accepted" or "preparing" or "ready" or "out_for_delivery" or "completed" or "cancelled" or "assign-driver" or "mark-paid" or "acknowledge-delivery" or "report-delivery-problem" or "resolve-delivery-problem" or "confirm-delivery")) throw new FormFailure("invalid");
            if (action == "cancelled" && !Checkbox(form, "confirm_cancel")) throw new FormFailure("confirm-cancel");
            var collected = action == "mark-paid" && Checkbox(form, "payment_collected");
            if (action == "mark-paid" && !collected) throw new FormFailure("confirm-payment");
            string? driverId = null;
            if (action == "assign-driver")
            {
                driverId = Text(form, "driver_id", 128, true);
                if (!Identifier().IsMatch(driverId)) throw new FormFailure("driver");
            }
            var request = new ChangeRestaurantOrderRequest(Version(form), action, driverId, collected, Text(form, "delivery_note", 300, multiline: true), Text(form, "problem_code", 40));
            return Response(tenantId, read.Form!["return_page"] == "deliveries" ? "deliveries" : "operations", await api.ChangeOrderAsync(tenantId, orderId, request, read.Token!, context.RequestAborted));
        }
        catch (FormFailure failure) { return Redirect(tenantId, read.Form!["return_page"] == "deliveries" ? "deliveries" : "operations", failure.Notice); }
    }

    private static async Task<FormRead> ReadAsync(HttpContext context, IAntiforgery antiforgery)
    {
        context.Response.Headers.CacheControl = "no-store";
        context.Response.Headers.Pragma = "no-cache";
        context.Response.Headers["X-Frame-Options"] = "DENY";
        context.Response.Headers["Referrer-Policy"] = "same-origin";
        if (context.Items.ContainsKey(AuthFlow.UnavailableItem)) return new(null, null, AuthFlow.ServiceUnavailable());
        if (context.User.Identity?.IsAuthenticated != true || context.Items[AuthFlow.TokenItem] is not string token) return new(null, null, Results.Unauthorized());
        if (!SameOrigin(context.Request) || !context.Request.HasFormContentType) return new(null, null, FormError("Open this form from your workspace and try again."));
        var body = context.Features.Get<IHttpMaxRequestBodySizeFeature>();
        if (body is { IsReadOnly: false }) body.MaxRequestBodySize = 64 * 1024;
        try
        {
            await antiforgery.ValidateRequestAsync(context);
            var form = await context.Request.ReadFormAsync(context.RequestAborted);
            if (form.Files.Count != 0 || form.Count > 24 || form.Any(field => field.Value.Count != 1)) return new(null, null, FormError("Please check the form and try again."));
            return new(form, token, null);
        }
        catch (Exception error) when (error is AntiforgeryValidationException or InvalidDataException or BadHttpRequestException)
        { return new(null, null, FormError("This form expired. Return to your workspace, refresh the page and try again.")); }
    }

    private static IResult Response<T>(string tenant, string page, RestaurantApiResult<T> result) => Redirect(tenant, page,
        result.Succeeded ? "saved" : result.Code is "stale_menu" or "stale_settings" or "stale_order" ? "changed"
        : result.Code == "invalid_location" ? "location"
        : (int)result.Status is 401 or 403 ? "denied" : result.Uncertain ? "unconfirmed"
        : result.Code is "category_in_use" or "category_not_empty" ? "category-in-use" : (int)result.Status == 409 ? "conflict" : "invalid");

    private static IResult Redirect(string tenant, string page, string notice) => Results.LocalRedirect("/workspace/" + Uri.EscapeDataString(tenant) + "/" + page + "?notice=" + notice);
    private static IResult FormError(string message) => Results.Content("<!doctype html><html lang=\"en\"><head><meta charset=\"utf-8\"><meta name=\"viewport\" content=\"width=device-width,initial-scale=1\"><title>Workspace | Tide Casa</title></head><body><main><h1>Let’s try that again.</h1><p>" + System.Text.Encodings.Web.HtmlEncoder.Default.Encode(message) + "</p><a href=\"/account\">Return to your account</a></main></body></html>", "text/html; charset=utf-8", statusCode: 400);
    private static int Version(IFormCollection form) => int.TryParse(form["version"], NumberStyles.None, CultureInfo.InvariantCulture, out var version) && version >= 0 ? version : throw new FormFailure("invalid");
    private static string EntryId(IFormCollection form, string field) => MenuId().IsMatch(form[field].ToString()) ? form[field].ToString() : throw new FormFailure("invalid");
    private static bool Checkbox(IFormCollection form, string field) => form[field].ToString() switch { "" => false, "true" => true, _ => throw new FormFailure("invalid") };
    private static string Text(IFormCollection form, string field, int max, bool required = false, bool multiline = false)
    {
        var value = form[field].ToString().Trim();
        if (value.Length > max || required && value.Length == 0 || value.Any(c => char.IsControl(c) && !(multiline && c is ('\r' or '\n' or '\t')))) throw new FormFailure("invalid");
        return value;
    }
    private static int? Amount(IFormCollection form, string field, int maximum, bool optional = false)
    {
        var text = form[field].ToString().Trim();
        if (text.Length == 0 && optional) return null;
        if (text.Length > 16 || !decimal.TryParse(text, NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var amount)
            || amount < 0 || amount > maximum / 100m || decimal.Truncate(amount * 100) != amount * 100) throw new FormFailure("amount");
        return (int)(amount * 100);
    }
    private static bool SameOrigin(HttpRequest request)
    {
        if (!Uri.TryCreate(request.Scheme + "://" + request.Host, UriKind.Absolute, out var expected)) return false;
        var origin = request.Headers.Origin.ToString();
        var value = string.IsNullOrEmpty(origin) ? request.Headers.Referer.ToString() : origin;
        return Uri.TryCreate(value, UriKind.Absolute, out var source) && source.UserInfo.Length == 0 && source.Scheme == expected.Scheme
            && source.IdnHost.Equals(expected.IdnHost, StringComparison.OrdinalIgnoreCase) && source.Port == expected.Port
            && (origin.Length == 0 || source.AbsolutePath == "/") && request.Headers["Sec-Fetch-Site"] != "cross-site";
    }

    public static string? Notice(string? code) => code switch
    {
        "saved" => "Your change is saved.",
        "changed" => "Someone changed these details while the page was open. Review the latest information before trying again.",
        "denied" => "Your current account does not have permission to make that change.",
        "unconfirmed" => "We couldn’t confirm the change. Review the current information before trying again.",
        "conflict" => "That change conflicts with the current menu or order. Review the latest information and available actions.",
        "category-in-use" => "Move or remove the items in this category before removing the category.",
        "confirm-remove" => "Check the confirmation box before removing an item or category.",
        "confirm-cancel" => "Check the confirmation box before cancelling an order.",
        "confirm-payment" => "Only mark an order paid after collecting its full total directly from the customer. Check the confirmation box when collected.",
        "driver" => "Choose an available delivery driver.",
        "price" => "Enter a price or a display label such as Market price. Items without a numeric price cannot be ordered online.",
        "website" => "Use a complete HTTPS website address, such as https://example.com.",
        "amount" => "Use a non-negative amount with no more than two decimal places, within the limits shown on the form.",
        "delivery" => "Use five-digit delivery ZIP codes and a delivery capacity from 1 to 30.",
        "location" => "Enter the restaurant address and valid latitude and longitude before listing it in nearby search.",
        "invalid" => "The change was not saved. Check the field lengths, menu category and ordering settings, then try again.",
        _ => null
    };

    private sealed record FormRead(IFormCollection? Form, string? Token, IResult? Failure);
    private sealed class FormFailure(string notice) : Exception { public string Notice { get; } = notice; }
    [GeneratedRegex("^[A-Za-z0-9_-]{1,128}$", RegexOptions.CultureInvariant)] private static partial Regex Identifier();
    [GeneratedRegex("^[a-z][a-z0-9-]{0,63}$", RegexOptions.CultureInvariant)] private static partial Regex MenuId();
    [GeneratedRegex("^[0-9]{5}$", RegexOptions.CultureInvariant)] private static partial Regex Zip();
}
