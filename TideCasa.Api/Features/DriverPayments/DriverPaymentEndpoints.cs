using System.Data.Common;
using System.Text.Json;
using TideCasa.Api.Features.Authentication;
using TideCasa.Api.Features.PublicDemo;
using TideCasa.Api.Features.RestaurantOrdering;
using TideCasa.Api.Infrastructure;
using TideCasa.Api.Infrastructure.Payments;
using TideCasa.Contracts;

namespace TideCasa.Api.Features.DriverPayments;

public static class DriverPaymentEndpoints
{
    public static void MapDriverPayments(this WebApplication app)
    {
        var drivers=app.MapGroup("/api/v1/drivers").RequireAuthorization().WithMetadata(new ApiBodyLimit(16*1024)).RequireRateLimiting("public-form").AddEndpointFilter<DriverPaymentFilter>();
        drivers.MapGet("/payments",async(int? page,HttpContext c,DriverPaymentsStore s,CancellationToken ct)=>Results.Ok(await s.EarningsAsync(User(c),ct,page??0)));
        drivers.MapPost("/payout-setup",async(StartDriverPayoutSetup r,HttpContext c,DriverPaymentsStore s,CancellationToken ct)=>Results.Ok(await s.StartPayoutSetupAsync(User(c),r,ct)));
        var clients=app.MapGroup("/api/v1/tenants/{id}/driver-payments").RequireAuthorization().WithMetadata(new ApiBodyLimit(16*1024)).RequireRateLimiting("public-form").AddEndpointFilter<DriverPaymentFilter>();
        clients.MapGet("",async(string id,int? page,HttpContext c,DriverPaymentsStore s,CancellationToken ct)=>Results.Ok(await s.ClientWorkspaceAsync(id,User(c),ct,page??0)));
        clients.MapPost("/{paymentId}/approve",async(string id,string paymentId,ApproveDriverPayment r,HttpContext c,DriverPaymentsStore s,CancellationToken ct)=>Results.Ok(await s.ApproveAsync(id,paymentId,User(c),r,ct)));
        clients.MapPost("/{paymentId}/refresh",async(string id,string paymentId,HttpContext c,DriverPaymentsStore s,CancellationToken ct)=>Results.Ok(await s.RefreshAsync(id,paymentId,User(c),ct)));
        app.MapPost("/api/stripe/driver-payments/webhook",Webhook).WithMetadata(new ApiBodyLimit(256*1024));
    }
    private static AuthUser User(HttpContext c)=>(AuthUser)c.Items[RegisteredBearerHandler.UserItem]!;
    private static async Task<IResult> Webhook(HttpContext c,DriverPaymentOptions options,DriverPaymentsStore store,CancellationToken ct)
    {
        if(!options.PaymentsEnabled)return Results.StatusCode(503);
        if(c.Request.Headers["Stripe-Signature"].Count!=1)return Results.BadRequest();
        try
        {
            using var bytes=new MemoryStream();var buffer=new byte[16384];
            for(;;){var read=await c.Request.Body.ReadAsync(buffer,ct);if(read==0)break;if(bytes.Length+read>256*1024)return Results.StatusCode(413);bytes.Write(buffer,0,read);}
            StripeWebhookSignature.Verify(bytes.ToArray(),c.Request.Headers["Stripe-Signature"].ToString(),options.WebhookSecret);
            using var json=JsonDocument.Parse(bytes.ToArray());StripeJson.RejectDuplicateProperties(json.RootElement);
            var root=json.RootElement;var obj=StripeJson.P(StripeJson.P(root,"data"),"object");var metadata=StripeJson.P(obj,"metadata");
            if(!StripeJson.Id(StripeJson.S(root,"id"),"evt")||StripeJson.B(root,"livemode")!=!options.Sandbox||!StripeJson.Empty(root,"account"))return Results.BadRequest();
            // Events only trigger retrieval of a known, approved attempt. They never supply payment amounts or destinations.
            if(StripeJson.S(metadata,"purpose")=="tide_driver_delivery" && Guid.TryParseExact(StripeJson.S(metadata,"tide_attempt_id"),"D",out var attempt))
                await store.ReconcileKnownNotificationAsync(attempt.ToString("D"),ct);
            return Results.Accepted();
        }
        catch(Exception e)when(e is StripeSignatureException or JsonException or BadHttpRequestException){return Results.BadRequest();}
        catch(Exception e)when(e is StripeTransportException or DbException or OrderingException or InvalidOperationException){return Results.StatusCode(503);}
    }
}
public sealed class DriverPaymentFilter:IEndpointFilter
{
    public async ValueTask<object?> InvokeAsync(EndpointFilterInvocationContext c,EndpointFilterDelegate next)
    {
        try{return await next(c);}
        catch(OrderingException e){return Results.Problem(statusCode:e.Status,title:e.Message,extensions:new Dictionary<string,object?>{{"code",e.Code}});}
        catch(Exception e)when(e is StripeTransportException or DbException or JsonException or InvalidOperationException or OperationCanceledException or FormatException or OverflowException)
        {return Results.Problem(statusCode:503,title:"Payment could not be confirmed. Refresh the saved payment before trying again.",extensions:new Dictionary<string,object?>{{"code","payment_unavailable"}});}
    }
}
public sealed class DriverPaymentRecovery(IServiceScopeFactory scopes,DriverPaymentOptions options,PublicDemoOptions demo,
    IConfiguration config,IHostEnvironment environment,ILogger<DriverPaymentRecovery> logger):BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        if(demo.Enabled||!options.PaymentsEnabled||environment.IsDevelopment()&&!config.GetValue("DriverPayments:WorkerEnabled",true))return;
        using var timer=new PeriodicTimer(TimeSpan.FromSeconds(60));
        while(!ct.IsCancellationRequested)
        {
            try
            {
                using var scope=scopes.CreateScope();var store=scope.ServiceProvider.GetRequiredService<DriverPaymentsStore>();
                foreach(var id in await store.RecoverableAttempts(ct))
                {
                    try{await store.ReconcileAttemptAsync(id,ct);}
                    catch(Exception e)when(e is not OperationCanceledException){logger.LogWarning("A driver payment could not be reconciled; its saved attempt will be reviewed again.");}
                }
            }
            catch(OperationCanceledException)when(ct.IsCancellationRequested){break;}
            catch(Exception){logger.LogWarning("Driver payment recovery is temporarily unavailable.");}
            try{if(!await timer.WaitForNextTickAsync(ct))break;}catch(OperationCanceledException)when(ct.IsCancellationRequested){break;}
        }
    }
}
