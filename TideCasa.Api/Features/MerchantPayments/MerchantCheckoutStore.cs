using TideCasa.Api.Infrastructure;
using System.Globalization;
using System.Text.Json;
using System.Data.Common;
using TideCasa.Api.Features.RestaurantOrdering;
using TideCasa.Contracts;

namespace TideCasa.Api.Features.MerchantPayments;

public sealed partial class MerchantPaymentsStore
{
    private sealed record Attempt(string Id, string Tenant, string OrderId, string Account, string RequestHash,
        int Amount, MerchantCheckoutParameters Parameters, string State, string? Session, string? Expires,
        string Created, string? LeaseUntil, int Refunded, int PendingRefund);
    private const string AttemptColumns = "id,tenant_id,order_id,account_id,request_hash,amount_cents,provider_request_json,state,session_id,expires_at,created_at,lease_until,refunded_cents,pending_refund_cents";
    private static Attempt MapAttempt(DbDataReader r) => new(r.GetString(0), r.GetString(1), r.GetString(2), r.GetString(3), r.GetString(4), r.ReadInt32(5),
        JsonSerializer.Deserialize<MerchantCheckoutParameters>(r.GetString(6), Json) ?? throw new InvalidOperationException("Invalid payment snapshot."),
        r.GetString(7), NullString(r, 8), NullString(r, 9), r.GetString(10), NullString(r, 11), r.ReadInt32(12), r.ReadInt32(13));
    private static async Task<Attempt> AttemptById(DbConnection db, DbTransaction tx, string id, CancellationToken ct) =>
        (await Rows(db, tx, "SELECT " + AttemptColumns + " FROM tide_restaurant_payment_attempts WHERE id=@id AND environment='test'", MapAttempt, ct, ("@id", id))).SingleOrDefault()
        ?? throw new MerchantFailure("This payment attempt is unavailable.", 404, "merchant_missing");

