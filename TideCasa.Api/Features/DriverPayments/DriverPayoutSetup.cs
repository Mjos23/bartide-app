using System.Text.Json;
using TideCasa.Api.Features.RestaurantOrdering;
using TideCasa.Api.Infrastructure;
using TideCasa.Api.Infrastructure.Payments;
using TideCasa.Contracts;

namespace TideCasa.Api.Features.DriverPayments;

public sealed partial class DriverPaymentsStore
{
    private sealed record PayoutBinding(string? Account,string Key,string Request,string State,string Created,string? LeaseUntil);
    private async Task<PayoutBinding?> Binding(string user,CancellationToken ct)
    {
        await using var db=await database.OpenAsync(ct);using var tx=db.BeginTransaction(deferred:true);
        return (await Rows(db,tx,"SELECT account_id,create_key,create_json,state,created_at,lease_until FROM tide_driver_payout_accounts WHERE user_id=@user AND environment=@env",
            r=>new PayoutBinding(r.IsDBNull(0)?null:r.GetString(0),r.GetString(1),r.GetString(2),r.GetString(3),r.GetString(4),r.IsDBNull(5)?null:r.GetString(5)),ct,("@user",user),("@env",options.Environment))).SingleOrDefault();
    }
    private async Task<DriverPayoutStatus> PayoutStatusAsync(AuthUser user,CancellationToken ct)
    {
        DriverPayoutStatus Status(string state,string message)=>new(state,options.SetupEnabled,options.PaymentsEnabled,options.Sandbox,message);
        if(!options.SetupEnabled)return Status("disabled","Driver payment setup is not enabled yet. Completed work remains recorded for your client to review.");
        var binding=await Binding(user.UserId,ct);
        if(binding?.Account is null)return Status(binding?.State??"unconnected","Connect your own Stripe account to receive payments. Your restaurant must approve each completed delivery.");
        try
        {
            var recipient=await provider.RecipientAsync(binding.Account,user.UserId,ct);
            return Status(recipient.Ready?"ready":"onboarding",recipient.Ready
                ? "Your payment account is ready. Payments credit your Stripe balance; bank payout timing is managed by Stripe."
                : "Continue Stripe setup to enable transfers and bank payouts.");
        }
        catch(Exception e) when(e is StripeTransportException or OrderingException)
        {return Status("unavailable","Your saved payment connection could not be verified. Refresh before requesting payment.");}
    }
    public async Task<DriverHostedLink> StartPayoutSetupAsync(AuthUser user,StartDriverPayoutSetup request,CancellationToken ct)
    {
        options.RequireSetup();
        if(request is null || !request.ConfirmUsIndividual || !Guid.TryParseExact(request.RequestKey,"D",out _))
            throw new OrderingException("Confirm your US individual account details before connecting payments.");
        string? account;string key,body;var lease=Guid.NewGuid().ToString("D");
        await using(var db=await database.OpenAsync(ct))
        {
            using var tx=db.BeginTransaction(deferred:false);
            var rows=await Rows(db,tx,"SELECT account_id,create_key,create_json,state,created_at,lease_until FROM tide_driver_payout_accounts WHERE user_id=@user AND environment=@env",
                r=>new PayoutBinding(r.IsDBNull(0)?null:r.GetString(0),r.GetString(1),r.GetString(2),r.GetString(3),r.GetString(4),r.IsDBNull(5)?null:r.GetString(5)),ct,("@user",user.UserId),("@env",options.Environment));
            var old=rows.SingleOrDefault();account=old?.Account;
            if(old is null)
            {
                key=request.RequestKey;body=JsonSerializer.Serialize(new DriverRecipientCreate(user.UserId,user.FullName??user.Email,user.Email),Json);
                await Run(db,tx,"""
                    INSERT INTO tide_driver_payout_accounts(user_id,environment,create_key,create_json,state,created_at,updated_at,lease_key,lease_until)
                    VALUES(@user,@env,@key,@json,'creating',@now,@now,@lease,@until)
                    """,ct,("@user",user.UserId),("@env",options.Environment),("@key",key),("@json",body),("@now",Now()),("@lease",lease),("@until",DateTimeOffset.UtcNow.AddMinutes(2).ToString("O")));
            }
            else
            {
                key=old.Key;body=old.Request;
                if(account is null)
                {
                    if(old.State=="review" || DateTimeOffset.Parse(old.Created)<DateTimeOffset.UtcNow.AddHours(-23))throw Review();
                    if(DateTimeOffset.TryParse(old.LeaseUntil,out var until)&&until>DateTimeOffset.UtcNow)throw new OrderingException("Payment setup is in progress. Try again shortly.",409,"payment_pending");
                    await Run(db,tx,"UPDATE tide_driver_payout_accounts SET lease_key=@lease,lease_until=@until WHERE user_id=@user AND environment=@env",ct,
                        ("@lease",lease),("@until",DateTimeOffset.UtcNow.AddMinutes(2).ToString("O")),("@user",user.UserId),("@env",options.Environment));
                }
            }
            await tx.CommitAsync(ct);
        }
        if(account is null)
        {
            try
            {
                var recipient=await provider.CreateRecipientAsync(JsonSerializer.Deserialize<DriverRecipientCreate>(body,Json)!,key,ct);
                await using var db=await database.OpenAsync(ct);using var tx=db.BeginTransaction(deferred:false);
                if(await Run(db,tx,"UPDATE tide_driver_payout_accounts SET account_id=@account,state=@state,updated_at=@now,lease_key=NULL,lease_until=NULL WHERE user_id=@user AND environment=@env AND account_id IS NULL AND lease_key=@lease",ct,
                    ("@account",recipient.AccountId),("@state",recipient.Ready?"ready":"onboarding"),("@now",Now()),("@user",user.UserId),("@env",options.Environment),("@lease",lease))!=1)throw Review();
                await tx.CommitAsync(ct);account=recipient.AccountId;
            }
            catch
            {
                await using var db=await database.OpenAsync(CancellationToken.None);using var tx=db.BeginTransaction(deferred:false);
                await Run(db,tx,"UPDATE tide_driver_payout_accounts SET lease_key=NULL,lease_until=NULL WHERE user_id=@user AND environment=@env AND lease_key=@lease",CancellationToken.None,
                    ("@user",user.UserId),("@env",options.Environment),("@lease",lease));await tx.CommitAsync(CancellationToken.None);throw;
            }
        }
        await provider.RecipientAsync(account,user.UserId,ct);
        return new(await provider.OnboardingLinkAsync(account,user.UserId,request.RequestKey,ct));
    }
}
