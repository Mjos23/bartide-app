using System.Globalization;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Http.Features;
using TideCasa.Blazor.Features.Authentication;
using TideCasa.Blazor.Services;
using TideCasa.Contracts;

namespace TideCasa.Blazor.Features.Events;

public static class EventsFlow
{
    public static readonly string[] TimeZones = ["America/New_York", "America/Chicago", "America/Denver", "America/Los_Angeles", "America/Anchorage", "Pacific/Honolulu", "UTC"];
    public static void MapTideCasaEvents(this WebApplication app)
    {
        app.MapPost("/event-management/{tenant}/save", SaveAsync);
        app.MapPost("/event-management/{tenant}/{eventId}/cancel", CancelAsync);
        app.MapPost("/event-management/{tenant}/{eventId}/check-in", CheckInAsync);
        app.MapPost("/event-reservations/{slug}/{eventId}", RsvpAsync);
    }
    private static async Task<IResult> SaveAsync(string tenant, HttpContext context, IAntiforgery csrf, EventsClient api)
    {
        var read = await Read(context, csrf); if (read.Error is not null) return read.Error;
        if (!Identifier(tenant)) return Results.NotFound();
        try
        {
            var form = read.Form!; var eventId = Text(form, "event_id", 128);
            if (eventId.Length != 0 && !Guid.TryParseExact(eventId, "D", out _)) throw new FormFailure("invalid");
            var zone = Text(form, "time_zone", 80, true);
            var request = new SaveEventRequest(Key(form), Number(form, "version", 0, int.MaxValue), Text(form, "title", 160, true), Text(form, "details", 4000, multiline: true), Text(form, "location", 300, true),
                Instant(form, "starts_at", zone), Instant(form, "ends_at", zone), zone, Number(form, "capacity", 1, 500), Check(form, "published"));
            var response = await api.SendAsync<RestaurantEvent>(EventsClient.OwnerPath(tenant, eventId.Length == 0 ? "" : "/" + eventId), HttpMethod.Post, request, read.Token, context.RequestAborted);
            return Redirect(Owner(tenant), NoticeCode(response));
        }
        catch (FormFailure error) { return Redirect(Owner(tenant), error.Code); }
    }
    private static async Task<IResult> CancelAsync(string tenant, string eventId, HttpContext context, IAntiforgery csrf, EventsClient api)
    {
        var read = await Read(context, csrf); if (read.Error is not null) return read.Error;
        if (!Identifier(tenant) || !Guid.TryParseExact(eventId, "D", out _)) return Results.NotFound();
        try
        {
            if (!Check(read.Form!, "confirm_cancel")) throw new FormFailure("confirm-cancel");
            var result = await api.SendAsync<RestaurantEvent>(EventsClient.OwnerPath(tenant, "/" + eventId + "/cancel"), HttpMethod.Post,
                new CancelEventRequest(Key(read.Form!), Number(read.Form!, "version", 0, int.MaxValue)), read.Token, context.RequestAborted);
            return Redirect(Owner(tenant), NoticeCode(result));
        }
        catch (FormFailure error) { return Redirect(Owner(tenant), error.Code); }
    }
    private static async Task<IResult> CheckInAsync(string tenant, string eventId, HttpContext context, IAntiforgery csrf, EventsClient api)
    {
        var read = await Read(context, csrf); if (read.Error is not null) return read.Error;
        if (!Identifier(tenant) || !Guid.TryParseExact(eventId, "D", out _)) return Results.NotFound();
        var path = Owner(tenant) + "?event=" + eventId;
        try
        {
            if (!Check(read.Form!, "confirm_present")) throw new FormFailure("confirm-present");
            var result = await api.SendAsync<EventGuestList>(EventsClient.OwnerPath(tenant, "/" + eventId + "/check-in"), HttpMethod.Post,
                new CheckInEventRequest(Key(read.Form!), Text(read.Form!, "user_id", 128, true), Number(read.Form!, "version", 0, int.MaxValue)), read.Token, context.RequestAborted);
            return Redirect(path, NoticeCode(result));
        }
        catch (FormFailure error) { return Redirect(path, error.Code); }
    }
    private static async Task<IResult> RsvpAsync(string slug, string eventId, HttpContext context, IAntiforgery csrf, EventsClient api)
    {
        var read = await Read(context, csrf); if (read.Error is not null) return read.Error;
        if (!Identifier(slug) || !Guid.TryParseExact(eventId, "D", out _)) return Results.NotFound();
        try
        {
            var action = Text(read.Form!, "action", 20, true);
            if (action is not ("attend" or "cancel")) throw new FormFailure("invalid");
            var result = await api.SendAsync<MyEventRsvp>(EventsClient.PublicPath(slug, "/" + eventId + "/rsvp"), HttpMethod.Post,
                new SetEventRsvpRequest(Key(read.Form!), Number(read.Form!, "version", -1, int.MaxValue), action == "attend"), read.Token, context.RequestAborted);
            return Redirect(Public(slug), result.Succeeded ? action == "attend" ? "reserved" : "left" : NoticeCode(result));
        }
        catch (FormFailure error) { return Redirect(Public(slug), error.Code); }
    }
    private static async Task<FormRead> Read(HttpContext context, IAntiforgery csrf)
    {
        context.Response.Headers.CacheControl = "no-store"; context.Response.Headers.Pragma = "no-cache";
        context.Response.Headers["X-Frame-Options"] = "DENY"; context.Response.Headers["Referrer-Policy"] = "same-origin";
        if (context.Items.ContainsKey(AuthFlow.UnavailableItem)) return new(null, null, AuthFlow.ServiceUnavailable());
        if (context.User.Identity?.IsAuthenticated != true || context.Items[AuthFlow.TokenItem] is not string token) return new(null, null, Results.Unauthorized());
        if (!SameOrigin(context.Request) || !context.Request.HasFormContentType) return new(null, null, Results.BadRequest());
        if (context.Features.Get<IHttpMaxRequestBodySizeFeature>() is { IsReadOnly: false } limit) limit.MaxRequestBodySize = 64 * 1024;
        try
        {
            await csrf.ValidateRequestAsync(context); var form = await context.Request.ReadFormAsync(context.RequestAborted);
            if (form.Files.Count != 0 || form.Count > 20 || form.Any(x => x.Value.Count != 1)) return new(null, null, Results.BadRequest());
            return new(form, token, null);
        }
        catch (Exception error) when (error is AntiforgeryValidationException or InvalidDataException or BadHttpRequestException) { return new(null, null, Results.BadRequest()); }
    }
    public static string Owner(string tenant) => "/workspace/" + Uri.EscapeDataString(tenant) + "/events";
    public static string Public(string slug) => "/events/" + Uri.EscapeDataString(slug);
    private static IResult Redirect(string path, string code) => Results.LocalRedirect(path + (path.Contains('?') ? "&" : "?") + "notice=" + code);
    private static string NoticeCode<T>(EventsResult<T> result) => result.Succeeded ? "saved" : result.Uncertain ? "uncertain" : (int)result.Status is 401 or 403 ? "denied" : result.Code switch
    { "event_full" => "full", "capacity_below_reserved" => "capacity", "event_has_guests" => "has-guests", "event_closed" or "rsvp_closed" or "check_in_closed" => "closed", "event_changed" or "request_conflict" => "changed", _ => "invalid" };
    public static string? Notice(string? value) => value switch
    {
        "saved" => "Your event change is saved.", "reserved" => "You’re on the guest list. Your RSVP is for one person.", "left" => "Your reservation is cancelled.",
        "changed" => "These details changed while the page was open. Review the latest information and try again.",
        "uncertain" => "We couldn’t confirm your change. Check the current details before trying again.", "denied" => "Your account cannot make that change.",
        "full" => "This event is full. A place may become available if someone cancels.", "capacity" => "Capacity cannot be lower than the number of confirmed reservations.",
        "has-guests" => "This event has reservations. Keep it published or cancel it.", "closed" => "This action is no longer available for the event.",
        "confirm-cancel" => "Check the confirmation box before cancelling the event.", "confirm-present" => "Confirm the guest is present before checking them in.",
        "time" => "Check the dates and time zone. Times during a daylight-saving clock change may be skipped or repeated; choose an unambiguous time.",
        "invalid" => "Check the event details, dates and capacity, then try again.", _ => null
    };
    public static string Local(string instant, string zone) => TimeZoneInfo.ConvertTime(DateTimeOffset.Parse(instant, CultureInfo.InvariantCulture), TimeZoneInfo.FindSystemTimeZoneById(zone)).ToString("yyyy-MM-ddTHH:mm", CultureInfo.InvariantCulture);
    public static string When(RestaurantEvent item)
    {
        var zone = TimeZoneInfo.FindSystemTimeZoneById(item.TimeZone);
        var start = TimeZoneInfo.ConvertTime(DateTimeOffset.Parse(item.StartsAt, CultureInfo.InvariantCulture), zone);
        var end = TimeZoneInfo.ConvertTime(DateTimeOffset.Parse(item.EndsAt, CultureInfo.InvariantCulture), zone);
        var culture = CultureInfo.GetCultureInfo("en-US");
        return start.ToString("ddd, MMM d · h:mm tt", culture) + " – " + end.ToString(start.Date == end.Date ? "h:mm tt" : "ddd, MMM d · h:mm tt", culture) + " · " + item.TimeZone.Replace('_', ' ');
    }
    public static bool CheckInOpen(RestaurantEvent item) => item.State == "published" && DateTimeOffset.UtcNow >= DateTimeOffset.Parse(item.StartsAt).AddHours(-4) && DateTimeOffset.UtcNow <= DateTimeOffset.Parse(item.EndsAt);
    public static bool ReservationsOpen(RestaurantEvent item) => item.State == "published" && DateTimeOffset.Parse(item.StartsAt) > DateTimeOffset.UtcNow;
    private static string Instant(IFormCollection form, string field, string zone)
    {
        if (zone != "UTC" && !Regex.IsMatch(zone, "^[A-Za-z_+-]+/[A-Za-z_+/-]+$") || !DateTime.TryParseExact(form[field], "yyyy-MM-ddTHH:mm", CultureInfo.InvariantCulture, DateTimeStyles.None, out var local)) throw new FormFailure("time");
        TimeZoneInfo tz;
        try { tz = TimeZoneInfo.FindSystemTimeZoneById(zone); }
        catch (Exception error) when (error is TimeZoneNotFoundException or InvalidTimeZoneException) { throw new FormFailure("time"); }
        local = DateTime.SpecifyKind(local, DateTimeKind.Unspecified);
        if (tz.IsInvalidTime(local) || tz.IsAmbiguousTime(local)) throw new FormFailure("time");
        return new DateTimeOffset(TimeZoneInfo.ConvertTimeToUtc(local, tz)).ToString("O");
    }
    private static string Key(IFormCollection form) => Guid.TryParseExact(form["request_key"], "D", out _) ? form["request_key"].ToString() : throw new FormFailure("invalid");
    private static bool Identifier(string value) => Regex.IsMatch(value, "^[A-Za-z0-9_-]{1,128}$");
    private static int Number(IFormCollection form, string field, int min, int max) => int.TryParse(form[field], NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var value) && value >= min && value <= max ? value : throw new FormFailure("invalid");
    private static bool Check(IFormCollection form, string field) => form[field].ToString() switch { "" => false, "true" => true, _ => throw new FormFailure("invalid") };
    private static string Text(IFormCollection form, string field, int max, bool required = false, bool multiline = false)
    {
        var value = form[field].ToString().Trim();
        if (value.Length > max || required && value.Length == 0 || value.Any(c => char.IsControl(c) && !(multiline && c is '\r' or '\n' or '\t'))) throw new FormFailure("invalid");
        return value;
    }
    private static bool SameOrigin(HttpRequest request)
    {
        var origin = request.Headers.Origin.ToString(); var candidate = origin.Length == 0 ? request.Headers.Referer.ToString() : origin;
        return Uri.TryCreate(candidate, UriKind.Absolute, out var uri) && uri.UserInfo.Length == 0 && uri.Scheme == request.Scheme && uri.IdnHost.Equals(request.Host.Host, StringComparison.OrdinalIgnoreCase)
            && uri.Port == (request.Host.Port ?? (request.Scheme == "https" ? 443 : 80)) && (origin.Length == 0 || uri.AbsolutePath == "/") && request.Headers["Sec-Fetch-Site"] != "cross-site";
    }
    private sealed record FormRead(IFormCollection? Form, string? Token, IResult? Error);
    private sealed class FormFailure(string code) : Exception { public string Code { get; } = code; }
}
