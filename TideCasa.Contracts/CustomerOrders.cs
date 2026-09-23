namespace TideCasa.Contracts;

public sealed record CustomerOrders(string RestaurantName, string Slug, IReadOnlyList<RestaurantOrderReceipt> Orders);
