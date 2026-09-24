using TideCasa.Contracts;
namespace TideCasa.Blazor.Services;

public sealed partial class RestaurantManagementClient
{
    public Task<RestaurantApiResult<DeliveryLocationBoard>> LocationsAsync(string id,string token,CancellationToken ct)=>SendAsync<DeliveryLocationBoard>(HttpMethod.Get,Path(id,"delivery-location"),null,token,ct);
    public Task<RestaurantApiResult<DeliveryLocationBoard>> LocationSettingsAsync(string id,DeliveryLocationSettings body,string token,CancellationToken ct)=>SendAsync<DeliveryLocationBoard>(HttpMethod.Post,Path(id,"delivery-location/settings"),body,token,ct);
    public Task<RestaurantApiResult<DeliveryLocationSession>> LocationStartAsync(string id,string order,DeliveryLocationStart body,string token,CancellationToken ct)=>SendAsync<DeliveryLocationSession>(HttpMethod.Post,Path(id,"delivery-location/"+Uri.EscapeDataString(order)+"/start"),body,token,ct);
    public Task<RestaurantApiResult<DeliveryLocationView>> LocationPointAsync(string id,string order,DeliveryLocationUpdate body,string token,CancellationToken ct)=>SendAsync<DeliveryLocationView>(HttpMethod.Post,Path(id,"delivery-location/"+Uri.EscapeDataString(order)+"/point"),body,token,ct);
    public Task<RestaurantApiResult<System.Text.Json.JsonElement>> LocationStopAsync(string id,string order,DeliveryLocationStop body,string token,CancellationToken ct)=>SendAsync<System.Text.Json.JsonElement>(HttpMethod.Post,Path(id,"delivery-location/"+Uri.EscapeDataString(order)+"/stop"),body,token,ct);
}
public sealed record CustomerDeliveryLocation(DeliveryLocationView? Location);
public sealed partial class RestaurantOrderingClient
{
    public Task<RestaurantApiResult<CustomerDeliveryLocation>> LocationAsync(string slug,RestaurantTrackingRequest request,CancellationToken ct=default)=>SendAsync<CustomerDeliveryLocation>(HttpMethod.Post,RestaurantPath(slug)+"/delivery-location",request,null,ct);
}
