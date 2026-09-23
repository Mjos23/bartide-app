using System.Text.Json;
using TideCasa.Api.Features.Authentication;
using TideCasa.Api.Infrastructure;
using TideCasa.Contracts;

namespace TideCasa.Api.Features.RestaurantOrdering;

public sealed partial class RestaurantOrderingStore
{
    public async Task<CustomerOrders> CustomerOrdersAsync(string slug, AuthUser user, CancellationToken ct)
    {
        await using var db = await database.OpenAsync(ct);
        using var tx = db.BeginTransaction(deferred: true);
        var venue = await ReadVenueAsync(db, tx, slug, false, ct);
        // Bound both history and response size; the customer identity is always supplied by authentication.
        var owner = db.Sql("json_extract(payload_json,'$.customer_user_id')", "payload_json::jsonb->>'customer_user_id'");
        await using var query = Command(db, tx, $"SELECT payload_json FROM bartide_enhanced_orders WHERE tenant_id=@tenant AND {owner}=@user AND created_at>=@since ORDER BY created_at DESC LIMIT 30",
            ("@tenant", venue.Id), ("@user", user.UserId), ("@since", DateTimeOffset.UtcNow.AddDays(-90).ToString("O")));
        var orders = new List<RestaurantOrderReceipt>();
        await using var reader = await query.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct)) orders.Add(Receipt(Parse(reader.GetString(0))));
        return new(venue.Name, slug, orders);
    }

    public async Task<RestaurantOrderReceipt> SaveCustomerOrderAsync(string slug, RestaurantTrackingRequest request, AuthUser user, CancellationToken ct)
    {
        if (!Guid.TryParseExact(request.OrderId, "D", out _) || !ValidTrackingKey(request.TrackingKey))
            throw new OrderingException("Order not found.", 404, "not_found");
        await using var db = await database.OpenAsync(ct);
        using var tx = db.BeginTransaction(deferred: false);
        var venue = await ReadVenueAsync(db, tx, slug, false, ct);
        string? raw; long version;
        await using (var query = Command(db, tx, "SELECT payload_json,version FROM bartide_enhanced_orders WHERE tenant_id=@tenant AND id=@id AND tracking_hash=@key",
            ("@tenant", venue.Id), ("@id", request.OrderId), ("@key", Hash(request.TrackingKey))))
        {
            await using var reader = await query.ExecuteReaderAsync(ct);
            if (!await reader.ReadAsync(ct)) throw new OrderingException("Order not found.", 404, "not_found");
            raw = reader.GetString(0); version = Convert.ToInt64(reader.GetValue(1));
        }
        var payload = Parse(raw);
        var existing = String(payload, "customer_user_id");
        if (existing.Length > 0 && existing != user.UserId)
            throw new OrderingException("This receipt is already saved to another account.", 409, "already_saved");
        if (existing.Length == 0)
        {
            payload["customer_user_id"] = user.UserId;
            payload["version"] = version + 1;
            await using var update = Command(db, tx, "UPDATE bartide_enhanced_orders SET payload_json=@payload,version=version+1 WHERE tenant_id=@tenant AND id=@id AND version=@version",
                ("@payload", payload.ToJsonString(Json)), ("@tenant", venue.Id), ("@id", request.OrderId), ("@version", version));
            if (await update.ExecuteNonQueryAsync(ct) != 1) throw new OrderingException("Order changed. Try saving again.", 409, "stale_order");
        }
        await tx.CommitAsync(ct);
        return Receipt(payload);
    }
}
