namespace TideCasa.Contracts;

public sealed record RestaurantEvent(string Id, string Title, string Details, string Location,
    string StartsAt, string EndsAt, string TimeZone, int Capacity, int Reserved, string State, int Version);
public sealed record RestaurantEvents(string TenantId, string Slug, string Name, IReadOnlyList<RestaurantEvent> Events);
public sealed record EventGuest(string UserId, string DisplayName, string Email, string State, int Version, string UpdatedAt);
public sealed record EventGuestList(RestaurantEvent Event, IReadOnlyList<EventGuest> Guests);
public sealed record MyEventRsvp(string State, int Version, string EventState);
public sealed record MyEventReservation(RestaurantEvent Event, MyEventRsvp Rsvp);
public sealed record MyEventReservations(IReadOnlyList<MyEventReservation> Reservations);
public sealed record SaveEventRequest(string RequestKey, int ExpectedVersion, string Title, string Details, string Location,
    string StartsAt, string EndsAt, string TimeZone, int Capacity, bool Published);
public sealed record CancelEventRequest(string RequestKey, int ExpectedVersion);
public sealed record SetEventRsvpRequest(string RequestKey, int ExpectedVersion, bool Attending);
public sealed record CheckInEventRequest(string RequestKey, string UserId, int ExpectedVersion);
