using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Data.Common;
using TideCasa.Api.Infrastructure;
using TideCasa.Contracts;
using static TideCasa.Api.Features.ServiceBilling.ServiceBillingProvider;

namespace TideCasa.Api.Features.ServiceBilling;

public sealed partial class ServiceBillingStore(ApplicationDatabase database, ServiceBillingProvider provider)
{
    private sealed record Tenant(string Id, string Name, string Email, string Status, string Vertical);
    private sealed record Referral(string ProfileId, string Code, int DiscountPercent);
    private sealed record Order(string Id, string TenantId, string Environment, string Status, string RequestJson, long Initial, long Monthly, long Total,
        bool AppStores, string? SessionId, string? SubscriptionId, string? CustomerId, string? InitialInvoiceId, string SubscriptionStatus,
        bool CancelAtPeriodEnd, long? PeriodEnd, string? PaidAt, long Revision, string CreatedAt);
    private const string OrderColumns = "id,tenant_id,environment,status,request_json,initial_cents,monthly_cents,total_cents,app_stores,session_id,subscription_id,customer_id,initial_invoice_id,subscription_status,cancel_at_period_end,period_end,paid_at,subscription_revision,created_at";
    private static string Now() => DateTimeOffset.UtcNow.ToString("O");
    private static BillingException Changed() => new("Billing changed while this request was being processed. Refresh and try again.", 409, "billing_changed");
    private static BillingException Missing() => new("This billing record is unavailable.", 404, "billing_missing");

    public async Task<ServiceBillingWorkspace> WorkspaceAsync(string tenant, AuthUser user, CancellationToken ct)
    {
        await using var db = await database.OpenAsync(ct); using var tx = db.BeginTransaction(deferred: true);
        var owner = await Owner(db, tx, tenant, user, ct);
        var rows = await Orders(db, tx, "WHERE tenant_id=@tenant ORDER BY created_at DESC LIMIT 20", ct, ("@tenant", tenant));
        var orders = new List<ServiceBillingOrder>();
        foreach (var row in rows)
        {
            var invoices = await Rows(db, tx, "SELECT id,kind,amount_cents,status,refunded_cents,refund_pending_cents,refund_failed_cents,paid_at,hosted_url FROM tide_service_invoices WHERE order_id=@id ORDER BY updated_at DESC,id DESC LIMIT 36", r =>
                new ServiceBillingInvoice(r.GetString(0), r.GetString(1), r.ReadInt64(2), r.GetString(3), r.ReadInt64(4), r.ReadInt64(5), r.ReadInt64(6), Nullable(r, 7), HostedUrl(Nullable(r, 8), "invoice.stripe.com")), ct, ("@id", row.Id));
            var enabled = provider.Ready && row.Environment == provider.Environment;
            orders.Add(new(row.Id, row.Environment, row.Status, row.Initial, row.Monthly, row.Total, row.AppStores, row.SubscriptionStatus, row.CancelAtPeriodEnd, row.PeriodEnd, row.PaidAt,
                enabled && row.SubscriptionId is not null && row.SubscriptionStatus is not ("canceled" or "incomplete_expired") && !row.CancelAtPeriodEnd,
                enabled && row.SessionId is not null && row.Status is "pending" or "processing", invoices));
        }
        var historical = await Rows(db, tx, "SELECT id,environment,channel,status,amount_cents,refunded_cents,refund_pending_cents,paid_at,hosted_url FROM bartide_payment_orders WHERE tenant_id=@tenant ORDER BY created_at DESC LIMIT 30", r =>
            new HistoricalBillingOrder(r.GetString(0), r.GetString(1), r.GetString(2), r.GetString(3), r.ReadInt64(4), r.ReadInt64(5), r.ReadInt64(6), Nullable(r, 7), HostedUrl(Nullable(r, 8), r.GetString(2) == "invoice" ? "invoice.stripe.com" : "checkout.stripe.com")), ct, ("@tenant", tenant));
        var canPurchase = owner.Status == "draft" && owner.Vertical is "bartide" or "tide-casa"
            && await Count(db, tx, "SELECT COUNT(*) FROM tide_service_orders WHERE tenant_id=@tenant AND environment=@environment AND status NOT IN('failed','expired')", ct, ("@tenant", tenant), ("@environment", provider.Environment)) == 0
            && await Count(db, tx, "SELECT COUNT(*) FROM bartide_payment_orders WHERE tenant_id=@tenant AND environment=@environment AND status NOT IN('failed','expired','void')", ct, ("@tenant", tenant), ("@environment", provider.Environment)) == 0;
        return new(tenant, owner.Name, provider.Environment, provider.CheckoutReady, canPurchase,
            provider.CheckoutReady ? "Review the first payment and automatic monthly renewal before continuing." : "New checkout is being connected. Your saved billing history is available here.", orders, historical);
    }

