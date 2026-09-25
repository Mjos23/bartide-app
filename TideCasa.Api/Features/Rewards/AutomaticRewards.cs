using System.Data.Common;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using TideCasa.Api.Infrastructure;
using TideCasa.Contracts;

namespace TideCasa.Api.Features.Rewards;

public sealed partial class RewardsStore
{
    // A private receipt plus a fresh authenticated identity binds an order once.
    // Names, phone numbers and client-supplied payment claims are never identities.
    public async Task<RewardChangeResult> LinkOrderAsync(string slug, AuthUser user, RestaurantTrackingRequest request, CancellationToken ct)
    {
        if (request is null || !Guid.TryParseExact(request.OrderId, "D", out _) || request.TrackingKey is not { Length: 64 }
            || request.TrackingKey.Any(c => !char.IsAsciiHexDigit(c))) throw Missing();
        await using var db = await database.OpenAsync(ct);
        using var tx = db.BeginTransaction(deferred: false);
        var business = await Tenant(db, tx, slug, true, ct);
        var hash = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(request.TrackingKey)));
        var rows = await Rows(db, tx, "SELECT payload_json,version FROM bartide_enhanced_orders WHERE tenant_id=@t AND id=@id AND tracking_hash=@hash",
            r => (Payload: r.GetString(0), Version: r.ReadInt64(1)), ct, ("@t", business.Id), ("@id", request.OrderId), ("@hash", hash));
        if (rows.Count != 1) throw Missing();
        var payload = JsonNode.Parse(rows[0].Payload)!.AsObject();
        var owner = payload["customer_user_id"]?.GetValue<string>();
        if (!string.IsNullOrEmpty(owner) && owner != user.UserId)
            throw new RewardsException("This order is already connected to another account.", 409, "order_already_linked");
        if (string.IsNullOrEmpty(owner))
        {
            payload["customer_user_id"] = user.UserId;
            payload["version"] = rows[0].Version + 1;
            if (await Run(db, tx, "UPDATE bartide_enhanced_orders SET payload_json=@json,version=version+1 WHERE tenant_id=@t AND id=@id AND version=@version", ct,
                ("@json", payload.ToJsonString()), ("@t", business.Id), ("@id", request.OrderId), ("@version", rows[0].Version)) != 1) throw Stale();
        }
        var member = await OwnMember(db, tx, business.Id, user, ct);
        if (member is not null) await Reconcile(db, tx, business.Id, member, ct);
        await tx.CommitAsync(ct);
        return new(request.OrderId, "Eligible paid purchases appear in your rewards automatically.");
    }

    private async Task ImportPaidOrders(DbConnection db, DbTransaction tx, string tenant, string member, CancellationToken ct)
    {
        var members = await Rows(db, tx, "SELECT user_id,joined_at FROM tide_loyalty_members WHERE tenant_id=@t AND id=@m",
            r => (User: r.GetString(0), Joined: DateTimeOffset.Parse(r.GetString(1), CultureInfo.InvariantCulture)), ct, ("@t", tenant), ("@m", member));
        if (members.Count != 1) return;
        var customer = members[0];
        var owner = db.Sql("json_extract(payload_json,'$.customer_user_id')", "tide_json(payload_json)->>'customer_user_id'");
        var orders = await Rows(db, tx, $"SELECT id,payload_json,created_at,status FROM bartide_enhanced_orders WHERE tenant_id=@t AND {owner}=@u ORDER BY created_at,id",
            r => (Id: r.GetString(0), Payload: r.GetString(1), Created: DateTimeOffset.Parse(r.GetString(2), CultureInfo.InvariantCulture), Status: r.GetString(3)), ct, ("@t", tenant), ("@u", customer.User));
        var versions = await Rows(db, tx, "SELECT r.id,v.id,v.kind,v.item_id,v.timezone_id,v.active,v.created_at,v.version FROM tide_reward_rules r JOIN tide_reward_rule_versions v ON v.rule_id=r.id WHERE r.tenant_id=@t ORDER BY v.version DESC",
            r => (Rule: r.GetString(0), Version: r.GetString(1), Kind: r.GetString(2), Item: r.IsDBNull(3) ? null : r.GetString(3), Zone: r.GetString(4), Active: r.ReadInt32(5) == 1, Created: DateTimeOffset.Parse(r.GetString(6), CultureInfo.InvariantCulture)), ct, ("@t", tenant));
        foreach (var order in orders)
        {
            if (order.Created < customer.Joined || order.Created > clock.GetUtcNow()) continue;
            using var json = JsonDocument.Parse(order.Payload);
            var payload = json.RootElement;
            if (!payload.TryGetProperty("lines", out var lines) || lines.ValueKind != JsonValueKind.Array) continue;
            var paid = order.Status is not ("cancelled" or "canceled" or "payment_review" or "paid_needs_review")
                && payload.TryGetProperty("payment_status", out var payment) && payment.GetString() is "paid" or "paid_in_person";
            var foodCents = lines.EnumerateArray().Sum(line => checked((long)Math.Max(0, line.GetProperty("unit_cents").GetInt32()) * line.GetProperty("quantity").GetInt32()));
            await ReconcileOrderPoints(db, tx, tenant, member, order.Id, paid ? checked((int)(foodCents / 100)) : 0, ct);
            if (!paid) continue;
            // Select the version in force when the customer ordered; edits never backdate eligibility.
            foreach (var group in versions.GroupBy(v => v.Rule))
            {
                var rule = group.FirstOrDefault(v => v.Created <= order.Created);
                if (rule.Version is null || !rule.Active || rule.Kind == "points") continue;
                var visit = rule.Kind == "monthly-visits";
                if (visit && (!payload.TryGetProperty("fulfillment", out var fulfillment) || fulfillment.GetString() != "dine-in")) continue;
                var units = 0;
                foreach (var line in lines.EnumerateArray())
                    if (line.GetProperty("unit_cents").GetInt32() > 0 && (visit || line.GetProperty("item_id").GetString() == rule.Item))
                        units = checked(units + line.GetProperty("quantity").GetInt32());
                if (units <= 0) continue;
                if (visit) units = 1;
                var local = Local(order.Created, rule.Zone);
                var day = visit ? local.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) : null;
                var period = visit ? local.ToString("yyyy-MM", CultureInfo.InvariantCulture) : "lifetime";
                var key = "order:" + order.Id + ":" + (visit ? "visit" : rule.Item);
                // A later paid order on the same local day can support that day's visit after a refund.
                if (visit)
                    await Run(db, tx, "UPDATE tide_reward_qualifications SET source_reference=@ref WHERE tenant_id=@t AND rule_id=@r AND version_id=@v AND member_id=@m AND local_day=@day AND actor='automatic-order' AND state='eligible'", ct,
                        ("@ref", order.Id), ("@t", tenant), ("@r", rule.Rule), ("@v", rule.Version), ("@m", member), ("@day", day));
                await Run(db, tx, "INSERT INTO tide_reward_qualifications(id,tenant_id,rule_id,version_id,member_id,source_key,source_kind,source_reference,units,item_id,local_day,period,state,note,actor,occurred_at) VALUES(@id,@t,@r,@v,@m,@key,'paid-order',@ref,@units,@item,@day,@period,'eligible',@note,'automatic-order',@at) ON CONFLICT DO NOTHING", ct,
                    ("@id", Id()), ("@t", tenant), ("@r", rule.Rule), ("@v", rule.Version), ("@m", member), ("@key", key), ("@ref", order.Id), ("@units", units), ("@item", visit ? null : rule.Item), ("@day", day), ("@period", period),
                    ("@note", visit ? "Automatic visit from a paid dine-in order." : "Automatic purchase credit from a paid order."), ("@at", order.Created.ToString("O")));
            }
        }
        // Refunds can remove points already reserved for a reward. Release pending reservations
        // before staff can fulfill them; already fulfilled history remains and may leave a debt.
        if (await PointsBalance(db, tx, tenant, member, ct) < 0)
        {
            var pending = await Rows(db, tx, "SELECT id,points_cost FROM tide_reward_issued WHERE tenant_id=@t AND member_id=@m AND state='requested' AND points_cost>0 ORDER BY earned_at DESC,id", r => (Id: r.GetString(0), Cost: r.ReadInt32(1)), ct, ("@t", tenant), ("@m", member));
            foreach (var reward in pending)
            {
                if (await PointsBalance(db, tx, tenant, member, ct) >= 0) break;
                await Run(db, tx, "UPDATE tide_reward_issued SET state='cancelled',resolution_note='Automatic cancellation after an order refund or cancellation',updated_at=@now WHERE id=@id AND tenant_id=@t AND member_id=@m AND state='requested'", ct,
                    ("@now", Now()), ("@id", reward.Id), ("@t", tenant), ("@m", member));
                await CustomerLedger(db, tx, Id(), tenant, member, "dotnet-release:" + reward.Id, reward.Cost, "dotnet-release", "Refunded purchase no longer supports this reward", "automatic-order", ct);
            }
        }
    }

    private async Task ReconcileOrderPoints(DbConnection db, DbTransaction tx, string tenant, string member, string order, int desired, CancellationToken ct)
    {
        var prefix = "automatic-order:" + order + ":";
        var entries = await Rows(db, tx, "SELECT delta FROM tide_loyalty_ledger WHERE tenant_id=@t AND member_id=@m AND kind='automatic-order-points' AND event_key LIKE @prefix", r => r.ReadInt64(0), ct,
            ("@t", tenant), ("@m", member), ("@prefix", prefix + "%"));
        var delta = checked(desired - (int)entries.Sum());
        if (delta == 0) return;
        await CustomerLedger(db, tx, Id(), tenant, member, prefix + entries.Count.ToString(CultureInfo.InvariantCulture), delta, "automatic-order-points",
            delta > 0 ? "1 point per whole $1 of paid food and drinks" : "Points reversed after order refund or cancellation", "automatic-order", ct);
    }
}
