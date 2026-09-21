using TideCasa.Api.Infrastructure;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Data.Common;
using Stripe;
using Stripe.Events;

namespace TideCasa.Api.Features.MerchantPayments;

public sealed partial class MerchantPaymentsStore
{
    private sealed record InboxKey(string Context, string Account, string EventId);
    private sealed record NotificationReference(string ObjectId, string? IntentId = null, string? ChargeId = null, string? AttemptId = null);
    private static readonly HashSet<string> SnapshotTypes = ["checkout.session.completed", "checkout.session.expired", "checkout.session.async_payment_succeeded", "checkout.session.async_payment_failed", "payment_intent.succeeded", "payment_intent.payment_failed", "payment_intent.canceled", "charge.refunded", "refund.created", "refund.updated", "refund.failed"];

    public async Task AcceptNotificationAsync(string context, string body, string signature, CancellationToken ct)
    {
        if (!options.Configured) throw new MerchantFailure("Payment notifications are not configured.", 503, "merchant_disabled");
        string eventId, eventType, account; NotificationReference reference;
        if (context == "snapshot")
        {
            if (!MerchantPaymentOptions.ValidSecret(options.SnapshotSecret)) throw new MerchantFailure("Payment notifications are not configured.", 503, "merchant_disabled");
            var parsed = EventUtility.ConstructEvent(body, signature, options.SnapshotSecret, tolerance: 300, throwOnApiVersionMismatch: true);
            if (parsed.Livemode || !MerchantPaymentOptions.AccountId(parsed.Account)) throw new MerchantFailure("Wrong payment event context.", 400, "event_context");
            eventId = parsed.Id; eventType = parsed.Type; account = parsed.Account;
            if (!SnapshotTypes.Contains(eventType)) return;
            using var json = JsonDocument.Parse(body); var obj = json.RootElement.GetProperty("data").GetProperty("object");
            var objectId = Field(obj, "id") ?? throw new MerchantFailure("Invalid event reference.");
            reference = new(objectId,
                eventType.StartsWith("payment_intent.", StringComparison.Ordinal) ? objectId : Field(obj, "payment_intent"),
                eventType.StartsWith("charge.", StringComparison.Ordinal) ? objectId : Field(obj, "charge"),
                obj.TryGetProperty("metadata", out var metadata) ? Field(metadata, "tide_attempt_id") : null);
        }
        else
        {
            if (context != "thin" || !MerchantPaymentOptions.ValidSecret(options.ThinSecret)) throw new MerchantFailure("Account notifications are not configured.", 503, "merchant_disabled");
            var notification = provider.Client.ParseEventNotification(body, signature, options.ThinSecret, tolerance: 300);
            // Accounts v2 merchant objects belong to the platform. This endpoint
            // never follows an arbitrary organization or connected-account context.
            var scope = notification.Context?.ToString();
            if (notification.Livemode || !string.IsNullOrEmpty(scope) && scope != options.PlatformAccount) throw new MerchantFailure("Wrong account event context.", 400, "event_context");
            string? related = notification switch
            {
                V2CoreAccountCreatedEventNotification n => n.RelatedObject?.Id,
                V2CoreAccountClosedEventNotification n => n.RelatedObject?.Id,
                V2CoreAccountUpdatedEventNotification n => n.RelatedObject?.Id,
                V2CoreAccountIncludingConfigurationMerchantCapabilityStatusUpdatedEventNotification n => n.RelatedObject?.Id,
                V2CoreAccountIncludingConfigurationMerchantUpdatedEventNotification n => n.RelatedObject?.Id,
                V2CoreAccountIncludingDefaultsUpdatedEventNotification n => n.RelatedObject?.Id,
                V2CoreAccountIncludingRequirementsUpdatedEventNotification n => n.RelatedObject?.Id,
                _ => null
            };
            if (related is null) return;
            if (!MerchantPaymentOptions.AccountId(related)) throw new MerchantFailure("Invalid account event reference.");
            account = related; eventId = notification.Id; eventType = notification.Type; reference = new(account);
        }
        if (eventId is null || !Regex.IsMatch(eventId, "^[A-Za-z0-9_]{6,160}$")) throw new MerchantFailure("Invalid notification ID.");
        var key = new InboxKey(context, account, eventId); var savedReference = JsonSerializer.Serialize(reference, Json);
        await using (var db = await database.OpenAsync(ct))
        {
            using var tx = db.BeginTransaction(deferred: false);
            var known = await Rows(db, tx, "SELECT tenant_id FROM tide_merchant_accounts WHERE environment='test' AND account_id=@account", r => r.GetString(0), ct, ("@account", account));
            if (known.Count != 1) throw new MerchantFailure("Unknown merchant event context.", 400, "event_context");
            var prior = await Rows(db, tx, "SELECT event_type,object_id FROM tide_merchant_notification_inbox WHERE context=@context AND environment='test' AND account_id=@account AND event_id=@event", r => (r.GetString(0), r.GetString(1)), ct,
                ("@context", context), ("@account", account), ("@event", eventId));
            if (prior.Count > 0 && prior[0] != (eventType, savedReference)) throw new MerchantFailure("Notification replay differs from its original.", 400, "event_conflict");
            await Run(db, tx, "INSERT INTO tide_merchant_notification_inbox(context,environment,account_id,event_id,event_type,object_id,state,created_at,updated_at) VALUES(@context,'test',@account,@event,@type,@object,'received',@now,@now) ON CONFLICT DO NOTHING", ct,
                ("@context", context), ("@account", account), ("@event", eventId), ("@type", eventType), ("@object", savedReference), ("@now", Now())); tx.Commit();
        }
        // Durably queued notifications are acknowledged even when a concurrent
        // payment operation owns the lease. Recovery retries that inbox entry.
        try { await ProcessNotificationAsync(key, ct); }
        catch (Exception error) when (error is StripeException or HttpRequestException or TaskCanceledException or MerchantFailure) { await InboxFailed(key, CancellationToken.None); }
    }
    private static string? Field(JsonElement root, string name)
    {
        if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty(name, out var value) || value.ValueKind is JsonValueKind.Null) return null;
        var text = value.ValueKind == JsonValueKind.String ? value.GetString() : value.ValueKind == JsonValueKind.Object ? Field(value, "id") : null;
        return text is { Length: > 0 and <= 160 } ? text : null;
    }
    private async Task ProcessNotificationAsync(InboxKey key, CancellationToken ct)
    {
        NotificationReference reference; string eventType, tenant;
        await using (var db = await database.OpenAsync(ct))
        {
            using var tx = db.BeginTransaction(deferred: true);
            var saved = (await Rows(db, tx, "SELECT i.event_type,i.object_id,a.tenant_id FROM tide_merchant_notification_inbox i JOIN tide_merchant_accounts a ON a.account_id=i.account_id AND a.environment=i.environment WHERE i.context=@context AND i.environment='test' AND i.account_id=@account AND i.event_id=@event AND i.state<>'done'", r => (r.GetString(0), r.GetString(1), r.GetString(2)), ct,
                ("@context", key.Context), ("@account", key.Account), ("@event", key.EventId))).SingleOrDefault();
            if (saved == default) return;
            eventType = saved.Item1; reference = JsonSerializer.Deserialize<NotificationReference>(saved.Item2, Json)!; tenant = saved.Item3; tx.Commit();
        }
        if (key.Context == "thin")
        {
            var started = Now(); var snapshot = await provider.AccountAsync(key.Account, tenant, ct);
            await using var db = await database.OpenAsync(ct); using var tx = db.BeginTransaction(deferred: false);
            await Run(db, tx, "UPDATE tide_merchant_accounts SET state=@state,checked_at=@checked,updated_at=@now,version=version+1 WHERE tenant_id=@tenant AND environment='test' AND account_id=@account AND (checked_at IS NULL OR checked_at<=@checked)", ct,
                ("@state", snapshot.State), ("@checked", started), ("@now", Now()), ("@tenant", tenant), ("@account", key.Account));
            await CompleteInbox(db, tx, key, ct); tx.Commit(); return;
        }
        string? attemptId;
        await using (var db = await database.OpenAsync(ct))
        {
            using var tx = db.BeginTransaction(deferred: true);
            var matches = await Rows(db, tx, "SELECT id FROM tide_restaurant_payment_attempts WHERE environment='test' AND tenant_id=@tenant AND account_id=@account AND (session_id=@object OR intent_id=@intent OR charge_id=@charge OR id=@attempt) LIMIT 2", r => r.GetString(0), ct,
                ("@tenant", tenant), ("@account", key.Account), ("@object", reference.ObjectId), ("@intent", reference.IntentId), ("@charge", reference.ChargeId), ("@attempt", reference.AttemptId));
            if (matches.Count > 1) throw new MerchantFailure("Ambiguous payment event.", 409, "merchant_review");
            attemptId = matches.SingleOrDefault(); tx.Commit();
        }
        if (attemptId is null) throw new MerchantFailure("Payment event is waiting for its order binding.", 409, "merchant_pending");
        await ReconcileAsync(attemptId, false, key, ct, eventType.StartsWith("checkout.session.", StringComparison.Ordinal) ? reference.ObjectId : null);
    }
    private static Task<int> CompleteInbox(DbConnection db, DbTransaction tx, InboxKey key, CancellationToken ct) =>
        Run(db, tx, "UPDATE tide_merchant_notification_inbox SET state='done',attempts=attempts+1,last_error=NULL,lease_key=NULL,lease_until=NULL,updated_at=@now WHERE context=@context AND environment='test' AND account_id=@account AND event_id=@event", ct,
            ("@now", Now()), ("@context", key.Context), ("@account", key.Account), ("@event", key.EventId));
    private async Task InboxFailed(InboxKey key, CancellationToken ct)
    {
        await using var db = await database.OpenAsync(ct); using var tx = db.BeginTransaction(deferred: false);
        await Run(db, tx, "UPDATE tide_merchant_notification_inbox SET attempts=attempts+1,last_error='reconciliation_pending',updated_at=@now WHERE context=@context AND environment='test' AND account_id=@account AND event_id=@event AND state<>'done'", ct,
            ("@now", Now()), ("@context", key.Context), ("@account", key.Account), ("@event", key.EventId)); tx.Commit();
    }
    public async Task RecoverAsync(CancellationToken ct)
    {
        if (!options.Configured) return;
        List<InboxKey> events; List<string> attempts;
        await using (var db = await database.OpenAsync(ct))
        {
            using var tx = db.BeginTransaction(deferred: true);
            events = await Rows(db, tx, "SELECT context,account_id,event_id FROM tide_merchant_notification_inbox WHERE environment='test' AND state<>'done' ORDER BY updated_at LIMIT 30", r => new InboxKey(r.GetString(0), r.GetString(1), r.GetString(2)), ct);
            attempts = await Rows(db, tx, "SELECT id FROM tide_restaurant_payment_attempts WHERE environment='test' AND state IN ('reserved','creating','open','paid','refund_pending','partially_refunded','review') ORDER BY CASE WHEN state IN ('reserved','creating','open') THEN 0 ELSE 1 END,updated_at LIMIT 30", r => r.GetString(0), ct); tx.Commit();
        }
        foreach (var key in events)
        {
            try { await ProcessNotificationAsync(key, ct); }
            catch (Exception error) when (error is StripeException or HttpRequestException or TaskCanceledException or MerchantFailure) { if (ct.IsCancellationRequested) return; await InboxFailed(key, ct); }
        }
        foreach (var id in attempts)
        {
            try { await ReconcileAsync(id, false, null, ct); }
            catch (Exception error) when (error is StripeException or HttpRequestException or TaskCanceledException or MerchantFailure) { if (ct.IsCancellationRequested) return; }
        }
    }
}

public sealed class MerchantRecovery(IServiceScopeFactory scopes, MerchantPaymentOptions settings, ILogger<MerchantRecovery> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(TimeSpan.FromSeconds(45), stoppingToken);
                if (!settings.Configured) continue;
                using var scope = scopes.CreateScope();
                await scope.ServiceProvider.GetRequiredService<MerchantPaymentsStore>().RecoverAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            catch { logger.LogWarning("Merchant payment recovery will retry later."); }
        }
    }
}