    public async Task<ServiceBillingQuote> QuoteAsync(string tenant, AuthUser user, ServiceQuoteRequest request, CancellationToken ct)
    {
        await using var db = await database.OpenAsync(ct); using var tx = db.BeginTransaction(deferred: true);
        var owner = await Owner(db, tx, tenant, user, ct);
        return (await Quote(db, tx, owner, user, request.AppStores, request.ReferralCode, ct)).Quote;
    }

    public async Task<ServiceCheckoutLink> CheckoutAsync(string tenant, AuthUser user, ServiceCheckoutRequest request, CancellationToken ct)
    {
        if (!Guid.TryParseExact(request.RequestId, "D", out _) || !request.AcceptedTerms || request.TermsVersion != TermsVersion)
            throw new BillingException("Review and accept the first payment and $50 automatic monthly renewal.", 400, "terms_required");
        if (!provider.CheckoutReady) throw Unavailable();
        await using (var db = await database.OpenAsync(ct)) { using var tx = db.BeginTransaction(deferred: true); await Owner(db, tx, tenant, user, ct); }
        await provider.CheckMethodsAsync(ct);
        Order order;
        await using (var db = await database.OpenAsync(ct))
        {
            using var tx = db.BeginTransaction(deferred: false); var owner = await Owner(db, tx, tenant, user, ct);
            if (owner.Status != "draft" || owner.Vertical is not ("bartide" or "tide-casa")) throw new BillingException("New checkout is available for a draft business workspace only.");
            var quoted = await Quote(db, tx, owner, user, request.AppStores, request.ReferralCode, ct);
            if (request.QuoteFingerprint != quoted.Quote.Fingerprint) throw new BillingException("The price or referral changed. Review the updated total before continuing.", 409, "quote_changed");
            if (await Count(db, tx, "SELECT COUNT(*) FROM bartide_payment_orders WHERE tenant_id=@tenant AND environment=@environment AND status NOT IN('failed','expired','void')", ct, ("@tenant", tenant), ("@environment", provider.Environment)) > 0)
                throw new BillingException("An earlier payment exists for this workspace. It must be reviewed before starting a maintenance purchase.", 409, "historical_purchase");
            var existing = (await Orders(db, tx, "WHERE tenant_id=@tenant AND environment=@environment AND status NOT IN('failed','expired')", ct, ("@tenant", tenant), ("@environment", provider.Environment))).SingleOrDefault();
            if (existing is null)
            {
                var now = Now(); var ident = Guid.NewGuid().ToString("D");
                var quote = quoted.Quote;
                var saved = JsonSerializer.Serialize(new { email = owner.Email, name = owner.Name, origin = provider.Origin, expires = DateTimeOffset.UtcNow.AddHours(23).ToUnixTimeSeconds(),
                    termsVersion = TermsVersion, consentedAt = now, appStores = request.AppStores, requestId = request.RequestId, fingerprint = quote.Fingerprint,
                    methodConfigurationId = provider.MethodConfiguration,
                    referral = quoted.Referral is { } referral ? new { profileId = referral.ProfileId, code = referral.Code, discountPercent = referral.DiscountPercent } : null,
                    quote = new { setupCents = quote.SetupCents, storesCents = quote.StoresCents, initialCents = quote.SetupCents + quote.StoresCents, monthlyCents = 5000, firstCents = quote.FirstPaymentCents, discountCents = quote.DiscountCents } });
                await Run(db, tx, "INSERT INTO tide_service_orders(id,tenant_id,environment,request_json,initial_cents,monthly_cents,total_cents,app_stores,created_at,updated_at) VALUES(@id,@tenant,@environment,@request,@initial,5000,@total,@stores,@now,@now)", ct,
                    ("@id", ident), ("@tenant", tenant), ("@environment", provider.Environment), ("@request", saved), ("@initial", quote.SetupCents + quote.StoresCents), ("@total", quote.FirstPaymentCents), ("@stores", request.AppStores ? 1 : 0), ("@now", now));
                existing = (await Orders(db, tx, "WHERE id=@id", ct, ("@id", ident))).Single();
            }
            order = existing;
            using var snapshot = JsonDocument.Parse(order.RequestJson);
            var savedCode = S(P(snapshot.RootElement, "referral"), "code") ?? "";
            if (order.Total != quoted.Quote.FirstPaymentCents || order.AppStores != request.AppStores || savedCode != (quoted.Quote.ReferralCode ?? ""))
                throw new BillingException("A checkout with different options is already saved. Discard its unpaid checkout before choosing again.", 409, "checkout_exists");
            if (order.Status == "paid") throw new BillingException("Payment is already recorded. Open your billing history.", 409, "already_paid");
            await tx.CommitAsync(ct);
        }
        JsonElement session;
        if (order.SessionId is not null) session = await provider.GetAsync("/v1/checkout/sessions/" + ProviderId(order.SessionId, "cs"), ct);
        else
        {
            if (!DateTimeOffset.TryParse(order.CreatedAt, out var created) || created < DateTimeOffset.UtcNow.AddHours(-23)) throw new BillingException("This unconfirmed checkout needs a billing review before retrying.", 409, "checkout_review");
            session = await provider.PostAsync("/v1/checkout/sessions", CheckoutParameters(order), "tide-dotnet:" + order.Id + ":checkout", ct);
            ValidateSession(order, session, requireMethods: true);
            await using var db = await database.OpenAsync(ct); using var tx = db.BeginTransaction(deferred: false);
            await Owner(db, tx, tenant, user, ct);
            await Run(db, tx, "UPDATE tide_service_orders SET session_id=@session,updated_at=@now WHERE id=@id AND session_id IS NULL", ct, ("@session", S(session, "id")), ("@id", order.Id), ("@now", Now()));
            var current = (await Orders(db, tx, "WHERE id=@id", ct, ("@id", order.Id))).Single();
            if (current.SessionId != S(session, "id")) throw Review();
            await tx.CommitAsync(ct); order = current;
        }
        ValidateSession(order, session, requireMethods: true);
        if (S(session, "status") != "open") throw new BillingException("Checkout is expired or being confirmed. Refresh your billing history.", 409, "checkout_closed");
        var url = HostedUrl(S(session, "url"), "checkout.stripe.com") ?? throw Unavailable();
        return new(order.Id, url, order.Environment);
    }

