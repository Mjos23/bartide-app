using System.Text.Json;
using System.Text.Json.Nodes;
using TideCasa.Api.Infrastructure;
using TideCasa.Contracts;

namespace TideCasa.Api.Features.RestaurantOrdering;

public sealed partial class RestaurantOrderingStore
{
    public async Task<RestaurantMenuEditor> SaveDirectoryAsync(string id, AuthUser user, SaveRestaurantDirectoryRequest request, CancellationToken ct)
    {
        if (request?.Location is not { } location) throw new OrderingException("Enter the restaurant location.");
        var address = Text(location.Address, "Street address", 300, !location.Listed);
        if ((location.Latitude is null) != (location.Longitude is null)
            || (location.Listed || location.Latitude is not null) && !ValidCoordinates(location.Latitude, location.Longitude))
            throw new OrderingException("Set a valid restaurant location before listing it.", 400, "invalid_location");
        await ChangeMenuAsync(id, user, request.ExpectedVersion, menu =>
        {
            ((JsonObject)menu["venue"]!)["directory"] = JsonSerializer.SerializeToNode(location with { Address = address }, Json);
        }, ct);
        return await EditorAsync(id, user, ct);
    }

    public async Task<NearbyRestaurantResults> NearbyAsync(NearbyRestaurantRequest request, CancellationToken ct)
    {
        if (request is null || !ValidCoordinates(request.Latitude, request.Longitude))
            throw new OrderingException("Allow location access to find nearby restaurants.", 400, "invalid_location");
        if (request.Offset is < 0 or > 100000) throw new OrderingException("Choose a valid results page.");
        var search = Text(request.Search ?? "", "Restaurant name", 80, true);
        await using var db = await database.OpenAsync(ct);
        using var tx = db.BeginTransaction(deferred: true);
        var candidates = new List<(string Id, RestaurantDirectoryLocation Location, string Tagline)>();
        await using (var query = Command(db, tx, "SELECT id,name,menu_json FROM bartide_customers WHERE vertical='bartide' AND status='active'"))
        {
            await using var reader = await query.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
            {
                if (!reader.GetString(1).Contains(search, StringComparison.OrdinalIgnoreCase)) continue;
                try
                {
                    var profile = Parse(reader.GetString(2))["venue"] as JsonObject;
                    var location = DirectoryLocation(profile);
                    if (location is not { Listed: true } || string.IsNullOrWhiteSpace(location.Address)
                        || !ValidCoordinates(location.Latitude, location.Longitude)) continue;
                    candidates.Add((reader.GetString(0), location, profile is null ? "" : String(profile, "tagline")));
                }
                catch (Exception e) when (e is JsonException or InvalidOperationException or FormatException) { /* A malformed listing cannot hide other restaurants. */ }
            }
        }
        var results = new List<NearbyRestaurant>();
        foreach (var candidate in candidates)
        {
            try
            {
                // Use the same menu/config validation as direct guest ordering.
                var venue = await ReadVenueAsync(db, tx, candidate.Id, true, ct);
                if (!Boolean(venue.Config, "enabled", false)) continue;
                var options = Options(venue);
                if (!options.PickupEnabled && !options.DeliveryEnabled) continue;
                results.Add(new(venue.Slug, venue.Name, candidate.Location.Address, candidate.Tagline,
                    Miles(request.Latitude!.Value, request.Longitude!.Value, candidate.Location.Latitude!.Value, candidate.Location.Longitude!.Value),
                    options.AcceptingOrders, options.PickupEnabled, options.DeliveryEnabled));
            }
            catch (Exception e) when (e is OrderingException or JsonException or InvalidOperationException or FormatException or OverflowException) { }
        }
        await tx.CommitAsync(ct);
        // Never round before sorting. The slug only stabilizes exactly equal distances.
        var page = results.OrderBy(r => r.DistanceMiles).ThenBy(r => r.Slug, StringComparer.Ordinal).Skip(request.Offset).Take(51).ToArray();
        return new(page.Take(50).ToArray(), page.Length > 50);
    }

    private static RestaurantDirectoryLocation? DirectoryLocation(JsonObject? profile)
    {
        try { return profile?["directory"]?.Deserialize<RestaurantDirectoryLocation>(Json); }
        catch (Exception e) when (e is JsonException or InvalidOperationException) { return null; }
    }
    private static bool ValidCoordinates(double? latitude, double? longitude) => latitude is { } lat && longitude is { } lon
        && double.IsFinite(lat) && double.IsFinite(lon) && lat is >= -90 and <= 90 && lon is >= -180 and <= 180;
    private static double Miles(double lat, double lon, double otherLat, double otherLon)
    {
        const double radians = Math.PI / 180;
        var a = Math.Pow(Math.Sin((otherLat - lat) * radians / 2), 2)
            + Math.Cos(lat * radians) * Math.Cos(otherLat * radians) * Math.Pow(Math.Sin((otherLon - lon) * radians / 2), 2);
        return 3958.7613 * 2 * Math.Asin(Math.Sqrt(Math.Clamp(a, 0, 1)));
    }
}
