using System.Data.Common;
using TideCasa.Api.Infrastructure;

namespace TideCasa.Api.Features.BusinessPosts;

public sealed partial class BusinessPostsStore
{
    private static string EligibleDelivery(DbConnection db) => db.Sql(
        "EXISTS(SELECT 1 FROM tide_business_posts p JOIN bartide_customers c ON c.id=p.tenant_id JOIN bartide_enhanced_configs x ON x.tenant_id=c.id JOIN tide_push_subscriptions s ON s.id=tide_push_outbox.subscription_id WHERE p.id=tide_push_outbox.post_id AND p.state='published' AND c.status='active' AND c.vertical IN('bartide','tide-casa') AND CASE WHEN json_valid(x.settings_json) THEN COALESCE(json_type(x.settings_json,'$.enabled'),'')='true' ELSE 0 END AND s.active=1 AND s.tenant_id=p.tenant_id AND s.generation=tide_push_outbox.subscription_generation)",
        "EXISTS(SELECT 1 FROM tide_business_posts p JOIN bartide_customers c ON c.id=p.tenant_id JOIN bartide_enhanced_configs x ON x.tenant_id=c.id JOIN tide_push_subscriptions s ON s.id=tide_push_outbox.subscription_id WHERE p.id=tide_push_outbox.post_id AND p.state='published' AND c.status='active' AND c.vertical IN('bartide','tide-casa') AND COALESCE(tide_json(x.settings_json)->'enabled'='true'::jsonb,false) AND s.active=1 AND s.tenant_id=p.tenant_id AND s.generation=tide_push_outbox.subscription_generation)");

    internal async Task CancelIneligibleDeliveries(CancellationToken ct)
    {
        await using var db = await database.OpenAsync(ct); using var tx = db.BeginTransaction(deferred: false);
        await Run(db, tx, db.Sql("UPDATE tide_push_outbox SET state='cancelled',lease_key=NULL,lease_until=NULL,updated_at=@now WHERE state IN('pending','sending') AND (@disabled=1 OR NOT " + EligibleDelivery(db) + " OR julianday(created_at)<julianday('now','-1 day'))",
            "UPDATE tide_push_outbox SET state='cancelled',lease_key=NULL,lease_until=NULL,updated_at=@now WHERE state IN('pending','sending') AND (@disabled=1 OR NOT " + EligibleDelivery(db) + " OR tide_iso_instant(created_at)<statement_timestamp()-interval '1 day')"), ct,
            ("@now", Now()), ("@disabled", push.Ready ? 0 : 1));
        await Run(db, tx, db.Sql("UPDATE tide_push_outbox SET state='failed',lease_key=NULL,lease_until=NULL,updated_at=@now WHERE attempts>=5 AND (state='pending' OR (state='sending' AND julianday(lease_until)<=julianday('now')))",
            "UPDATE tide_push_outbox SET state='failed',lease_key=NULL,lease_until=NULL,updated_at=@now WHERE attempts>=5 AND (state='pending' OR (state='sending' AND tide_iso_instant(lease_until)<=statement_timestamp()))"), ct, ("@now", Now()));
        tx.Commit();
    }

