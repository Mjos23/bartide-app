using System.Data.Common;
using System.Text.Json;
using TideCasa.Api.Features.RestaurantOrdering;
using TideCasa.Api.Infrastructure;
using TideCasa.Contracts;

namespace TideCasa.Api.Features.DriverPayments;

public sealed partial class DriverPaymentsStore
{
    private sealed record Payable(string Id,string Tenant,string Member,string User,string Source,int AgreedPay);
    private sealed record PayState(string Status,int Version,string? Attempt,int Amount);
    private sealed record Attempt(string Id,string Payment,string Request,string? Session,string Status,string Created,string? LeaseUntil);
    private static Task<List<Payable>> Payables(DbConnection db,DbTransaction tx,string tenant,string payment,CancellationToken ct)=>
        Rows(db,tx,"SELECT id,tenant_id,member_id,user_id,source,agreed_pay_cents FROM tide_driver_payables WHERE id=@id AND tenant_id=@tenant",
            r=>new Payable(r.GetString(0),r.GetString(1),r.GetString(2),r.GetString(3),r.GetString(4),r.ReadInt32(5)),ct,("@id",payment),("@tenant",tenant));
    private async Task<PayState> State(DbConnection db,DbTransaction tx,string payment,CancellationToken ct)=>
        (await Rows(db,tx,"SELECT status,version,current_attempt_id,driver_pay_cents FROM tide_driver_payment_states WHERE payment_id=@id AND environment=@env",
            r=>new PayState(r.GetString(0),r.ReadInt32(1),r.IsDBNull(2)?null:r.GetString(2),r.ReadInt32(3)),ct,("@id",payment),("@env",options.Environment))).SingleOrDefault()??new("pending_approval",0,null,0);
    public async Task<DriverPaymentCheckout> ApproveAsync(string tenant,string paymentId,AuthUser user,ApproveDriverPayment request,CancellationToken ct)
    {
        options.RequirePayments();
        if(request is null||!request.ConfirmPayment||request.DriverPayCents is <50 or >100000||request.ExpectedVersion<0)throw new OrderingException("Review the driver pay and confirm the payment total.");
        ValidatePage(request.ReturnPage);
        Payable payable;string account;string? existing;
        await using(var db=await database.OpenAsync(ct))
        {
            using var tx=db.BeginTransaction(deferred:false);
            if(!(await Access(db,tx,tenant,user,ct)).Owner)throw new OrderingException("Only the client owner can approve driver payments.",403,"forbidden");
            payable=(await Payables(db,tx,tenant,paymentId,ct)).SingleOrDefault()??throw new OrderingException("Completed delivery not found.",404,"not_found");
            if(payable.User.Length==0)
            {
                var linked=(await Rows(db,tx,"SELECT user_id FROM bartide_enhanced_members WHERE tenant_id=@tenant AND id=@member AND role='driver' AND user_id IS NOT NULL",r=>r.GetString(0),ct,("@tenant",tenant),("@member",payable.Member))).SingleOrDefault();
                if(string.IsNullOrEmpty(linked))throw new OrderingException("The driver must connect their sign-in and payment account first.",409,"driver_payment_setup");
                payable=payable with{User=linked};
                await Run(db,tx,"UPDATE tide_driver_payables SET user_id=@user WHERE id=@id AND user_id=''",ct,("@user",linked),("@id",paymentId));
            }
            var state=await State(db,tx,paymentId,ct);ValidateApproval(payable,state,request);
            existing=state.Status is "creating" or "open"?state.Attempt:null;
            account=(await Rows(db,tx,"SELECT account_id FROM tide_driver_payout_accounts WHERE user_id=@user AND environment=@env AND account_id IS NOT NULL",r=>r.GetString(0),ct,("@user",payable.User),("@env",options.Environment))).SingleOrDefault()
                ??throw new OrderingException("The driver must finish payment setup before you can pay them.",409,"driver_payment_setup");
            await tx.CommitAsync(ct);
        }
        if(existing is not null)
        {await ReconcileAttemptAsync(existing,ct);return await CheckoutView(tenant,paymentId,user,ct);}
        var readiness=await provider.RecipientAsync(account,payable.User,ct);
        if(!readiness.Ready)throw new OrderingException("This driver's Stripe transfers and payouts are not ready.",409,"driver_payment_setup");
        string attempt;
        await using(var db=await database.OpenAsync(ct))
        {
            using var tx=db.BeginTransaction(deferred:false);
            if(!(await Access(db,tx,tenant,user,ct)).Owner)throw new OrderingException("Owner approval required.",403,"forbidden");
            var state=await State(db,tx,paymentId,ct);ValidateApproval(payable,state,request);
            if(state.Status is "creating" or "open")throw Stale();
            attempt=Guid.NewGuid().ToString("D");var fee=Fee(request.DriverPayCents,payable.Source=="network");var now=Now();
            var parameters=new DriverChargeRequest(paymentId,attempt,tenant,payable.User,account,request.DriverPayCents,fee,request.DriverPayCents+fee,
                "/workspace/"+Uri.EscapeDataString(tenant)+"/driver-payments",DateTimeOffset.UtcNow.AddHours(1).ToUnixTimeSeconds(),request.ReturnPage);
            await Run(db,tx,"""
                INSERT INTO tide_driver_payment_attempts(id,payment_id,environment,account_id,user_id,request_json,status,created_at,updated_at)
                VALUES(@id,@payment,@env,@account,@user,@json,'creating',@now,@now)
                """,ct,("@id",attempt),("@payment",paymentId),("@env",options.Environment),("@account",account),("@user",payable.User),("@json",JsonSerializer.Serialize(parameters,Json)),("@now",now));
            await Run(db,tx,"""
                INSERT INTO tide_driver_payment_states(payment_id,environment,driver_pay_cents,fee_cents,total_cents,status,version,current_attempt_id,approved_by,approved_at,updated_at)
                VALUES(@payment,@env,@amount,@fee,@total,'creating',1,@attempt,@actor,@now,@now)
                ON CONFLICT(payment_id,environment) DO UPDATE SET status='creating',version=tide_driver_payment_states.version+1,
                    current_attempt_id=@attempt,updated_at=@now,approved_by=@actor,approved_at=@now
                """,ct,("@payment",paymentId),("@env",options.Environment),("@amount",request.DriverPayCents),("@fee",fee),("@total",parameters.TotalCents),("@attempt",attempt),("@actor",user.UserId),("@now",now));
            await tx.CommitAsync(ct);
        }
        await ReconcileAttemptAsync(attempt,ct);return await CheckoutView(tenant,paymentId,user,ct);
    }
    private static void ValidateApproval(Payable payable,PayState state,ApproveDriverPayment request)
    {
        if(state.Version!=request.ExpectedVersion)throw Stale();
        if(payable.Source=="network"&&request.DriverPayCents!=payable.AgreedPay || state.Amount>0&&request.DriverPayCents!=state.Amount)
            throw new OrderingException("The agreed driver pay cannot be changed on this payment.",409,"payment_amount");
        if(state.Status is not ("pending_approval" or "expired" or "creating" or "open"))throw Review();
    }
    public async Task<DriverPaymentCheckout> RefreshAsync(string tenant,string paymentId,AuthUser user,CancellationToken ct)
    {
        string? attempt;
        await using(var db=await database.OpenAsync(ct))
        {
            using var tx=db.BeginTransaction(deferred:false);await Access(db,tx,tenant,user,ct);
            if((await Payables(db,tx,tenant,paymentId,ct)).Count!=1)throw new OrderingException("Payment not found.",404,"not_found");
            attempt=(await State(db,tx,paymentId,ct)).Attempt;await tx.CommitAsync(ct);
        }
        if(attempt is not null&&options.PaymentsEnabled)await ReconcileAttemptAsync(attempt,ct);
        return await CheckoutView(tenant,paymentId,user,ct);
    }
    private async Task<DriverPaymentCheckout> CheckoutView(string tenant,string paymentId,AuthUser user,CancellationToken ct)
    {
        await using var db=await database.OpenAsync(ct);using var tx=db.BeginTransaction(deferred:false);
        var access=await Access(db,tx,tenant,user,ct);
        var line=(await Lines(db,tx,tenant,null,ct,paymentId)).SingleOrDefault()??throw new OrderingException("Payment not found.",404,"not_found");
        if(!access.Owner)line=line with{CheckoutUrl=null};
        await tx.CommitAsync(ct);return new(line,line.CheckoutUrl);
    }
    internal async Task ReconcileAttemptAsync(string attemptId,CancellationToken ct)
    {
        options.RequirePayments();Attempt attempt;var lease=Guid.NewGuid().ToString("D");
        await using(var db=await database.OpenAsync(ct))
        {
            using var tx=db.BeginTransaction(deferred:false);
            attempt=(await Rows(db,tx,"SELECT id,payment_id,request_json,session_id,status,created_at,lease_until FROM tide_driver_payment_attempts WHERE id=@id AND environment=@env",
                r=>new Attempt(r.GetString(0),r.GetString(1),r.GetString(2),r.IsDBNull(3)?null:r.GetString(3),r.GetString(4),r.GetString(5),r.IsDBNull(6)?null:r.GetString(6)),ct,("@id",attemptId),("@env",options.Environment))).SingleOrDefault()??throw Review();
            if(DateTimeOffset.TryParse(attempt.LeaseUntil,out var until)&&until>DateTimeOffset.UtcNow)return;
            if(attempt.Session is null && (attempt.Status=="review" || DateTimeOffset.Parse(attempt.Created)<DateTimeOffset.UtcNow.AddHours(-23)))
            {await SaveAttemptState(db,tx,attempt,"review",null,null,null,ct);await tx.CommitAsync(ct);return;}
            await Run(db,tx,"UPDATE tide_driver_payment_attempts SET lease_key=@lease,lease_until=@until WHERE id=@id AND environment=@env",ct,
                ("@lease",lease),("@until",DateTimeOffset.UtcNow.AddMinutes(2).ToString("O")),("@id",attemptId),("@env",options.Environment));await tx.CommitAsync(ct);
        }
        try
        {
            var request=JsonSerializer.Deserialize<DriverChargeRequest>(attempt.Request,Json)!;
            var result=attempt.Session is null?await provider.CreateCheckoutAsync(request,ct):await provider.CheckoutAsync(request,attempt.Session,ct);
            await using var db=await database.OpenAsync(ct);using var tx=db.BeginTransaction(deferred:false);
            await SaveAttemptState(db,tx,attempt,result.State,result.SessionId,result.Url,lease,ct);await tx.CommitAsync(ct);
        }
        catch(OrderingException error) when(error.Code=="payment_review")
        {
            await using var db=await database.OpenAsync(CancellationToken.None);using var tx=db.BeginTransaction(deferred:false);
            await SaveAttemptState(db,tx,attempt,"review",attempt.Session,null,lease,CancellationToken.None);await tx.CommitAsync(CancellationToken.None);throw;
        }
        finally
        {
            await using var db=await database.OpenAsync(CancellationToken.None);using var tx=db.BeginTransaction(deferred:false);
            // Rotate failed reads too, so unavailable provider objects cannot starve the recovery queue.
            await Run(db,tx,"UPDATE tide_driver_payment_attempts SET lease_key=NULL,lease_until=NULL,updated_at=@now WHERE id=@id AND environment=@env AND lease_key=@lease",CancellationToken.None,
                ("@id",attemptId),("@env",options.Environment),("@lease",lease),("@now",Now()));await tx.CommitAsync(CancellationToken.None);
        }
    }
    private async Task SaveAttemptState(DbConnection db,DbTransaction tx,Attempt attempt,string state,string? session,string? url,string? lease,CancellationToken ct)
    {
        if(state is not ("creating" or "open" or "paid" or "review" or "refunded" or "disputed" or "expired"))throw Review();
        // A terminal successful payment cannot be reopened by a stale Checkout response.
        if(attempt.Status is "paid" or "refunded" or "disputed" && state is "creating" or "open" or "expired")throw Review();
        var changed=await Run(db,tx,"""
            UPDATE tide_driver_payment_attempts SET status=@state,session_id=COALESCE(@session,session_id),checkout_url=@url,updated_at=@now,lease_key=NULL,lease_until=NULL
            WHERE id=@id AND environment=@env AND (@lease IS NULL OR lease_key=@lease)
            """,ct,("@state",state),("@session",session),("@url",state=="open"?url:null),("@now",Now()),("@id",attempt.Id),("@env",options.Environment),("@lease",lease));
        if(changed!=1)return;
        await Run(db,tx,"""
            UPDATE tide_driver_payment_states SET status=@state,version=version+CASE WHEN status<>@state THEN 1 ELSE 0 END,updated_at=@now
            WHERE payment_id=@payment AND environment=@env AND current_attempt_id=@attempt
            """,ct,("@state",state),("@now",Now()),("@payment",attempt.Payment),("@env",options.Environment),("@attempt",attempt.Id));
    }
    internal async Task<List<string>> RecoverableAttempts(CancellationToken ct)
    {
        await using var db=await database.OpenAsync(ct);using var tx=db.BeginTransaction(deferred:true);
        return await Rows(db,tx,"SELECT id FROM tide_driver_payment_attempts WHERE environment=@env AND status IN ('creating','open','paid','disputed','review') ORDER BY updated_at,id LIMIT 50",r=>r.GetString(0),ct,("@env",options.Environment));
    }
    internal async Task ReconcileKnownNotificationAsync(string attemptId,CancellationToken ct)
    {
        bool known;
        await using(var db=await database.OpenAsync(ct))
        {
            using var tx=db.BeginTransaction(deferred:true);
            known=(await Rows(db,tx,"SELECT id FROM tide_driver_payment_attempts WHERE id=@id AND environment=@env",r=>r.GetString(0),ct,("@id",attemptId),("@env",options.Environment))).Count==1;
        }
        if(known)await ReconcileAttemptAsync(attemptId,ct);
    }
}
