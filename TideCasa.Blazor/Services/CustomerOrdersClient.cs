using TideCasa.Contracts;
namespace TideCasa.Blazor.Services;

public sealed partial class RestaurantOrderingClient
{
    public Task<RestaurantApiResult<CustomerOrders>> CustomerOrdersAsync(string slug, string token, CancellationToken ct) =>
        SendAsync<CustomerOrders>(HttpMethod.Get, RestaurantPath(slug) + "/my-orders", null, token, ct);
    public Task<RestaurantApiResult<RestaurantOrderReceipt>> SaveCustomerOrderAsync(string slug, RestaurantTrackingRequest request, string token, CancellationToken ct) =>
        SendAsync<RestaurantOrderReceipt>(HttpMethod.Post, RestaurantPath(slug) + "/my-orders", request, token, ct);
}