    public async Task<ServiceCheckoutLink> ResumeCheckoutAsync(string tenant, string orderId, AuthUser user, ServiceBillingActionRequest request, CancellationToken ct)
    {
        if (!request.Confirmed || !Guid.TryParseExact(request.RequestId, "D", out _)) throw new BillingException("Confirm the saved checkout first.", 400, "confirmation_required");
        Order order;
        await using (var db = await database.OpenAsync(ct))
        {
            using var tx = db.BeginTransaction(deferred: true); await Owner(db, tx, tenant, user, ct);
            order = (await Orders(db, tx, "WHERE id=@id AND tenant_id=@tenant AND environment=@environment AND status IN('pending','processing')", ct, ("@id", orderId), ("@tenant", tenant), ("@environment", provider.Environment))).SingleOrDefault() ?? throw Missing();
        }
        using var saved = JsonDocument.Parse(order.RequestJson);
        if (S(saved.RootElement, "termsVersion") != TermsVersion || !DateTimeOffset.TryParse(S(saved.RootElement, "consentedAt"), out _)) throw new BillingException("This earlier checkout needs a billing review before retrying.", 409, "checkout_review");
        var code = S(P(saved.RootElement, "referral"), "code");
        var quote = await QuoteAsync(tenant, user, new(order.AppStores, code), ct);
        if (quote.FirstPaymentCents != order.Total) throw new BillingException("The saved referral price changed. Review billing before trying again.", 409, "quote_changed");
        return await CheckoutAsync(tenant, user, new(request.RequestId, order.AppStores, code, quote.Fingerprint, true, TermsVersion), ct);
    }

