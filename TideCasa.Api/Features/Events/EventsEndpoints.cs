using System.Text.Json;
using System.Data.Common;
using TideCasa.Api.Features.Authentication;
using TideCasa.Api.Infrastructure;
using TideCasa.Contracts;

namespace TideCasa.Api.Features.Events;

public static class EventsEndpoints
{
    public static void MapTideCasaEvents(this WebApplication app)
    {
        var owners = app.MapGroup("/api/v1/tenants/{id}/events").WithTags("Events")
            .RequireAuthorization().WithMetadata(new ApiBodyLimit(64 * 1024)).AddEndpointFilter<EventsFilter>();
        owners.MapGet("", async (string id, HttpContext context, EventsStore store, CancellationToken ct) =>
            Results.Ok(await store.ListAsync(id, true, User(context), ct)));
        owners.MapPost("", async (string id, SaveEventRequest request, HttpContext context, EventsStore store, CancellationToken ct) =>
            Results.Ok(await store.SaveAsync(id, null, User(context), request, ct)));
        owners.MapPost("/{eventId}", async (string id, string eventId, SaveEventRequest request, HttpContext context, EventsStore store, CancellationToken ct) =>
            Results.Ok(await store.SaveAsync(id, eventId, User(context), request, ct)));
        owners.MapPost("/{eventId}/cancel", async (string id, string eventId, CancelEventRequest request, HttpContext context, EventsStore store, CancellationToken ct) =>
            Results.Ok(await store.CancelAsync(id, eventId, User(context), request, ct)));
        owners.MapGet("/{eventId}/guests", async (string id, string eventId, HttpContext context, EventsStore store, CancellationToken ct) =>
            Results.Ok(await store.GuestsAsync(id, eventId, User(context), ct)));
        owners.MapPost("/{eventId}/check-in", async (string id, string eventId, CheckInEventRequest request, HttpContext context, EventsStore store, CancellationToken ct) =>
            Results.Ok(await store.CheckInAsync(id, eventId, User(context), request, ct)));

        var guests = app.MapGroup("/api/v1/restaurants/{slug}/events").WithTags("Events")
            .RequireRateLimiting("restaurant-ordering").AddEndpointFilter<EventsFilter>();
        guests.MapGet("", async (string slug, EventsStore store, CancellationToken ct) => Results.Ok(await store.ListAsync(slug, false, null, ct)));
        guests.MapGet("/mine", async (string slug, HttpContext context, EventsStore store, CancellationToken ct) =>
            Results.Ok(await store.MineAsync(slug, User(context), ct))).RequireAuthorization();
        guests.MapPost("/{eventId}/rsvp", async (string slug, string eventId, SetEventRsvpRequest request, HttpContext context, EventsStore store, CancellationToken ct) =>
            Results.Ok(await store.RsvpAsync(slug, eventId, User(context), request, ct))).RequireAuthorization();
    }
    private static AuthUser User(HttpContext context) => (AuthUser)context.Items[RegisteredBearerHandler.UserItem]!;
}

public sealed class EventsFilter : IEndpointFilter
{
    public async ValueTask<object?> InvokeAsync(EndpointFilterInvocationContext context, EndpointFilterDelegate next)
    {
        try { return await next(context); }
        catch (EventFailure error) { return Results.Problem(statusCode: error.Status, title: error.Message, extensions: new Dictionary<string, object?> { ["code"] = error.Code }); }
        catch (Exception error) when (error is DbException or JsonException or InvalidOperationException or FormatException or OverflowException)
        { return Results.Problem(statusCode: 503, title: "Events are temporarily unavailable. Please try again.", extensions: new Dictionary<string, object?> { ["code"] = "events_unavailable" }); }
    }
}
