using TideCasa.Api.Infrastructure;
using System.Globalization;
using System.Text.Json;
using System.Data.Common;
using TideCasa.Api.Infrastructure.Payments;
using static TideCasa.Api.Features.ServiceBilling.ServiceBillingProvider;

namespace TideCasa.Api.Features.ServiceBilling;

public sealed partial class ServiceBillingStore
{
    private sealed record Bound(JsonElement Session, JsonElement Subscription, string Customer, string FirstInvoice, long PeriodEnd);
    private sealed record InvoiceEvidence(JsonElement Invoice, JsonElement? Charge, IReadOnlyList<JsonElement> Refunds, bool Paid, string Kind, long Amount, string? PaidAt);
    private sealed record Inbox(string EventId, string EventType, string ObjectType, string ObjectId, string Lease);
    private static readonly HashSet<string> EventTypes = ["checkout.session.completed", "checkout.session.async_payment_succeeded", "checkout.session.async_payment_failed", "checkout.session.expired",
        "invoice.finalized", "invoice.paid", "invoice.payment_failed", "invoice.payment_action_required", "invoice.finalization_failed", "invoice.voided", "invoice.marked_uncollectible",
        "customer.subscription.created", "customer.subscription.updated", "customer.subscription.deleted", "customer.subscription.paused", "customer.subscription.resumed",
        "charge.refunded", "refund.created", "refund.updated", "refund.failed"];

    public async Task AcceptWebhookAsync(ReadOnlyMemory<byte> raw, string signature, CancellationToken ct)
    {
        provider.NeedReady();
        try { StripeWebhookSignature.Verify(raw.Span, signature, provider.WebhookSecret); }
        catch (StripeSignatureException) { throw new BillingException("The payment notification signature is invalid.", 400, "invalid_signature"); }
        using var document = JsonDocument.Parse(raw, new JsonDocumentOptions { MaxDepth = 48 }); var root = document.RootElement;
        StripeJson.RejectDuplicateProperties(root);
        if (S(root, "api_version") != ApiVersion || S(root, "object") != "event" || P(root, "livemode").ValueKind is not (JsonValueKind.True or JsonValueKind.False)
            || B(root, "livemode") != (provider.Environment == "live") || P(root, "account").ValueKind is not (JsonValueKind.Undefined or JsonValueKind.Null))
            throw new BillingException("This notification belongs to a different payment context.", 400, "wrong_context");
        var eventId = ProviderId(S(root, "id"), "evt"); var eventType = S(root, "type") ?? "";
        if (!EventTypes.Contains(eventType)) return;
        var obj = P(P(root, "data"), "object"); var kind = S(obj, "object") ?? "";
        var expected = eventType.StartsWith("checkout.") ? "checkout.session" : eventType.StartsWith("customer.subscription.") ? "subscription" : eventType.StartsWith("invoice.") ? "invoice" : eventType.StartsWith("refund.") ? "refund" : "charge";
        var prefix = kind switch { "checkout.session" => "cs", "subscription" => "sub", "invoice" => "in", "charge" => "ch", "refund" => "re", _ => "invalid" };
        if (kind != expected || !Id(S(obj, "id"), prefix)) throw new BillingException("The payment notification reference is invalid.", 400, "invalid_event");
        var objectId = S(obj, "id")!;
        await using (var db = await database.OpenAsync(ct))
        {
            using var tx = db.BeginTransaction(deferred: false); var now = Now();
            await Run(db, tx, "INSERT INTO tide_service_event_inbox(environment,event_id,event_type,object_type,object_id,created_at,updated_at) VALUES(@environment,@id,@type,@kind,@object,@now,@now) ON CONFLICT DO NOTHING", ct,
                ("@environment", provider.Environment), ("@id", eventId), ("@type", eventType), ("@kind", kind), ("@object", objectId), ("@now", now));
            if (await Count(db, tx, "SELECT COUNT(*) FROM tide_service_event_inbox WHERE environment=@environment AND event_id=@id AND event_type=@type AND object_type=@kind AND object_id=@object", ct,
                ("@environment", provider.Environment), ("@id", eventId), ("@type", eventType), ("@kind", kind), ("@object", objectId)) != 1) throw Review();
            await tx.CommitAsync(ct);
        }
        await ProcessEventAsync(eventId, ct);
    }

