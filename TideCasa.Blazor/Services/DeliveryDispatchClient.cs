using TideCasa.Contracts;

namespace TideCasa.Blazor.Services;

public sealed partial class RestaurantManagementClient
{
    public Task<RestaurantApiResult<DeliveryDispatchWorkspace>> GetDispatchAsync(string tenant, string token, CancellationToken ct) =>
        SendAsync<DeliveryDispatchWorkspace>(HttpMethod.Get, Path(tenant, "delivery-dispatch"), null, token, ct);
    public Task<RestaurantApiResult<DeliveryDispatchWorkspace>> SaveDispatchSettingsAsync(string tenant, SaveDeliveryDispatchSettings body, string token, CancellationToken ct) =>
        SendAsync<DeliveryDispatchWorkspace>(HttpMethod.Post, Path(tenant, "delivery-dispatch/settings"), body, token, ct);
    public Task<RestaurantApiResult<DeliveryDispatchWorkspace>> SaveDriverProfileAsync(string tenant, string driver, SaveDeliveryDriverProfile body, string token, CancellationToken ct) =>
        SendAsync<DeliveryDispatchWorkspace>(HttpMethod.Post, Path(tenant, "delivery-dispatch/drivers/" + Uri.EscapeDataString(driver)), body, token, ct);
    public Task<RestaurantApiResult<RestaurantOperationsWorkspace>> SaveAssignmentPlanAsync(string tenant, string order, SaveDeliveryAssignmentPlan body, string token, CancellationToken ct) =>
        SendAsync<RestaurantOperationsWorkspace>(HttpMethod.Post, Path(tenant, "delivery-dispatch/orders/" + Uri.EscapeDataString(order) + "/plan"), body, token, ct);
    public Task<RestaurantApiResult<DeliveryDispatchResult>> RunDispatchAsync(string tenant, string token, CancellationToken ct) =>
        SendAsync<DeliveryDispatchResult>(HttpMethod.Post, Path(tenant, "delivery-dispatch/run"), new { }, token, ct);
}
