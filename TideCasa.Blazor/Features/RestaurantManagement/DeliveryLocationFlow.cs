using System.Globalization;
using Microsoft.AspNetCore.Antiforgery;
using TideCasa.Blazor.Features.Authentication;
using TideCasa.Blazor.Services;
using TideCasa.Contracts;
namespace TideCasa.Blazor.Features.RestaurantManagement;

public static partial class RestaurantManagementFlow
{
    public static void MapDeliveryLocationFlow(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapGet("/restaurant-management/{tenantId}/delivery-location",async(string tenantId,HttpContext c,RestaurantManagementClient api)=>
        {
            c.Response.Headers.CacheControl="no-store";
            if(!Identifier().IsMatch(tenantId)||c.Items[AuthFlow.TokenItem] is not string token)return Results.Unauthorized();
            return LocationResponse(await api.LocationsAsync(tenantId,token,c.RequestAborted));
        }).RequireAuthorization();
        endpoints.MapPost("/restaurant-management/{tenantId}/delivery-location/{orderId}/{operation}",async(string tenantId,string orderId,string operation,HttpContext c,IAntiforgery af,RestaurantManagementClient api)=>
        {
            if(!Identifier().IsMatch(tenantId)||!(orderId=="settings" || Guid.TryParseExact(orderId,"D",out _)))return Results.NotFound();
            var read=await ReadAsync(c,af);if(read.Failure is {} fail)return fail;
            var f=read.Form!;var ct=c.RequestAborted;var token=read.Token!;
            try
            {
                if(orderId=="settings"&&operation=="save")return LocationResponse(await api.LocationSettingsAsync(tenantId,new(Version(f),Checkbox(f,"enabled")),token,ct));
                if(operation=="start")return LocationResponse(await api.LocationStartAsync(tenantId,orderId,new(Checkbox(f,"consent")),token,ct));
                if(operation=="stop")return LocationResponse(await api.LocationStopAsync(tenantId,orderId,new(Text(f,"session",64,true)),token,ct));
                if(operation=="point")
                {
                    if(!long.TryParse(f["sequence"],NumberStyles.None,CultureInfo.InvariantCulture,out var seq))throw new FormFailure("invalid");
                    DeliveryLocationPoint? point=null;
                    if(f.ContainsKey("latitude"))
                    {
                        double N(string key)=>double.TryParse(f[key],NumberStyles.Float,CultureInfo.InvariantCulture,out var v)&&double.IsFinite(v)?v:throw new FormFailure("invalid");
                        point=new(N("latitude"),N("longitude"),N("accuracyMeters"),Text(f,"capturedAt",40,true));
                    }
                    return LocationResponse(await api.LocationPointAsync(tenantId,orderId,new(Text(f,"session",64,true),seq,point),token,ct));
                }
                return Results.NotFound();
            }
            catch(FormFailure){return Results.BadRequest(new {code="invalid"});}
        }).RequireAuthorization();
    }
    private static IResult LocationResponse<T>(RestaurantApiResult<T> result)=>result.Succeeded?Results.Json(result.Value):Results.Json(new {code=result.Code??"unavailable"},statusCode:(int)result.Status);
}
