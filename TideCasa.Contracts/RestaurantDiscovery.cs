namespace TideCasa.Contracts;

public sealed record RestaurantDirectoryLocation(bool Listed, string Address, double? Latitude, double? Longitude);
public sealed record SaveRestaurantDirectoryRequest(int ExpectedVersion, RestaurantDirectoryLocation Location);
public sealed record NearbyRestaurantRequest(double? Latitude, double? Longitude, string? Search = null, int Offset = 0);
public sealed record NearbyRestaurant(string Slug, string Name, string Address, string Tagline, double DistanceMiles,
    bool AcceptingOrders, bool PickupEnabled, bool DeliveryEnabled);
public sealed record NearbyRestaurantResults(IReadOnlyList<NearbyRestaurant> Restaurants, bool HasMore);
