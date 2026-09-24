using System.Globalization;
using Microsoft.AspNetCore.Antiforgery;
using TideCasa.Blazor.Services;
using TideCasa.Contracts;

namespace TideCasa.Blazor.Features.RestaurantManagement;

public static partial class RestaurantManagementFlow
{
    public static void MapDeliveryDispatchFlow(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapPost("/restaurant-management/{tenantId}/dispatch/{operation}", SaveDispatchAsync).RequireAuthorization();
        endpoints.MapPost("/restaurant-management/{tenantId}/dispatch/orders/{orderId}", SaveDispatchPlanAsync).RequireAuthorization();
    }

    private static async Task<IResult> SaveDispatchAsync(string tenantId, string operation, HttpContext context,
        IAntiforgery antiforgery, RestaurantManagementClient api, StaffTrainingClient team)
    {
        if (!Identifier().IsMatch(tenantId)) return Results.NotFound();
        var read = await ReadAsync(context, antiforgery);
        if (read.Failure is { } failure) return failure;
        var form = read.Form!;
        try
        {
            string Id(string key) => Identifier().IsMatch(Text(form, key, 128, true)) ? Text(form, key, 128, true) : throw new FormFailure("dispatch-invalid");
            if (operation == "settings")
                return DispatchResponse(tenantId, await api.SaveDispatchSettingsAsync(tenantId, new(Version(form), Checkbox(form, "automatic")), read.Token!, context.RequestAborted));
            if (operation == "run")
            {
                var result = await api.RunDispatchAsync(tenantId, read.Token!, context.RequestAborted);
                return result.Succeeded
                    ? Redirect(tenantId, "deliveries", result.Value!.Assigned == 0 ? "dispatch-none" : "dispatch-assigned")
                    : DispatchResponse(tenantId, result);
            }
            if (operation == "profile")
            {
                var availability = Text(form, "availability", 20, true);
                if (availability is not ("available" or "offline" or "scheduled")) throw new FormFailure("dispatch-invalid");
                if (!int.TryParse(form["capacity"], NumberStyles.None, CultureInfo.InvariantCulture, out var capacity) || capacity is < 1 or > 10)
                    throw new FormFailure("dispatch-invalid");
                var zips = Text(form, "zips", 500, multiline: true).Split([',', ' ', '\r', '\n', '\t'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Distinct(StringComparer.Ordinal).ToArray();
                if (zips.Length > 50 || zips.Any(zip => !Zip().IsMatch(zip))) throw new FormFailure("dispatch-invalid");
                return DispatchResponse(tenantId, await api.SaveDriverProfileAsync(tenantId, Id("driver"), new(Version(form), availability, capacity, zips), read.Token!, context.RequestAborted));
            }
            StaffTrainingResult teamResult;
            if (operation == "driver")
                teamResult = await team.SendAsync(tenantId, "/members", HttpMethod.Post, new AddTeamMemberRequest(Text(form, "name", 80, true), Text(form, "email", 254, true), "driver"), read.Token!, context.RequestAborted);
            else if (operation == "shift")
                teamResult = await team.SendAsync(tenantId, "/shifts", HttpMethod.Post, new AddTeamShiftRequest(Id("driver"), DispatchTime(form, "starts"), DispatchTime(form, "ends"), Text(form, "label", 80, true)), read.Token!, context.RequestAborted);
            else if (operation == "remove-shift")
                teamResult = await team.SendAsync(tenantId, "/shifts/" + Uri.EscapeDataString(Id("shift")), HttpMethod.Delete, null, read.Token!, context.RequestAborted);
            else return Results.NotFound();
            return Redirect(tenantId, "deliveries", teamResult.Succeeded ? "dispatch-saved" : (int)teamResult.Status is 401 or 403 ? "denied" : teamResult.Uncertain ? "unconfirmed" : teamResult.Code switch
            {
                "shift_overlap" => "dispatch-overlap", "member_exists" => "dispatch-member-exists", _ => "dispatch-invalid"
            });
        }
        catch (FormFailure formError) { return Redirect(tenantId, "deliveries", formError.Notice == "dispatch-time" ? formError.Notice : "dispatch-invalid"); }
    }

    private static async Task<IResult> SaveDispatchPlanAsync(string tenantId, string orderId, HttpContext context,
        IAntiforgery antiforgery, RestaurantManagementClient api)
    {
        if (!Identifier().IsMatch(tenantId) || !Guid.TryParseExact(orderId, "D", out _)) return Results.NotFound();
        var read = await ReadAsync(context, antiforgery);
        if (read.Failure is { } failure) return failure;
        var form = read.Form!;
        try
        {
            var mode = Text(form, "mode", 20, true);
            if (mode is not ("manual" or "automatic" or "scheduled")) throw new FormFailure("dispatch-invalid");
            var preferred = mode == "scheduled" ? Text(form, "driver", 128) : "";
            if (preferred.Length > 0 && !Identifier().IsMatch(preferred)) throw new FormFailure("dispatch-invalid");
            var when = mode == "scheduled" ? DispatchTime(form, "dispatch_at") : null;
            return DispatchResponse(tenantId, await api.SaveAssignmentPlanAsync(tenantId, orderId,
                new(Version(form), mode, when, preferred.Length == 0 ? null : preferred), read.Token!, context.RequestAborted));
        }
        catch (FormFailure formError) { return Redirect(tenantId, "deliveries", formError.Notice == "dispatch-time" ? formError.Notice : "dispatch-invalid"); }
    }

    // Native datetime-local values have no offset: convert the explicitly selected zone at the server boundary.
    private static string DispatchTime(IFormCollection form, string key)
    {
        var zone = Text(form, "timezone", 40, true);
        if (zone is not ("America/New_York" or "America/Chicago" or "America/Denver" or "America/Los_Angeles" or "UTC")) throw new FormFailure("dispatch-time");
        if (!DateTime.TryParseExact(Text(form, key, 30, true), ["yyyy-MM-ddTHH:mm", "yyyy-MM-ddTHH:mm:ss"], CultureInfo.InvariantCulture, DateTimeStyles.None, out var local)) throw new FormFailure("dispatch-time");
        try
        {
            var timezone = TimeZoneInfo.FindSystemTimeZoneById(zone);
            if (timezone.IsInvalidTime(local) || timezone.IsAmbiguousTime(local)) throw new FormFailure("dispatch-time");
            return new DateTimeOffset(DateTime.SpecifyKind(local, DateTimeKind.Unspecified), timezone.GetUtcOffset(local)).ToUniversalTime().ToString("O", CultureInfo.InvariantCulture);
        }
        catch (Exception error) when (error is TimeZoneNotFoundException or InvalidTimeZoneException) { throw new FormFailure("dispatch-time"); }
    }

    private static IResult DispatchResponse<T>(string tenant, RestaurantApiResult<T> result) => Redirect(tenant, "deliveries",
        result.Succeeded ? "dispatch-saved" : (int)result.Status is 401 or 403 ? "denied" : result.Uncertain ? "unconfirmed"
        : result.Code == "delivery_disabled" ? "dispatch-disabled" : result.Code == "driver_unavailable" ? "dispatch-driver"
        : (int)result.Status == 409 ? "dispatch-changed" : "dispatch-invalid");

    public static string? DispatchNotice(string? code) => code switch
    {
        "dispatch-saved" => "Your delivery change is saved.",
        "dispatch-assigned" => "Available drivers have been assigned to eligible orders. Review the updated order list.",
        "dispatch-none" => "No orders were assigned. Orders must be accepted, due for dispatch and match an available driver with a connected account, ZIP coverage and remaining capacity.",
        "dispatch-changed" => "The driver, order or dispatch settings changed. Review the latest information before saving again.",
        "dispatch-overlap" => "This driver already has a shift during those hours. Choose another time.",
        "dispatch-member-exists" => "That email is already on the team. Manage the existing account from Team and schedule.",
        "dispatch-disabled" => "Enable the delivery workflow in delivery settings before using automatic or scheduled dispatch.",
        "dispatch-driver" => "This driver is no longer available to this workspace. Refresh the driver list and choose an active driver with a verified sign-in account.",
        "dispatch-time" => "Choose a valid date, time and time zone. For a skipped or repeated daylight-saving hour, enter the equivalent time using UTC.",
        "dispatch-invalid" => "The delivery change was not saved. Use a capacity of 1–10, five-digit ZIP codes and valid dates. Schedule dispatch within the next 90 days; shifts must last 15 minutes to 16 hours.",
        _ => null
    };
}
