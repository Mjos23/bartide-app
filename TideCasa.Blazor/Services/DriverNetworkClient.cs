using TideCasa.Contracts;

namespace TideCasa.Blazor.Services;

public sealed partial class RestaurantManagementClient
{
    public Task<RestaurantApiResult<DriverNetworkAccount>> GetDriverAccountAsync(string token, CancellationToken ct) =>
        SendAsync<DriverNetworkAccount>(HttpMethod.Get, "api/v1/drivers/me", null, token, ct);
    public Task<RestaurantApiResult<DriverNetworkAccount>> SaveNetworkDriverAsync(SaveNetworkDriverProfile body, string token, CancellationToken ct) =>
        SendAsync<DriverNetworkAccount>(HttpMethod.Post, "api/v1/drivers/me", body, token, ct);
    public Task<RestaurantApiResult<ClientDriverNetworkWorkspace>> GetDriverNetworkAsync(string tenant, string token, CancellationToken ct) =>
        SendAsync<ClientDriverNetworkWorkspace>(HttpMethod.Get, Path(tenant, "driver-network"), null, token, ct);
    public Task<RestaurantApiResult<ClientDriverNetworkWorkspace>> OfferDriverHireAsync(string tenant, OfferNetworkDriverHire body, string token, CancellationToken ct) =>
        SendAsync<ClientDriverNetworkWorkspace>(HttpMethod.Post, Path(tenant, "driver-network/offers"), body, token, ct);
    public Task<RestaurantApiResult<ClientDriverNetworkWorkspace>> EndDriverHireAsync(string tenant, string hire, EndNetworkDriverHire body, string token, CancellationToken ct) =>
        SendAsync<ClientDriverNetworkWorkspace>(HttpMethod.Post, Path(tenant, "driver-network/hires/" + Uri.EscapeDataString(hire) + "/end"), body, token, ct);
    public Task<RestaurantApiResult<DriverNetworkAccount>> RespondDriverHireAsync(string hire, RespondNetworkDriverHire body, string token, CancellationToken ct) =>
        SendAsync<DriverNetworkAccount>(HttpMethod.Post, "api/v1/drivers/hires/" + Uri.EscapeDataString(hire) + "/respond", body, token, ct);
    public Task<RestaurantApiResult<DriverEarningsWorkspace>> GetDriverEarningsAsync(string token, CancellationToken ct, int page = 0) =>
        SendAsync<DriverEarningsWorkspace>(HttpMethod.Get, "api/v1/drivers/payments?page=" + page.ToString(System.Globalization.CultureInfo.InvariantCulture), null, token, ct);
    public Task<RestaurantApiResult<DriverHostedLink>> StartDriverPayoutAsync(StartDriverPayoutSetup body, string token, CancellationToken ct) =>
        SendAsync<DriverHostedLink>(HttpMethod.Post, "api/v1/drivers/payout-setup", body, token, ct);
    public Task<RestaurantApiResult<ClientDriverPaymentsWorkspace>> GetDriverPaymentsAsync(string tenant, string token, CancellationToken ct, int page = 0) =>
        SendAsync<ClientDriverPaymentsWorkspace>(HttpMethod.Get, Path(tenant, "driver-payments") + "?page=" + page.ToString(System.Globalization.CultureInfo.InvariantCulture), null, token, ct);
    public Task<RestaurantApiResult<DriverPaymentCheckout>> ApproveDriverPaymentAsync(string tenant, string payment, ApproveDriverPayment body, string token, CancellationToken ct) =>
        SendAsync<DriverPaymentCheckout>(HttpMethod.Post, Path(tenant, "driver-payments/" + Uri.EscapeDataString(payment) + "/approve"), body, token, ct);
    public Task<RestaurantApiResult<DriverPaymentCheckout>> RefreshDriverPaymentAsync(string tenant, string payment, string token, CancellationToken ct) =>
        SendAsync<DriverPaymentCheckout>(HttpMethod.Post, Path(tenant, "driver-payments/" + Uri.EscapeDataString(payment) + "/refresh"), new { }, token, ct);
}