    public async Task RetryPendingAsync(CancellationToken ct)
    {
        if (!provider.Ready) return;
        List<string> pending;
        await using (var db = await database.OpenAsync(ct))
            pending = await Rows(db, null, "SELECT event_id FROM tide_service_event_inbox WHERE environment=@environment AND state IN('pending','processing') AND lease_until<@now ORDER BY updated_at LIMIT 5", r => r.GetString(0), ct,
                ("@environment", provider.Environment), ("@now", DateTimeOffset.UtcNow.ToUnixTimeSeconds()));
        foreach (var id in pending)
        {
            try { await ProcessEventAsync(id, ct); }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch (Exception) { /* The inbox records a sanitized retry marker; no provider payload is logged. */ }
        }
    }

    private async Task ProcessEventAsync(string eventId, CancellationToken cancellation)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellation); deadline.CancelAfter(TimeSpan.FromSeconds(90)); var ct = deadline.Token;
        Inbox? inbox;
        await using (var db = await database.OpenAsync(ct))
        {
            using var tx = db.BeginTransaction(deferred: false); var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds(); var lease = Guid.NewGuid().ToString("N");
            var rows = await Rows(db, tx, "SELECT event_type,object_type,object_id,state,lease_until FROM tide_service_event_inbox WHERE environment=@environment AND event_id=@id", r => (Type: r.GetString(0), Kind: r.GetString(1), ObjectId: r.GetString(2), State: r.GetString(3), Lease: r.ReadInt64(4)), ct, ("@environment", provider.Environment), ("@id", eventId));
            if (rows.Count == 0 || rows[0].State is "complete" or "ignored") return;
            if (rows[0].State == "processing" && rows[0].Lease >= now) return; // Already durably accepted by the current worker.
            await Run(db, tx, "UPDATE tide_service_event_inbox SET state='processing',lease_token=@lease,lease_until=@until,attempts=attempts+1,updated_at=@time WHERE environment=@environment AND event_id=@id", ct,
                ("@lease", lease), ("@until", now + 120), ("@time", Now()), ("@environment", provider.Environment), ("@id", eventId));
            inbox = new(eventId, rows[0].Type, rows[0].Kind, rows[0].ObjectId, lease); await tx.CommitAsync(ct);
        }
        try
        {
            await provider.CheckAccountAsync(ct);
            var resource = inbox.ObjectType switch { "checkout.session" => "checkout/sessions/", "subscription" => "subscriptions/", "invoice" => "invoices/", "charge" => "charges/", "refund" => "refunds/", _ => throw Review() };
            var current = await provider.GetAsync("/v1/" + resource + inbox.ObjectId, ct);
            if (S(current, "id") != inbox.ObjectId || S(current, "object") != inbox.ObjectType) throw Review();
            JsonElement? invoice = null, incomingRefund = null; string? subId = null; JsonElement reference = current;
            if (inbox.ObjectType is "charge" or "refund")
            {
                JsonElement charge;
                if (inbox.ObjectType == "refund") { incomingRefund = current; charge = await provider.GetAsync("/v1/charges/" + ProviderId(ObjectId(current, "charge"), "ch"), ct); }
                else charge = current;
                var intentId = ObjectId(charge, "payment_intent");
                if (!Id(intentId, "pi")) { await Ignore(inbox, ct); return; }
                var payments = await provider.GetAsync("/v1/invoice_payments?payment[type]=payment_intent&payment[payment_intent]=" + intentId + "&status=paid&limit=2", ct);
                var entries = Data(payments);
                if (entries.Count == 0 && !B(payments, "has_more")) { await Ignore(inbox, ct); return; }
                if (entries.Count != 1 || B(payments, "has_more")) throw Review();
                invoice = await provider.GetAsync("/v1/invoices/" + ProviderId(ObjectId(entries[0], "invoice"), "in"), ct);
            }
            else if (inbox.ObjectType == "invoice") invoice = current;
            if (invoice is { } inv)
            {
                subId = ObjectId(P(P(inv, "parent"), "subscription_details"), "subscription");
                if (!Id(subId, "sub")) { await Ignore(inbox, ct); return; }
                reference = await provider.GetAsync("/v1/subscriptions/" + subId, ct);
            }
            else if (inbox.ObjectType == "subscription") subId = S(current, "id");
            else subId = ObjectId(current, "subscription");
            var metadata = P(reference, "metadata");
            if (S(metadata, "tide_purpose") != Purpose) { await Ignore(inbox, ct); return; }
            Order order;
            await using (var db = await database.OpenAsync(ct))
                order = (await Orders(db, null, "WHERE id=@id AND environment=@environment", ct, ("@id", S(metadata, "tide_order_id")), ("@environment", provider.Environment))).SingleOrDefault()
                    ?? throw new BillingException("The purchase is still being saved. The notification will retry.", 503, "purchase_pending");
            Metadata(order, reference);
            if (subId is null)
            {
                ValidateSession(order, current);
                await using var db = await database.OpenAsync(ct); using var tx = db.BeginTransaction(deferred: false);
                var state = S(current, "status") == "expired" ? "expired" : S(current, "status") == "open" ? "pending" : "processing";
                if (await Run(db, tx, "UPDATE tide_service_orders SET session_id=COALESCE(session_id,@session),status=CASE WHEN status='paid' THEN status ELSE @state END,subscription_revision=subscription_revision+1,updated_at=@now WHERE id=@id AND subscription_revision=@revision", ct,
                    ("@session", S(current, "id")), ("@state", state), ("@now", Now()), ("@id", order.Id), ("@revision", order.Revision)) != 1) throw Changed();
                await Complete(db, tx, inbox, order.Id, ct); await tx.CommitAsync(ct); return;
            }
            var bound = await BindAsync(order, ProviderId(subId, "sub"), ct);
            if (invoice is null) invoice = await provider.GetAsync("/v1/invoices/" + ProviderId(ObjectId(bound.Subscription, "latest_invoice") ?? bound.FirstInvoice, "in"), ct);
            InvoiceEvidence? evidence = invoice is { } invoiceValue ? await InvoiceAsync(order, invoiceValue, bound, incomingRefund, ct) : null;
            if (inbox.ObjectType == "charge" && (evidence?.Charge is not { } captured || S(captured, "id") != inbox.ObjectId)) throw Review();
            await using (var db = await database.OpenAsync(ct))
            {
                using var tx = db.BeginTransaction(deferred: false);
                await SaveBound(db, tx, order, bound, ct);
                if (evidence is not null) await SaveInvoice(db, tx, order, evidence, ct);
                await Complete(db, tx, inbox, order.Id, ct); await tx.CommitAsync(ct);
            }
        }
        catch
        {
            try
            {
                await using var db = await database.OpenAsync(CancellationToken.None);
                using var tx = db.BeginTransaction(deferred: false);
                await Run(db, tx, "UPDATE tide_service_event_inbox SET state='pending',lease_token=NULL,lease_until=@until,failure_code='retry_required',updated_at=@now WHERE environment=@environment AND event_id=@id AND lease_token=@lease", CancellationToken.None,
                    ("@until", DateTimeOffset.UtcNow.AddSeconds(30).ToUnixTimeSeconds()), ("@now", Now()), ("@environment", provider.Environment), ("@id", inbox.EventId), ("@lease", inbox.Lease));
                await tx.CommitAsync(CancellationToken.None);
            }
            catch (DbException) { }
            throw;
        }
    }

    private async Task<Bound> BindAsync(Order order, string subscriptionId, CancellationToken ct)
    {
        JsonElement session;
        if (order.SessionId is not null) session = await provider.GetAsync("/v1/checkout/sessions/" + ProviderId(order.SessionId, "cs"), ct);
        else
        {
            // A signed event can arrive after Stripe succeeds but before its session ID is committed.
            var found = await provider.GetAsync("/v1/checkout/sessions?subscription=" + ProviderId(subscriptionId, "sub") + "&limit=2", ct);
            var sessions = Data(found); if (sessions.Count != 1 || B(found, "has_more")) throw new BillingException("Checkout is still being saved. The notification will retry.", 503, "purchase_pending");
            session = sessions[0];
        }
        ValidateSession(order, session);
        if (ObjectId(session, "subscription") != subscriptionId || order.SubscriptionId is not null && order.SubscriptionId != subscriptionId) throw Review();
        var subscription = await provider.GetAsync("/v1/subscriptions/" + ProviderId(subscriptionId, "sub"), ct); Metadata(order, subscription);
        if (S(subscription, "id") != subscriptionId || S(subscription, "object") != "subscription") throw Review();
        var customer = ProviderId(ObjectId(subscription, "customer"), "cus"); var firstInvoice = ProviderId(ObjectId(session, "invoice"), "in");
        if (ObjectId(session, "customer") != customer || order.CustomerId is not null && order.CustomerId != customer || order.InitialInvoiceId is not null && order.InitialInvoiceId != firstInvoice) throw Review();
        var list = P(subscription, "items"); var items = Data(list);
        if (items.Count != 1 || B(list, "has_more")) throw Review();
        var price = P(items[0], "price"); var recurring = P(price, "recurring");
        if (N(items[0], "quantity") != 1 || S(price, "currency") != "usd" || N(price, "unit_amount") != order.Monthly
            || S(recurring, "interval") != "month" || N(recurring, "interval_count") != 1 || N(items[0], "current_period_end") <= 0) throw Review();
        if (S(subscription, "status") is not ("incomplete" or "incomplete_expired" or "trialing" or "active" or "past_due" or "canceled" or "unpaid" or "paused")) throw Review();
        using var snapshot = JsonDocument.Parse(order.RequestJson);
        if (S(snapshot.RootElement, "termsVersion") == TermsVersion)
        {
            if (order.Monthly != MonthlyCents || order.Total != order.Initial + order.Monthly
                || S(subscription, "status") == "trialing" || N(subscription, "trial_start") > 0 || N(subscription, "trial_end") > 0) throw Review();
        }
        else if (S(snapshot.RootElement, "termsVersion") == DeferredTermsVersion)
        {
            var trialStart = N(subscription, "trial_start"); var trialEnd = N(subscription, "trial_end");
            if (order.Monthly != DeferredMonthlyCents || order.Total != order.Initial || trialStart <= 0
                || trialEnd - trialStart != DeferredMaintenanceDelayDays * 86400L) throw Review();
        }
        return new(session, subscription, customer, firstInvoice, N(items[0], "current_period_end"));
    }

    private async Task<InvoiceEvidence> InvoiceAsync(Order order, JsonElement invoice, Bound bound, JsonElement? incomingRefund, CancellationToken ct)
    {
        var invoiceId = ProviderId(S(invoice, "id"), "in"); var kind = invoiceId == bound.FirstInvoice ? "initial" : "recurring"; var amount = kind == "initial" ? order.Total : order.Monthly;
        if (S(invoice, "object") != "invoice" || !Mode(order, invoice) || ObjectId(P(P(invoice, "parent"), "subscription_details"), "subscription") != S(bound.Subscription, "id")
            || ObjectId(invoice, "customer") != bound.Customer || S(invoice, "currency") != "usd" || N(invoice, "total") != amount || N(invoice, "amount_due") != amount
            || kind == "recurring" && S(invoice, "billing_reason") != "subscription_cycle") throw Review();
        var status = S(invoice, "status");
        if (status is not ("draft" or "open" or "paid" or "uncollectible" or "void")) throw Review();
        if (status != "paid")
        {
            if (incomingRefund is not null) throw Review();
            return new(invoice, null, [], false, kind, amount, null);
        }
        if (N(invoice, "amount_paid") != amount || N(invoice, "amount_remaining") != 0) throw Review();
        var paidUnix = N(P(invoice, "status_transitions"), "paid_at"); if (paidUnix <= 0 || paidUnix > DateTimeOffset.UtcNow.AddMinutes(5).ToUnixTimeSeconds()) throw Review();
        var payments = await provider.GetAsync("/v1/invoice_payments?invoice=" + invoiceId + "&status=paid&limit=2", ct); var data = Data(payments);
        if (data.Count != 1 || B(payments, "has_more")) throw Review();
        var payment = data[0]; var paidObject = P(payment, "payment");
        if (!Mode(order, payment) || S(payment, "currency") != "usd" || N(payment, "amount_paid") != amount || ObjectId(payment, "invoice") != invoiceId || S(payment, "status") != "paid" || S(paidObject, "type") != "payment_intent") throw Review();
        var intentId = ProviderId(ObjectId(paidObject, "payment_intent"), "pi"); var intent = await provider.GetAsync("/v1/payment_intents/" + intentId, ct);
        if (S(intent, "id") != intentId || !Mode(order, intent) || S(intent, "currency") != "usd" || N(intent, "amount_received") != amount || S(intent, "status") != "succeeded" || ObjectId(intent, "customer") != bound.Customer) throw Review();
        var chargeId = ProviderId(ObjectId(intent, "latest_charge"), "ch"); var charge = await provider.GetAsync("/v1/charges/" + chargeId, ct);
        if (S(charge, "id") != chargeId || !Mode(order, charge) || ObjectId(charge, "payment_intent") != intentId || ObjectId(charge, "customer") != bound.Customer
            || S(charge, "currency") != "usd" || !B(charge, "paid") || !B(charge, "captured") || N(charge, "amount") != amount || N(charge, "amount_captured") != amount || S(P(charge, "payment_method_details"), "type") != "card") throw Review();
        var refunds = new Dictionary<string, JsonElement>(StringComparer.Ordinal); string? after = null;
        for (var page = 0; ; page++)
        {
            var response = await provider.GetAsync("/v1/refunds?charge=" + chargeId + "&limit=100" + (after is null ? "" : "&starting_after=" + after), ct);
            var entries = Data(response);
            foreach (var refund in entries) { var id = ProviderId(S(refund, "id"), "re"); if (!refunds.TryAdd(id, refund)) throw Review(); }
            if (!B(response, "has_more")) break;
            if (page >= 9 || entries.Count == 0) throw Review(); after = S(entries[^1], "id");
        }
        // The paged list was read after the event's refund reference. Do not let
        // an earlier pending snapshot overwrite a later succeeded list entry.
        if (incomingRefund is { } incoming && !refunds.ContainsKey(ProviderId(S(incoming, "id"), "re")))
        {
            var refreshed = await provider.GetAsync("/v1/refunds/" + ProviderId(S(incoming, "id"), "re"), ct);
            if (S(refreshed, "id") != S(incoming, "id")) throw Review();
            refunds.Add(S(incoming, "id")!, refreshed);
        }
        foreach (var refund in refunds.Values)
            if (ObjectId(refund, "charge") != chargeId || ObjectId(refund, "payment_intent") != intentId || S(refund, "currency") != "usd" || N(refund, "amount") <= 0 || N(refund, "amount") > amount
                || S(refund, "status") is not ("pending" or "requires_action" or "succeeded" or "failed" or "canceled")) throw Review();
        if (refunds.Values.Where(x => S(x, "status") is "pending" or "requires_action" or "succeeded").Sum(x => N(x, "amount")) > amount) throw Review();
        return new(invoice, charge, refunds.Values.ToArray(), true, kind, amount, DateTimeOffset.FromUnixTimeSeconds(paidUnix).ToString("O"));
    }

    private static async Task SaveBound(DbConnection db, DbTransaction tx, Order order, Bound bound, CancellationToken ct)
    {
        if (await Run(db, tx, "UPDATE tide_service_orders SET session_id=@session,subscription_id=@sub,customer_id=@customer,initial_invoice_id=@invoice,subscription_status=@status,cancel_at_period_end=@cancel,period_end=@end,subscription_revision=subscription_revision+1,updated_at=@now WHERE id=@id AND subscription_revision=@revision", ct,
            ("@session", S(bound.Session, "id")), ("@sub", S(bound.Subscription, "id")), ("@customer", bound.Customer), ("@invoice", bound.FirstInvoice), ("@status", S(bound.Subscription, "status")),
            ("@cancel", B(bound.Subscription, "cancel_at_period_end") ? 1 : 0), ("@end", bound.PeriodEnd), ("@now", Now()), ("@id", order.Id), ("@revision", order.Revision)) != 1) throw Changed();
    }

    private static async Task SaveInvoice(DbConnection db, DbTransaction tx, Order order, InvoiceEvidence evidence, CancellationToken ct)
    {
        var invoiceId = S(evidence.Invoice, "id")!; var now = Now();
        if (await Run(db, tx, """
            INSERT INTO tide_service_invoices(id,order_id,kind,amount_cents,status,hosted_url,paid_at,updated_at) VALUES(@id,@order,@kind,@amount,@status,@url,@paid,@now)
            ON CONFLICT(id) DO UPDATE SET status=CASE WHEN tide_service_invoices.paid_at IS NOT NULL THEN 'paid' ELSE excluded.status END,
             hosted_url=excluded.hosted_url,paid_at=COALESCE(tide_service_invoices.paid_at,excluded.paid_at),updated_at=excluded.updated_at
            WHERE tide_service_invoices.order_id=excluded.order_id AND tide_service_invoices.kind=excluded.kind AND tide_service_invoices.amount_cents=excluded.amount_cents
            """, ct, ("@id", invoiceId), ("@order", order.Id), ("@kind", evidence.Kind), ("@amount", evidence.Amount), ("@status", S(evidence.Invoice, "status")), ("@url", HostedUrl(S(evidence.Invoice, "hosted_invoice_url"), "invoice.stripe.com")), ("@paid", evidence.PaidAt), ("@now", now)) != 1) throw Review();
        if (!evidence.Paid) return;
        var chargeId = S(evidence.Charge!.Value, "id")!;
        await Run(db, tx, "INSERT INTO tide_service_refund_sync(invoice_id,charge_id) VALUES(@invoice,@charge) ON CONFLICT DO NOTHING", ct, ("@invoice", invoiceId), ("@charge", chargeId));
        if (await Count(db, tx, "SELECT COUNT(*) FROM tide_service_refund_sync WHERE invoice_id=@invoice AND charge_id=@charge", ct, ("@invoice", invoiceId), ("@charge", chargeId)) != 1) throw Review();
        foreach (var refund in evidence.Refunds)
        {
            if (await Run(db, tx, """
                INSERT INTO tide_service_refunds(id,invoice_id,charge_id,amount_cents,status,updated_at) VALUES(@id,@invoice,@charge,@amount,@status,@now)
                ON CONFLICT(id) DO UPDATE SET status=excluded.status,updated_at=excluded.updated_at
                WHERE tide_service_refunds.invoice_id=excluded.invoice_id AND tide_service_refunds.charge_id=excluded.charge_id AND tide_service_refunds.amount_cents=excluded.amount_cents
                """, ct, ("@id", S(refund, "id")), ("@invoice", invoiceId), ("@charge", chargeId), ("@amount", N(refund, "amount")), ("@status", S(refund, "status")), ("@now", now)) != 1) throw Review();
        }
        var refunded = await Count(db, tx, "SELECT COALESCE(SUM(amount_cents),0) FROM tide_service_refunds WHERE invoice_id=@invoice AND status='succeeded'", ct, ("@invoice", invoiceId));
        var pending = await Count(db, tx, "SELECT COALESCE(SUM(amount_cents),0) FROM tide_service_refunds WHERE invoice_id=@invoice AND status IN('pending','requires_action')", ct, ("@invoice", invoiceId));
        var failed = await Count(db, tx, "SELECT COALESCE(SUM(amount_cents),0) FROM tide_service_refunds WHERE invoice_id=@invoice AND status='failed'", ct, ("@invoice", invoiceId));
        if (refunded + pending > evidence.Amount) throw Review();
        var revision = await Count(db, tx, "SELECT refund_revision FROM tide_service_invoices WHERE id=@id", ct, ("@id", invoiceId)) + 1;
        await Run(db, tx, "UPDATE tide_service_invoices SET refunded_cents=@refund,refund_pending_cents=@pending,refund_failed_cents=@failed,refund_revision=@revision WHERE id=@id", ct,
            ("@refund", refunded), ("@pending", pending), ("@failed", failed), ("@revision", revision), ("@id", invoiceId));
        await Run(db, tx, "UPDATE tide_service_refund_sync SET revision=@revision,token=@token WHERE invoice_id=@id", ct, ("@revision", revision), ("@token", Guid.NewGuid().ToString("N")), ("@id", invoiceId));
        if (evidence.Kind == "initial")
        {
            await Run(db, tx, "UPDATE tide_service_orders SET status='paid',paid_at=COALESCE(paid_at,@paid),updated_at=@now WHERE id=@id", ct, ("@paid", evidence.PaidAt), ("@now", now), ("@id", order.Id));
            if (order.Environment == "live" && refunded == 0 && pending == 0)
                await Run(db, tx, "UPDATE bartide_customers SET status='building',enrollment_note=@note,enrolled_at=@now,build_ready_at=@ready,version=version+1,updated_at=@now WHERE id=@tenant AND status='draft'", ct,
                    ("@note", "Stripe verified setup and maintenance schedule: " + invoiceId), ("@now", evidence.PaidAt!), ("@ready", DateTimeOffset.Parse(evidence.PaidAt!, CultureInfo.InvariantCulture).AddDays(order.Monthly is DeferredMonthlyCents or MonthlyCents ? BuildLeadDays : 7).ToString("O")), ("@tenant", order.TenantId));
        }
        using var document = JsonDocument.Parse(order.RequestJson); var referral = P(document.RootElement, "referral"); var profile = S(referral, "profileId");
        if (profile is null) return;
        var initialRefund = evidence.Kind == "initial" ? refunded * order.Initial / evidence.Amount : 0;
        if (evidence.Kind == "initial" && order.Initial > 0) await ReferralSale(db, tx, order, invoiceId + ":initial", profile, "initial", order.Initial, initialRefund, revision, evidence.PaidAt!, ct);
        var maintenanceGross = evidence.Kind == "initial" ? order.Total - order.Initial : order.Monthly;
        if (maintenanceGross > 0)
            await ReferralSale(db, tx, order, invoiceId + ":maintenance", profile, "recurring", maintenanceGross, refunded - initialRefund, revision, evidence.PaidAt!, ct);
    }

    private static async Task ReferralSale(DbConnection db, DbTransaction tx, Order order, string sale, string profile, string kind, long gross, long refunded, long revision, string paidAt, CancellationToken ct)
    {
        var commission = (gross - refunded) * (kind == "initial" ? 20 : 10) / 100;
        if (refunded < 0 || refunded > gross || await Run(db, tx, """
            INSERT INTO tide_referral_sales(event_id,order_id,profile_id,kind,environment,gross_cents,refunded_cents,commission_cents,source_revision,paid_at,updated_at)
            VALUES(@event,@order,@profile,@kind,@environment,@gross,@refund,@commission,@revision,@paid,@now)
            ON CONFLICT(event_id) DO UPDATE SET refunded_cents=excluded.refunded_cents,commission_cents=excluded.commission_cents,source_revision=excluded.source_revision,updated_at=excluded.updated_at
            WHERE tide_referral_sales.order_id=excluded.order_id AND tide_referral_sales.profile_id=excluded.profile_id AND tide_referral_sales.kind=excluded.kind
             AND tide_referral_sales.environment=excluded.environment AND tide_referral_sales.gross_cents=excluded.gross_cents AND tide_referral_sales.source_revision<=excluded.source_revision
            """, ct, ("@event", sale), ("@order", order.Id), ("@profile", profile), ("@kind", kind), ("@environment", order.Environment), ("@gross", gross), ("@refund", refunded), ("@commission", commission), ("@revision", revision), ("@paid", paidAt), ("@now", Now())) != 1) throw Review();
    }

    private async Task Ignore(Inbox inbox, CancellationToken ct)
    {
        await using var db = await database.OpenAsync(ct); using var tx = db.BeginTransaction(deferred: false);
        if (await Run(db, tx, "UPDATE tide_service_event_inbox SET state='ignored',lease_token=NULL,lease_until=0,failure_code=NULL,updated_at=@now WHERE environment=@environment AND event_id=@id AND state='processing' AND lease_token=@lease", ct,
            ("@now", Now()), ("@environment", provider.Environment), ("@id", inbox.EventId), ("@lease", inbox.Lease)) != 1) throw Changed();
        await tx.CommitAsync(ct);
    }
    private async Task Complete(DbConnection db, DbTransaction tx, Inbox inbox, string orderId, CancellationToken ct)
    {
        if (await Count(db, tx, "SELECT COUNT(*) FROM tide_service_events WHERE id=@id AND order_id<>@order", ct, ("@id", inbox.EventId), ("@order", orderId)) != 0) throw Review();
        await Run(db, tx, "INSERT INTO tide_service_events(id,order_id,event_type,processed_at) VALUES(@id,@order,@type,@now) ON CONFLICT DO NOTHING", ct, ("@id", inbox.EventId), ("@order", orderId), ("@type", inbox.EventType), ("@now", Now()));
        if (await Run(db, tx, "UPDATE tide_service_event_inbox SET state='complete',lease_token=NULL,lease_until=0,failure_code=NULL,updated_at=@now WHERE environment=@environment AND event_id=@id AND state='processing' AND lease_token=@lease AND lease_until>=@time", ct,
            ("@now", Now()), ("@environment", provider.Environment), ("@id", inbox.EventId), ("@lease", inbox.Lease), ("@time", DateTimeOffset.UtcNow.ToUnixTimeSeconds())) != 1) throw Changed();
    }
}