    public async Task<ServiceBillingActionResult> ActionAsync(string tenant, string orderId, string action, AuthUser user, ServiceBillingActionRequest request, CancellationToken ct)
    {
        if (!request.Confirmed || !Guid.TryParseExact(request.RequestId, "D", out _)) throw new BillingException("Confirm the billing action first.", 400, "confirmation_required");
        Order order;
        await using (var db = await database.OpenAsync(ct)) { using var tx = db.BeginTransaction(deferred: true); await Owner(db, tx, tenant, user, ct); order = (await Orders(db, tx, "WHERE id=@id AND tenant_id=@tenant AND environment=@environment", ct, ("@id", orderId), ("@tenant", tenant), ("@environment", provider.Environment))).SingleOrDefault() ?? throw Missing(); }
        await provider.CheckAccountAsync(ct);
        if (action == "discard")
        {
            if (order.SessionId is null) throw new BillingException("An unconfirmed checkout needs a billing review before it can be discarded.");
            var session = await provider.GetAsync("/v1/checkout/sessions/" + ProviderId(order.SessionId, "cs"), ct); ValidateSession(order, session);
            if (order.Status == "paid" || S(session, "payment_status") == "paid" || S(session, "status") == "complete") throw new BillingException("A completed checkout cannot be discarded.");
            if (S(session, "status") == "open") await provider.PostAsync("/v1/checkout/sessions/" + order.SessionId + "/expire", new Dictionary<string, string>(), "tide-dotnet:" + order.Id + ":expire", ct);
            session = await provider.GetAsync("/v1/checkout/sessions/" + order.SessionId, ct); ValidateSession(order, session);
            if (S(session, "status") != "expired" || S(session, "payment_status") == "paid") throw Changed();
            await using var db = await database.OpenAsync(ct); using var tx = db.BeginTransaction(deferred: false); await Owner(db, tx, tenant, user, ct);
            if (await Run(db, tx, "UPDATE tide_service_orders SET status='expired',subscription_revision=subscription_revision+1,updated_at=@now WHERE id=@id AND subscription_revision=@revision AND status<>'paid'", ct, ("@id", order.Id), ("@revision", order.Revision), ("@now", Now())) != 1) throw Changed();
            await tx.CommitAsync(ct); return new("The unpaid checkout was discarded. You can review new options.");
        }
        if (action != "cancel" || order.SubscriptionId is null) throw new BillingException("Maintenance is not active yet.");
        var bound = await BindAsync(order, order.SubscriptionId, ct);
        if (!B(bound.Subscription, "cancel_at_period_end") && S(bound.Subscription, "status") is not ("canceled" or "incomplete_expired"))
            await provider.PostAsync("/v1/subscriptions/" + order.SubscriptionId, new Dictionary<string, string> { ["cancel_at_period_end"] = "true" }, "tide-dotnet:" + order.Id + ":cancel-renewal", ct);
        bound = await BindAsync(order, order.SubscriptionId, ct);
        if (!B(bound.Subscription, "cancel_at_period_end") && S(bound.Subscription, "status") is not ("canceled" or "incomplete_expired")) throw Unavailable();
        await using (var db = await database.OpenAsync(ct))
        {
            using var tx = db.BeginTransaction(deferred: false); await Owner(db, tx, tenant, user, ct);
            await SaveBound(db, tx, order, bound, ct); await tx.CommitAsync(ct);
        }
        return new(S(bound.Subscription, "status") == "canceled" ? "Maintenance is already canceled." : "Renewal canceled. Maintenance continues through the current paid period.");
    }