    internal async Task<PushDelivery?> ClaimDelivery(CancellationToken ct)
    {
        if (!push.Ready) return null;
        await using var db = await database.OpenAsync(ct); using var tx = db.BeginTransaction(deferred: false);
        var candidates = await Rows(db, tx, db.Sql("SELECT post_id,subscription_id,attempts FROM tide_push_outbox WHERE attempts<5 AND ((state='pending' AND julianday(next_attempt_at)<=julianday('now')) OR (state='sending' AND julianday(lease_until)<=julianday('now'))) AND " + EligibleDelivery(db) + " ORDER BY next_attempt_at,post_id,subscription_id LIMIT 1",
            "SELECT post_id,subscription_id,attempts FROM tide_push_outbox WHERE attempts<5 AND ((state='pending' AND tide_iso_instant(next_attempt_at)<=statement_timestamp()) OR (state='sending' AND tide_iso_instant(lease_until)<=statement_timestamp())) AND " + EligibleDelivery(db) + " ORDER BY next_attempt_at,post_id,subscription_id LIMIT 1"), r => (Post: r.GetString(0), Subscription: r.GetString(1), Attempts: r.ReadInt32(2)), ct);
        if (candidates.Count == 0) { tx.Commit(); return null; }
        var item = candidates[0]; var lease = Guid.NewGuid().ToString("D");
        await Run(db, tx, "UPDATE tide_push_outbox SET state='sending',attempts=attempts+1,lease_key=@lease,lease_until=@until,updated_at=@now WHERE post_id=@post AND subscription_id=@subscription", ct,
            ("@lease", lease), ("@until", DateTimeOffset.UtcNow.AddSeconds(60).ToString("O")), ("@now", Now()), ("@post", item.Post), ("@subscription", item.Subscription));
        var deliveries = await Rows(db, tx, "SELECT s.generation,s.endpoint,s.p256dh,s.auth,c.slug,p.title,p.body FROM tide_push_subscriptions s JOIN tide_business_posts p ON p.id=@post JOIN bartide_customers c ON c.id=p.tenant_id WHERE s.id=@subscription", r => new PushDelivery(item.Post, item.Subscription, r.ReadInt32(0), lease, item.Attempts + 1, r.GetString(1), r.GetString(2), r.GetString(3), r.GetString(4), r.GetString(5), r.GetString(6)), ct, ("@post", item.Post), ("@subscription", item.Subscription));
        tx.Commit(); return deliveries.Single();
    }

    internal async Task<bool> DeliveryStillEligible(PushDelivery delivery, CancellationToken ct)
    {
        await using var db = await database.OpenAsync(ct); using var tx = db.BeginTransaction(deferred: true);
        var valid = push.Ready && await Count(db, tx, db.Sql("SELECT COUNT(*) FROM tide_push_outbox WHERE post_id=@post AND subscription_id=@subscription AND lease_key=@lease AND state='sending' AND julianday(lease_until)>julianday('now') AND " + EligibleDelivery(db),
            "SELECT COUNT(*) FROM tide_push_outbox WHERE post_id=@post AND subscription_id=@subscription AND lease_key=@lease AND state='sending' AND tide_iso_instant(lease_until)>statement_timestamp() AND " + EligibleDelivery(db)), ct,
            ("@post", delivery.PostId), ("@subscription", delivery.SubscriptionId), ("@lease", delivery.LeaseKey)) == 1;
        tx.Commit(); return valid;
    }

    internal async Task CompleteDelivery(PushDelivery delivery, PushDeliveryResult result, CancellationToken ct)
    {
        await using var db = await database.OpenAsync(ct); using var tx = db.BeginTransaction(deferred: false);
        var retry = result.Status is 408 or 429 || result.Status >= 500;
        var state = result.Status is >= 200 and < 300 ? "sent" : retry && delivery.Attempts < 5 ? "pending" : "failed";
        var delay = Math.Max(result.RetryAfterSeconds ?? 0, 30 * (int)Math.Pow(2, delivery.Attempts - 1));
        var changed = await Run(db, tx, "UPDATE tide_push_outbox SET state=@state,last_status=@status,next_attempt_at=@next,lease_key=NULL,lease_until=NULL,updated_at=@now WHERE post_id=@post AND subscription_id=@subscription AND state='sending' AND lease_key=@lease", ct,
            ("@state", state), ("@status", result.Status), ("@next", DateTimeOffset.UtcNow.AddSeconds(delay).ToString("O")), ("@now", Now()), ("@post", delivery.PostId), ("@subscription", delivery.SubscriptionId), ("@lease", delivery.LeaseKey));
        if (changed == 1 && result.Status is 404 or 410)
        {
            await Run(db, tx, "UPDATE tide_push_subscriptions SET active=0,generation=generation+1,updated_at=@now WHERE id=@id AND generation=@generation", ct,
                ("@id", delivery.SubscriptionId), ("@generation", delivery.Generation), ("@now", Now()));
            await CancelSubscription(db, tx, delivery.SubscriptionId, ct);
        }
        tx.Commit();
    }
}
