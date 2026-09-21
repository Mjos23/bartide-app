using TideCasa.Api.Infrastructure;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Data.Common;
using TideCasa.Api.Features.MerchantPayments;
using TideCasa.Contracts;

namespace TideCasa.Api.Features.RestaurantOrdering;

public sealed partial class RestaurantOrderingStore
{
    private async Task<bool> PhoneAvailableAsync(DbConnection db, DbTransaction tx, string tenant, CancellationToken ct)
    {
        if (!merchantOptions.CheckoutEnabled) return false;
        await using var command = Command(db, tx, db.Sql("SELECT COUNT(*) FROM tide_merchant_accounts WHERE tenant_id=@tenant AND environment='test' AND account_id IS NOT NULL AND state='ready' AND julianday(checked_at)>julianday('now','-5 minutes')",
            "SELECT COUNT(*) FROM tide_merchant_accounts WHERE tenant_id=@tenant AND environment='test' AND account_id IS NOT NULL AND state='ready' AND tide_iso_instant(checked_at)>statement_timestamp()-interval '5 minutes'"), ("@tenant", tenant));
        return Convert.ToInt64(await command.ExecuteScalarAsync(ct)) == 1;
    }
    public async Task<MerchantPhoneReservation> ReservePhoneAsync(string slug, RestaurantOrderRequest request, string address, MerchantAccountBinding merchant, CancellationToken ct)
    {
        merchantOptions.RequireCheckout();
        var placed = await PlaceCoreAsync(slug, request, address, merchant, ct);
        await using var db = await database.OpenAsync(ct); using var tx = db.BeginTransaction(deferred: true);
        await using var cmd = Command(db, tx, "SELECT id FROM tide_restaurant_payment_attempts WHERE order_id=@order AND tenant_id=@tenant AND environment='test' AND account_id=@account", ("@order", placed.Receipt.OrderId), ("@tenant", merchant.TenantId), ("@account", merchant.AccountId));
        var id = await cmd.ExecuteScalarAsync(ct) as string;
        return id is null ? throw new MerchantFailure("This order needs payment review.", 409, "merchant_review") : new(id, placed.Receipt);
    }
    private static async Task EnforcePhoneReservationAsync(DbConnection db, DbTransaction tx, MerchantAccountBinding merchant, string address, CancellationToken ct)
    {
        await using var check = Command(db, tx, db.Sql("SELECT COUNT(*) FROM tide_merchant_accounts WHERE tenant_id=@tenant AND environment='test' AND account_id=@account AND state='ready' AND julianday(checked_at)>julianday('now','-5 minutes')",
            "SELECT COUNT(*) FROM tide_merchant_accounts WHERE tenant_id=@tenant AND environment='test' AND account_id=@account AND state='ready' AND tide_iso_instant(checked_at)>statement_timestamp()-interval '5 minutes'"), ("@tenant", merchant.TenantId), ("@account", merchant.AccountId));
        if (Convert.ToInt64(await check.ExecuteScalarAsync(ct)) != 1) throw new OrderingException("Phone payments are unavailable.", 409, "phone_unavailable");
        await using var pending = Command(db, tx, "SELECT COUNT(*) FROM tide_restaurant_payment_attempts WHERE tenant_id=@tenant AND client_hash=@client AND state IN ('reserved','creating','open','review')", ("@tenant", merchant.TenantId), ("@client", Hash(address)));
        if (Convert.ToInt64(await pending.ExecuteScalarAsync(ct)) >= 3) throw new OrderingException("Finish or cancel your earlier card checkouts before starting another.", 429, "checkout_limit");
    }
    private async Task InsertPhoneAttemptAsync(DbConnection db, DbTransaction tx, MerchantAccountBinding merchant, RestaurantOrderReceipt receipt, string requestHash, string address, CancellationToken ct)
    {
        var id = Guid.NewGuid().ToString("D"); var quote = receipt.Quote;
        var lines = quote.Lines.Select(line => new MerchantCheckoutLine(line.Name, line.UnitCents, line.Quantity)).ToList();
        if (quote.TaxCents > 0) lines.Add(new("Restaurant sales tax", quote.TaxCents, 1));
        if (quote.DeliveryFeeCents > 0) lines.Add(new("Delivery fee", quote.DeliveryFeeCents, 1));
        if (quote.TipCents > 0) lines.Add(new("Optional tip", quote.TipCents, 1));
        var destination = merchantOptions.PublicBase + "/order/" + Uri.EscapeDataString(merchant.Slug) + "?payment_return=" + receipt.OrderId;
        var expiry = DateTimeOffset.UtcNow.AddHours(1);
        var parameters = new MerchantCheckoutParameters(merchant.TenantId, merchant.Slug, receipt.OrderId, id, requestHash, merchant.AccountId,
            quote.TotalCents, destination, destination, expiry.ToUnixTimeSeconds(), lines);
        await using var insert = Command(db, tx, "INSERT INTO tide_restaurant_payment_attempts(id,tenant_id,order_id,environment,account_id,request_hash,amount_cents,currency,provider_request_json,state,expires_at,client_hash,created_at,updated_at) VALUES(@id,@tenant,@order,'test',@account,@hash,@amount,'usd',@json,'reserved',@expiry,@client,@now,@now)",
            ("@id", id), ("@tenant", merchant.TenantId), ("@order", receipt.OrderId), ("@account", merchant.AccountId), ("@hash", requestHash), ("@amount", quote.TotalCents), ("@json", JsonSerializer.Serialize(parameters, MerchantPaymentsStore.Json)), ("@expiry", expiry.ToString("O")), ("@client", Hash(address)), ("@now", Stamp()));
        await insert.ExecuteNonQueryAsync(ct);
    }
    internal static async Task ApplyPhonePaymentAsync(DbConnection db, DbTransaction tx, string tenant, string orderId, int expectedAmount, string paymentState, CancellationToken ct)
    {
        await using var read = Command(db, tx, "SELECT payload_json FROM bartide_enhanced_orders WHERE id=@id AND tenant_id=@tenant", ("@id", orderId), ("@tenant", tenant));
        var raw = await read.ExecuteScalarAsync(ct) as string ?? throw new MerchantFailure("The payment order is unavailable.", 409, "merchant_review");
        var payload = Parse(raw); var receipt = Receipt(payload);
        if (receipt.Quote.PaymentMethod != "phone" || receipt.Quote.TotalCents != expectedAmount) throw new MerchantFailure("The payment order needs review.", 409, "merchant_review");
        var status = receipt.Status;
        if (paymentState == "paid" && status == "awaiting_payment")
        {
            await using var active = Command(db, tx, "SELECT COUNT(*) FROM bartide_customers WHERE id=@tenant AND status='active' AND vertical='bartide'", ("@tenant", tenant));
            status = Convert.ToInt64(await active.ExecuteScalarAsync(ct)) == 1 ? "new" : "paid_needs_review";
        }
        else if (paymentState == "expired" && status == "awaiting_payment") status = "cancelled";
        // Financial review never rewinds or reopens fulfillment. In particular,
        // a refund on a completed delivery must not consume delivery capacity,
        // and a failed pending refund must leave the previous kitchen step intact.
        // The management action policy separately blocks unpaid/review/refund
        // states and permits only an owner to close a fully refunded active order.
        var effective = paymentState switch { "expired" => "expired", "open" or "reserved" or "creating" => "pending", _ => paymentState };
        // Never let an old unpaid/expired provider observation revoke a captured payment.
        if (receipt.PaymentStatus is "paid" or "refunded" or "partially_refunded" or "refund_pending" && effective is "pending" or "expired") return;
        if (receipt.PaymentStatus == effective && receipt.Status == status) return;
        var now = Stamp(); payload["status"] = status; payload["payment_status"] = effective; payload["updated_at"] = now;
        payload["version"] = Integer(payload, "version", 0, int.MaxValue - 1, 0) + 1;
        var history = payload["history"] as JsonArray;
        if (history is null) { history = new(); payload["history"] = history; }
        history.Add(new JsonObject { ["status"] = status, ["payment_status"] = effective, ["action"] = "stripe_verified", ["at"] = now });
        await using var save = Command(db, tx, "UPDATE bartide_enhanced_orders SET status=@status,payload_json=@json,version=version+1,updated_at=@now WHERE id=@id AND tenant_id=@tenant", ("@status", status), ("@json", payload.ToJsonString(Json)), ("@now", now), ("@id", orderId), ("@tenant", tenant));
        await save.ExecuteNonQueryAsync(ct);
    }
}