    private async Task<(ServiceBillingQuote Quote, Referral? Referral)> Quote(DbConnection db, DbTransaction tx, Tenant tenant, AuthUser user, bool stores, string? rawCode, CancellationToken ct)
    {
        var code = rawCode?.Trim().ToUpperInvariant() ?? ""; Referral? referral = null;
        if (code.Length != 0)
        {
            if (!Regex.IsMatch(code, "^[A-Z0-9][A-Z0-9-]{2,31}$")) throw new BillingException("Check your referral code.", 400, "referral_unavailable");
            referral = (await Rows(db, tx, db.Sql("SELECT id,code,discount_percent FROM tide_referral_profiles WHERE code=@code COLLATE NOCASE AND status='active' AND user_id<>@user AND email<>@email COLLATE NOCASE AND email<>@buyer COLLATE NOCASE",
            "SELECT id,code,discount_percent FROM tide_referral_profiles WHERE translate(code,'ABCDEFGHIJKLMNOPQRSTUVWXYZ','abcdefghijklmnopqrstuvwxyz')=translate(@code,'ABCDEFGHIJKLMNOPQRSTUVWXYZ','abcdefghijklmnopqrstuvwxyz') COLLATE \"C\" AND status='active' AND user_id<>@user AND translate(email,'ABCDEFGHIJKLMNOPQRSTUVWXYZ','abcdefghijklmnopqrstuvwxyz')<>translate(@email,'ABCDEFGHIJKLMNOPQRSTUVWXYZ','abcdefghijklmnopqrstuvwxyz') COLLATE \"C\" AND translate(email,'ABCDEFGHIJKLMNOPQRSTUVWXYZ','abcdefghijklmnopqrstuvwxyz')<>translate(@buyer,'ABCDEFGHIJKLMNOPQRSTUVWXYZ','abcdefghijklmnopqrstuvwxyz') COLLATE \"C\""), r => new Referral(r.GetString(0), r.GetString(1), r.ReadInt32(2)), ct,
                ("@code", code), ("@user", user.UserId), ("@email", user.Email), ("@buyer", tenant.Email))).SingleOrDefault();
            if (referral is null) throw new BillingException("This referral code is unavailable. You cannot use your own code.", 400, "referral_unavailable");
        }
        var discount = referral?.DiscountPercent ?? 0;
        var setup = 60000 - 60000 * discount / 100; var addon = stores ? 30000 - 30000 * discount / 100 : 0;
        var fingerprint = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new { tenant = tenant.Id, environment = provider.Environment, terms = TermsVersion, stores, profile = referral?.ProfileId, code, setup, addon, monthly = 5000 }))));
        return (new(stores, code.Length == 0 ? null : code, setup, addon, 60000 + (stores ? 30000 : 0) - setup - addon, setup + addon + 5000, 5000, "usd", TermsVersion, fingerprint), referral);
    }

    private Dictionary<string, string> CheckoutParameters(Order order)
    {
        using var document = JsonDocument.Parse(order.RequestJson); var snapshot = document.RootElement; var quote = P(snapshot, "quote");
        if (S(snapshot, "methodConfigurationId") != provider.MethodConfiguration || S(snapshot, "origin") != provider.Origin) throw new BillingException("Checkout configuration changed. This saved request needs review.", 409, "checkout_review");
        var guest = B(snapshot, "guest");
        var values = new Dictionary<string, string> { ["mode"] = "subscription", ["client_reference_id"] = order.Id,
            ["payment_method_configuration"] = provider.MethodConfiguration, ["wallet_options[link][display]"] = "never", ["adaptive_pricing[enabled]"] = "false",
            ["managed_payments[enabled]"] = "false",
            ["expires_at"] = N(snapshot, "expires").ToString(CultureInfo.InvariantCulture),
            ["success_url"] = provider.Origin + "/workspace/" + Uri.EscapeDataString(order.TenantId) + "/billing?checkout=returned",
            ["cancel_url"] = provider.Origin + "/workspace/" + Uri.EscapeDataString(order.TenantId) + "/billing?checkout=cancelled",
            ["custom_text[submit][message]"] = "Includes the first $50 maintenance month. Renews at $50/month until canceled. Terms: " + provider.Origin + "/service-terms" };
        if (guest)
        {
            values["success_url"] = provider.Origin + "/purchase/complete/" + order.Id + "/{CHECKOUT_SESSION_ID}";
            values["cancel_url"] = provider.Origin + "/purchase/" + (S(snapshot, "plan") == "restaurant" ? "restaurant" : "business") + "?appStores=" + (order.AppStores ? "true" : "false") + "&notice=cancelled";
        }
        else values["customer_email"] = S(snapshot, "email") ?? throw Review();
        foreach (var prefix in new[] { "metadata", "subscription_data[metadata]" })
        { values[prefix + "[tide_purpose]"] = Purpose; values[prefix + "[tide_order_id]"] = order.Id; values[prefix + "[tide_tenant_id]"] = order.TenantId; }
        var index = 0;
        void Add(long amount, string label, bool recurring)
        {
            if (amount == 0) return; if (amount < 0 || amount > 90000) throw Review();
            var prefix = "line_items[" + index++ + "]";
            values[prefix + "[quantity]"] = "1"; values[prefix + "[price_data][currency]"] = "usd";
            values[prefix + "[price_data][unit_amount]"] = amount.ToString(CultureInfo.InvariantCulture);
            values[prefix + "[price_data][product_data][name]"] = label;
            if (recurring) values[prefix + "[price_data][recurring][interval]"] = "month";
        }
        Add(5000, "Tide Casa monthly maintenance", true); Add(N(quote, "setupCents"), "Tide Casa app setup — one time", false);
        if (order.AppStores) Add(N(quote, "storesCents"), "App-store submission support — one time; eligible stores, separate fees and approval apply", false);
        return values;
    }

    private static void Metadata(Order order, JsonElement value)
    {
        var metadata = P(value, "metadata");
        if (!Mode(order, value) || S(metadata, "tide_purpose") != Purpose || S(metadata, "tide_order_id") != order.Id || S(metadata, "tide_tenant_id") != order.TenantId) throw Review();
    }
    private static bool Mode(Order order, JsonElement value) => P(value, "livemode").ValueKind is JsonValueKind.True or JsonValueKind.False && B(value, "livemode") == (order.Environment == "live");
    private void ValidateSession(Order order, JsonElement session, bool requireMethods = false)
    {
        Metadata(order, session);
        if (!Id(S(session, "id"), "cs") || order.SessionId is not null && S(session, "id") != order.SessionId || S(session, "object") != "checkout.session"
            || S(session, "mode") != "subscription" || S(session, "client_reference_id") != order.Id || S(session, "currency") != "usd" || N(session, "amount_total") != order.Total) throw Review();
        if (requireMethods)
        {
            var methods = P(session, "payment_method_types");
            if (methods.ValueKind != JsonValueKind.Array || methods.GetArrayLength() != 1 || methods[0].GetString() != "card"
                || S(P(session, "payment_method_configuration_details"), "id") != provider.MethodConfiguration) throw new BillingException("The hosted payment method configuration needs review.", 422, "cards_unverified");
        }
    }

    private static string ProviderId(string? id, string prefix) => Id(id, prefix) ? id! : throw Review();
    private static async Task<Tenant> Owner(DbConnection db, DbTransaction tx, string tenant, AuthUser user, CancellationToken ct)
    {
        if (!Regex.IsMatch(tenant, "^[A-Za-z0-9_-]{1,128}$")) throw Missing();
        return (await Rows(db, tx, "SELECT id,name,email,status,vertical FROM bartide_customers WHERE id=@id AND (user_id=@user OR @platform=1)", r => new Tenant(r.GetString(0), r.GetString(1), r.GetString(2), r.GetString(3), r.GetString(4)), ct,
            ("@id", tenant), ("@user", user.UserId), ("@platform", user.IsPlatformOwner ? 1 : 0))).SingleOrDefault() ?? throw new BillingException("This billing workspace is not available to this account.", 403, "billing_forbidden");
    }
    private static Task<List<Order>> Orders(DbConnection db, DbTransaction? tx, string suffix, CancellationToken ct, params (string, object?)[] args) =>
        Rows(db, tx, "SELECT " + OrderColumns + " FROM tide_service_orders " + suffix, r => new Order(r.GetString(0), r.GetString(1), r.GetString(2), r.GetString(3), r.GetString(4), r.ReadInt64(5), r.ReadInt64(6), r.ReadInt64(7), r.ReadInt64(8) == 1,
            Nullable(r, 9), Nullable(r, 10), Nullable(r, 11), Nullable(r, 12), r.GetString(13), r.ReadInt64(14) == 1, r.IsDBNull(15) ? null : r.ReadInt64(15), Nullable(r, 16), r.ReadInt64(17), r.GetString(18)), ct, args);
    private static string? Nullable(DbDataReader reader, int index) => reader.IsDBNull(index) ? null : reader.GetString(index);
    private static DbCommand Command(DbConnection db, DbTransaction? tx, string sql, params (string Key, object? Value)[] args)
    { var command = db.CreateCommand(); command.Transaction = tx; command.CommandText = sql; foreach (var (key, value) in args) command.Parameters.AddWithValue(key, value ?? DBNull.Value); return command; }
    private static async Task<int> Run(DbConnection db, DbTransaction? tx, string sql, CancellationToken ct, params (string, object?)[] args)
    { await using var command = Command(db, tx, sql, args); return await command.ExecuteNonQueryAsync(ct); }
    private static async Task<long> Count(DbConnection db, DbTransaction? tx, string sql, CancellationToken ct, params (string, object?)[] args)
    { await using var command = Command(db, tx, sql, args); return Convert.ToInt64(await command.ExecuteScalarAsync(ct), CultureInfo.InvariantCulture); }
    private static async Task<List<T>> Rows<T>(DbConnection db, DbTransaction? tx, string sql, Func<DbDataReader, T> map, CancellationToken ct, params (string, object?)[] args)
    { await using var command = Command(db, tx, sql, args); await using var reader = await command.ExecuteReaderAsync(ct); var rows = new List<T>(); while (await reader.ReadAsync(ct)) rows.Add(map(reader)); return rows; }
}