    public async Task<RestaurantPhoneCheckout> CheckoutAsync(string slug, RestaurantOrderRequest request, string address, CancellationToken ct)
    {
        var merchant = await ReadyAsync(slug, ct);
        var reservation = await ordering.ReservePhoneAsync(slug, request, address, merchant, ct);
        var snapshot = await ReconcileAsync(reservation.AttemptId, false, null, ct);
        return await ResultAsync(slug, new(reservation.Receipt.OrderId, request.TrackingKey), snapshot?.Url, ct);
    }
    public async Task<RestaurantPhoneCheckout> TrackPaymentAsync(string slug, RestaurantTrackingRequest request, bool cancel, CancellationToken ct)
    {
        // Verify the private receipt capability before making any provider call.
        var receipt = await ordering.TrackAsync(slug, request, ct);
        if (receipt.Quote.PaymentMethod != "phone") throw new MerchantFailure("This order uses payment to staff.", 409, "merchant_invalid");
        string id;
        await using (var db = await database.OpenAsync(ct))
        {
            using var tx = db.BeginTransaction(deferred: true);
            id = (await Rows(db, tx, "SELECT id FROM tide_restaurant_payment_attempts WHERE order_id=@order AND environment='test'", r => r.GetString(0), ct, ("@order", receipt.OrderId))).SingleOrDefault()
                ?? throw new MerchantFailure("This payment attempt is unavailable.", 404, "merchant_missing"); tx.Commit();
        }
        if (!options.Configured) return await ResultAsync(slug, request, null, ct);
        var snapshot = await ReconcileAsync(id, cancel, null, ct);
        return await ResultAsync(slug, request, options.CheckoutEnabled ? snapshot?.Url : null, ct);
    }
    private async Task<RestaurantPhoneCheckout> ResultAsync(string slug, RestaurantTrackingRequest request, string? url, CancellationToken ct)
    {
        var receipt = await ordering.TrackAsync(slug, request, ct);
        await using var db = await database.OpenAsync(ct); using var tx = db.BeginTransaction(deferred: true);
        var attempt = (await Rows(db, tx, "SELECT " + AttemptColumns + " FROM tide_restaurant_payment_attempts WHERE order_id=@order AND environment='test'", MapAttempt, ct, ("@order", receipt.OrderId))).Single();
        tx.Commit(); return new(receipt, attempt.State, attempt.State == "open" ? url : null, attempt.Expires, attempt.Refunded, attempt.PendingRefund);
    }
    private async Task<MerchantPaymentSnapshot?> ReconcileAsync(string attemptId, bool expire, InboxKey? inbox, CancellationToken ct, string? notifiedSession = null)
    {
        var lease = Guid.NewGuid().ToString("D"); Attempt attempt;
        await using (var db = await database.OpenAsync(ct))
        {
            using var tx = db.BeginTransaction(deferred: false); attempt = await AttemptById(db, tx, attemptId, ct);
            if (attempt.LeaseUntil is { } until && DateTimeOffset.Parse(until, CultureInfo.InvariantCulture) > DateTimeOffset.UtcNow) { tx.Commit(); return null; }
            if (attempt.Session is null && notifiedSession is null && DateTimeOffset.Parse(attempt.Created, CultureInfo.InvariantCulture) < DateTimeOffset.UtcNow.AddHours(-23))
            {
                await Run(db, tx, "UPDATE tide_restaurant_payment_attempts SET state='review',updated_at=@now WHERE id=@id", ct, ("@now", Now()), ("@id", attemptId));
                await RestaurantOrderingStore.ApplyPhonePaymentAsync(db, tx, attempt.Tenant, attempt.OrderId, attempt.Amount, "review", ct); tx.Commit();
                throw new MerchantFailure("This earlier checkout needs review before it can be retried.", 409, "merchant_review");
            }
            await Run(db, tx, "UPDATE tide_restaurant_payment_attempts SET lease_key=@lease,lease_until=@until,state=CASE WHEN session_id IS NULL THEN 'creating' ELSE state END,updated_at=@now WHERE id=@id", ct,
                ("@lease", lease), ("@until", DateTimeOffset.UtcNow.AddMinutes(2).ToString("O")), ("@now", Now()), ("@id", attemptId)); tx.Commit();
        }
        try
        {
            ValidateAttempt(attempt);
            var sessionId = attempt.Session ?? notifiedSession;
            MerchantPaymentSnapshot snapshot;
            if (sessionId is null) snapshot = await provider.CreateCheckoutAsync(attempt.Parameters, ct);
            else snapshot = expire ? await provider.ExpireAsync(attempt.Parameters, sessionId, ct) : await provider.CheckoutAsync(attempt.Parameters, sessionId, ct);
            if (expire && snapshot.State == "open") snapshot = await provider.ExpireAsync(attempt.Parameters, snapshot.SessionId, ct);
            await using var db = await database.OpenAsync(ct); using var tx = db.BeginTransaction(deferred: false);
            var current = await AttemptById(db, tx, attemptId, ct);
            await using var leaseCheck = Command(db, tx, "SELECT COUNT(*) FROM tide_restaurant_payment_attempts WHERE id=@id AND lease_key=@lease", ("@id", attemptId), ("@lease", lease));
            if (Convert.ToInt64(await leaseCheck.ExecuteScalarAsync(ct)) != 1) throw new MerchantFailure("This payment is already being checked. Try again shortly.", 409, "merchant_pending");
            if (current.Session is not null && current.Session != snapshot.SessionId) throw new MerchantFailure("This payment needs review.", 409, "merchant_review");
            foreach (var refund in snapshot.Refunds)
            {
                var prior = await Rows(db, tx, "SELECT amount_cents,charge_id,intent_id,attempt_id FROM tide_merchant_refunds WHERE environment='test' AND account_id=@account AND refund_id=@refund", r => (r.ReadInt64(0), r.GetString(1), r.GetString(2), r.GetString(3)), ct,
                    ("@account", attempt.Account), ("@refund", refund.Id));
                if (prior.Count > 0 && prior[0] != (refund.AmountCents, refund.ChargeId, refund.IntentId, attempt.Id)) throw new MerchantFailure("This refund needs review.", 409, "merchant_review");
                await Run(db, tx, "INSERT INTO tide_merchant_refunds(environment,account_id,refund_id,attempt_id,charge_id,intent_id,amount_cents,status,updated_at) VALUES('test',@account,@refund,@attempt,@charge,@intent,@amount,@state,@now) ON CONFLICT(environment,account_id,refund_id) DO UPDATE SET status=CASE WHEN tide_merchant_refunds.status='succeeded' THEN 'succeeded' ELSE excluded.status END,updated_at=excluded.updated_at", ct,
                    ("@account", attempt.Account), ("@refund", refund.Id), ("@attempt", attempt.Id), ("@charge", refund.ChargeId), ("@intent", refund.IntentId), ("@amount", refund.AmountCents), ("@state", refund.Status), ("@now", Now()));
            }
            var totals = (await Rows(db, tx, "SELECT COALESCE(SUM(CASE WHEN status='succeeded' THEN amount_cents ELSE 0 END),0),COALESCE(SUM(CASE WHEN status IN('pending','requires_action') THEN amount_cents ELSE 0 END),0) FROM tide_merchant_refunds WHERE attempt_id=@id AND account_id=@account AND environment='test'", r => (Paid: r.ReadInt32(0), Pending: r.ReadInt32(1)), ct, ("@id", attempt.Id), ("@account", attempt.Account))).Single();
            if (totals.Paid + totals.Pending > attempt.Amount) throw new MerchantFailure("Refund totals need review.", 409, "merchant_review");
            var state = snapshot.State;
            if (state == "paid") state = totals.Paid == attempt.Amount ? "refunded" : totals.Paid > 0 ? "partially_refunded" : totals.Pending > 0 ? "refund_pending" : "paid";
            if (current.State is "paid" or "partially_refunded" or "refunded" or "refund_pending" && state is "open" or "expired" or "review") state = current.State;
            await Run(db, tx, "UPDATE tide_restaurant_payment_attempts SET state=@state,session_id=@session,intent_id=COALESCE(@intent,intent_id),charge_id=COALESCE(@charge,charge_id),expires_at=@expires,refunded_cents=@refunded,pending_refund_cents=@pending,lease_key=NULL,lease_until=NULL,version=version+1,updated_at=@now WHERE id=@id AND lease_key=@lease", ct,
                ("@state", state), ("@session", snapshot.SessionId), ("@intent", snapshot.IntentId), ("@charge", snapshot.ChargeId), ("@expires", snapshot.ExpiresAt), ("@refunded", totals.Paid), ("@pending", totals.Pending), ("@now", Now()), ("@id", attempt.Id), ("@lease", lease));
            await RestaurantOrderingStore.ApplyPhonePaymentAsync(db, tx, attempt.Tenant, attempt.OrderId, attempt.Amount, state, ct);
            if (inbox is not null) await CompleteInbox(db, tx, inbox, ct);
            tx.Commit(); return snapshot with { State = state, Url = state == "open" ? snapshot.Url : null };
        }
        catch
        {
            await using var db = await database.OpenAsync(CancellationToken.None); using var tx = db.BeginTransaction(deferred: false);
            await Run(db, tx, "UPDATE tide_restaurant_payment_attempts SET lease_key=NULL,lease_until=NULL WHERE id=@id AND lease_key=@lease", CancellationToken.None, ("@id", attemptId), ("@lease", lease)); tx.Commit(); throw;
        }
    }
    private static void ValidateAttempt(Attempt attempt)
    {
        var p = attempt.Parameters;
        if (p.AttemptId != attempt.Id || p.TenantId != attempt.Tenant || p.OrderId != attempt.OrderId || p.AccountId != attempt.Account || p.RequestHash != attempt.RequestHash || p.AmountCents != attempt.Amount
            || p.Lines.Sum(line => checked(line.AmountCents * line.Quantity)) != attempt.Amount) throw new MerchantFailure("The saved payment request needs review.", 409, "merchant_review");
    }
}
