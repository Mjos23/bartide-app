using System.Globalization;
using System.Text.Json;
using TideCasa.Api.Infrastructure.Payments;
using TideCasa.Contracts;
using static TideCasa.Api.Infrastructure.Payments.StripeJson;

namespace TideCasa.Api.Features.MerchantPayments;

public sealed record MerchantAccountSnapshot(string AccountId, string State, bool Ready)
{
    public string ConnectionState { get; init; } = "unknown";
    public string CardPaymentsState { get; init; } = "unknown";
    public string PayoutsState { get; init; } = "unknown";
    public IReadOnlyList<string> RequirementCategories { get; init; } = [];
}
public sealed record MerchantAccountCreate(string TenantId, string Name, string Email);
public sealed record MerchantCheckoutLine(string Name, long AmountCents, long Quantity);
public sealed record MerchantCheckoutParameters(string TenantId, string Slug, string OrderId, string AttemptId,
    string RequestHash, string AccountId, int AmountCents, string SuccessUrl, string CancelUrl, long ExpiresAt,
    IReadOnlyList<MerchantCheckoutLine> Lines);
public sealed record MerchantPaymentSnapshot(string SessionId, string State, string? Url, string? IntentId, string? ChargeId,
    string ExpiresAt, IReadOnlyList<MerchantRefundSnapshot> Refunds);
public sealed record MerchantRefundSnapshot(string Id, string ChargeId, string IntentId, long AmountCents, string Status);

/// <summary>Maps the pinned Stripe wire contract; account context comes only from saved merchant bindings.</summary>
public sealed class MerchantStripeProvider : IDisposable
{
    private readonly MerchantPaymentOptions settings;
    private readonly StripeHttpTransport? transport;
    private StripeHttpTransport Client => transport ?? throw new MerchantFailure("Restaurant payments are not configured.", 503, "merchant_disabled");
    private static readonly string[] AccountIncludes = ["configuration.merchant", "defaults", "requirements", "identity"];
    public MerchantStripeProvider(MerchantPaymentOptions settings)
    {
        this.settings = settings;
        if (settings.Configured) transport = new(settings.Key, settings.ApiBase ?? StripeHttpTransport.ApiOrigin, settings.LocalTest);
    }
    public async Task<MerchantAccountSnapshot> CreateAccountAsync(MerchantAccountCreate saved, string key, CancellationToken ct)
    {
        settings.RequireOnboarding();
        var account = await Client.PostJsonAsync("/v2/core/accounts", new
        {
            contact_email = saved.Email, display_name = saved.Name, dashboard = "full", identity = new { country = "us" },
            configuration = new { merchant = new { capabilities = new { card_payments = new { requested = true } } } },
            defaults = new { responsibilities = new { fees_collector = "stripe", losses_collector = "stripe" } },
            metadata = new { tide_tenant_id = saved.TenantId, purpose = "tide_merchant" }, include = AccountIncludes
        }, "tide.merchant.account." + key, ct);
        return AccountSnapshot(account, saved.TenantId);
    }
    public async Task<MerchantAccountSnapshot> AccountAsync(string accountId, string tenant, CancellationToken ct)
    {
        if (!MerchantPaymentOptions.AccountId(accountId)) throw Mismatch();
        var query = string.Join("&", AccountIncludes.Select((value, i) => $"include%5B{i}%5D=" + Uri.EscapeDataString(value)));
        var account = await Client.GetAsync("/v2/core/accounts/" + accountId + "?" + query, null, ct);
        if (S(account, "id") != accountId) throw Mismatch();
        return AccountSnapshot(account, tenant);
    }
    private static MerchantAccountSnapshot AccountSnapshot(JsonElement account, string tenant)
    {
        var id = S(account, "id"); var metadata = P(account, "metadata");
        if (!MerchantPaymentOptions.AccountId(id) || S(account, "object") != "v2.core.account" || B(account, "livemode") != false
            || S(metadata, "tide_tenant_id") != tenant || S(metadata, "purpose") != "tide_merchant") throw Mismatch();
        var responsibilities = P(P(account, "defaults"), "responsibilities");
        var compatible = B(account, "closed") == false && S(account, "dashboard") == "full" && S(P(account, "identity"), "country")?.ToLowerInvariant() == "us"
            && S(responsibilities, "fees_collector") == "stripe" && S(responsibilities, "losses_collector") == "stripe";
        var merchant = P(P(account, "configuration"), "merchant"); var requirements = P(account, "requirements");
        var capabilities = P(merchant, "capabilities"); var cards = Capability(P(capabilities, "card_payments"));
        var payouts = Capability(P(P(capabilities, "stripe_balance"), "payouts"));
        var entries = P(requirements, "entries");
        var summary = P(requirements, "summary"); var minimumDeadline = P(summary, "minimum_deadline"); var deadline = S(minimumDeadline, "status");
        var requirementsKnown = requirements.ValueKind == JsonValueKind.Object && entries.ValueKind == JsonValueKind.Array
            && summary.ValueKind == JsonValueKind.Object && (minimumDeadline.ValueKind == JsonValueKind.Null || deadline is "past_due" or "currently_due" or "eventually_due")
            && entries.EnumerateArray().All(entry => S(entry, "awaiting_action_from") is "user" or "stripe");
        var waiting = requirementsKnown && entries.EnumerateArray().Any(entry => S(entry, "awaiting_action_from") == "stripe");
        var needsInput = requirementsKnown && entries.EnumerateArray().Any(entry => S(entry, "awaiting_action_from") == "user");
        var ready = compatible && B(merchant, "applied") == true && cards == "active" && requirementsKnown
            && (deadline is null or "eventually_due") && !waiting;
        var categories = new List<string>();
        if (deadline == "past_due") categories.Add("past_due");
        if (deadline == "currently_due" || needsInput) categories.Add("information_needed");
        if (deadline == "eventually_due") categories.Add("future_information");
        if (waiting) categories.Add("stripe_review");
        return new(id!, ready ? "ready" : compatible ? "onboarding" : "restricted", ready)
        {
            ConnectionState = !compatible ? "restricted" : !requirementsKnown || cards == "unknown" ? "unknown"
                : deadline is "past_due" or "currently_due" && !waiting || needsInput ? "needs_input"
                : waiting || cards == "pending" ? "pending_review" : ready ? "connected" : "needs_input",
            CardPaymentsState = cards, PayoutsState = payouts, RequirementCategories = categories
        };
    }
    private static string Capability(JsonElement capability) => S(capability, "status") is "active" or "pending" or "restricted" or "unsupported" or "inactive" ? S(capability, "status")! : "unknown";

    public async Task<MerchantEmbeddedSession> AccountSessionAsync(string accountId, CancellationToken ct)
    {
        if (!settings.EmbeddedOnboardingEnabled) throw new MerchantFailure("Embedded setup is not available. Continue with Stripe-hosted setup.", 503, "embedded_unavailable");
        if (!MerchantPaymentOptions.AccountId(accountId)) throw Mismatch();
        var session = await Client.PostFormAsync("/v1/account_sessions", new Dictionary<string, string>
        {
            ["account"] = accountId, ["components[account_onboarding][enabled]"] = "true", ["components[notification_banner][enabled]"] = "true"
        }, null, null, ct);
        var components = P(session, "components"); var secret = S(session, "client_secret");
        if (S(session, "object") != "account_session" || B(session, "livemode") != false || S(session, "account") != accountId
            || secret is not { Length: >= 16 and <= 1024 } || !secret.All(c => char.IsAsciiLetterOrDigit(c) || c == '_')
            || N(session, "expires_at") is not { } expiry || expiry <= DateTimeOffset.UtcNow.ToUnixTimeSeconds() || expiry > DateTimeOffset.UtcNow.AddDays(1).ToUnixTimeSeconds()
            || components.ValueKind != JsonValueKind.Object || B(P(components, "account_onboarding"), "enabled") != true || B(P(components, "notification_banner"), "enabled") != true
            || components.EnumerateObject().Any(c => c.Name is not ("account_onboarding" or "notification_banner") && B(c.Value, "enabled") != false)
            || components.EnumerateObject().Any(c => B(P(c.Value, "features"), "disable_stripe_user_authentication") == true)) throw Mismatch();
        return new(secret, settings.PublishableKey, expiry);
    }
    public async Task<string> OnboardingLinkAsync(string accountId, string tenant, string requestKey, CancellationToken ct)
    {
        settings.RequireOnboarding();
        if (!MerchantPaymentOptions.AccountId(accountId)) throw Mismatch();
        var path = settings.PublicBase + "/workspace/" + Uri.EscapeDataString(tenant) + "/payments";
        var link = await Client.PostJsonAsync("/v2/core/account_links", new
        {
            account = accountId, use_case = new { type = "account_onboarding", account_onboarding = new
            { configurations = new[] { "merchant" }, collection_options = new { fields = "eventually_due" }, return_url = path + "?returned=true", refresh_url = settings.PublicBase + "/merchant-payments/" + Uri.EscapeDataString(tenant) + "/refresh" } }
        }, "tide.merchant.link." + tenant + "." + requestKey, ct);
        if (S(link, "object") != "v2.core.account_link" || B(link, "livemode") != false || S(link, "account") != accountId || !settings.HostedUrl(S(link, "url"), true)) throw Mismatch();
        return S(link, "url")!;
    }
    public async Task<MerchantPaymentSnapshot> CreateCheckoutAsync(MerchantCheckoutParameters saved, CancellationToken ct)
    {
        settings.RequireCheckout();
        var fields = new Dictionary<string, string>
        {
            ["mode"] = "payment", ["client_reference_id"] = saved.OrderId, ["success_url"] = saved.SuccessUrl, ["cancel_url"] = saved.CancelUrl,
            ["expires_at"] = Number(saved.ExpiresAt), ["payment_intent_data[capture_method]"] = "automatic",
            ["wallet_options[link][display]"] = "never", ["allowed_payment_method_types[0]"] = "card"
        };
        foreach (var pair in Metadata(saved))
        { fields["metadata[" + pair.Key + "]"] = pair.Value; fields["payment_intent_data[metadata][" + pair.Key + "]"] = pair.Value; }
        for (var i = 0; i < saved.Lines.Count; i++)
        {
            var line = saved.Lines[i]; var prefix = "line_items[" + Number(i) + "]";
            fields[prefix + "[quantity]"] = Number(line.Quantity); fields[prefix + "[price_data][currency]"] = "usd";
            fields[prefix + "[price_data][unit_amount]"] = Number(line.AmountCents); fields[prefix + "[price_data][product_data][name]"] = line.Name;
        }
        var session = await Client.PostFormAsync("/v1/checkout/sessions", fields, saved.AccountId, "tide.restaurant.checkout." + saved.AttemptId, ct);
        if (B(session, "livemode") != false || S(session, "object") != "checkout.session" || !Matches(P(session, "metadata"), saved)) throw Mismatch();
        // Replayed creation responses may still say open. Always retrieve current state.
        return await CheckoutAsync(saved, RequireId(session, "cs"), ct);
    }
    public async Task<MerchantPaymentSnapshot> CheckoutAsync(MerchantCheckoutParameters saved, string sessionId, CancellationToken ct)
    {
        if (!Id(sessionId, "cs")) throw Mismatch();
        var session = await Client.GetAsync("/v1/checkout/sessions/" + sessionId, saved.AccountId, ct);
        if (S(session, "id") != sessionId) throw Mismatch();
        return await InspectAsync(saved, session, ct);
    }
    public async Task<MerchantPaymentSnapshot> ExpireAsync(MerchantCheckoutParameters saved, string sessionId, CancellationToken ct)
    {
        var current = await CheckoutAsync(saved, sessionId, ct);
        if (current.State != "open") return current;
        var session = await Client.PostFormAsync("/v1/checkout/sessions/" + sessionId + "/expire", [], saved.AccountId, "tide.restaurant.expire." + saved.AttemptId, ct);
        if (S(session, "id") != sessionId) throw Mismatch();
        return await InspectAsync(saved, session, ct);
    }
    private async Task<MerchantPaymentSnapshot> InspectAsync(MerchantCheckoutParameters expected, JsonElement session, CancellationToken ct)
    {
        var sessionId = RequireId(session, "cs"); var intentId = ObjectId(session, "payment_intent");
        if (B(session, "livemode") != false || S(session, "object") != "checkout.session" || S(session, "mode") != "payment"
            || S(session, "client_reference_id") != expected.OrderId || S(session, "currency") != "usd"
            || N(session, "amount_total") != expected.AmountCents || !Matches(P(session, "metadata"), expected)
            || N(session, "expires_at") is not { } expiresAt || expiresAt is < 1 or > 253402300799
            || S(session, "status") is null || S(session, "payment_status") is null || intentId is not null && !Id(intentId, "pi")) throw Mismatch();
        var expires = DateTimeOffset.FromUnixTimeSeconds(expiresAt).ToString("O");
        if (S(session, "status") == "open" && S(session, "payment_status") == "unpaid")
        {
            if (!settings.HostedUrl(S(session, "url"), false)) throw Mismatch();
            return new(sessionId, "open", S(session, "url"), intentId, null, expires, []);
        }
        if (S(session, "status") == "expired" && S(session, "payment_status") == "unpaid") return new(sessionId, "expired", null, intentId, null, expires, []);
        if (S(session, "status") != "complete" || S(session, "payment_status") != "paid" || intentId is null) return new(sessionId, "review", null, intentId, null, expires, []);
        var intent = await Client.GetAsync("/v1/payment_intents/" + intentId + "?expand%5B0%5D=latest_charge", expected.AccountId, ct);
        var charge = P(intent, "latest_charge"); var chargeId = RequireId(charge, "ch");
        if (B(intent, "livemode") != false || S(intent, "object") != "payment_intent" || S(intent, "id") != intentId || S(intent, "status") != "succeeded"
            || N(intent, "amount") != expected.AmountCents || N(intent, "amount_received") != expected.AmountCents || S(intent, "currency") != "usd"
            || !Matches(P(intent, "metadata"), expected) || !Empty(intent, "application_fee_amount") || !Empty(intent, "transfer_data")) throw Mismatch();
        var method = P(charge, "payment_method_details");
        if (B(charge, "livemode") != false || S(charge, "object") != "charge" || S(charge, "status") != "succeeded" || B(charge, "paid") != true || B(charge, "captured") != true
            || N(charge, "amount") != expected.AmountCents || N(charge, "amount_captured") != expected.AmountCents || S(charge, "currency") != "usd"
            || ObjectId(charge, "payment_intent") != intentId || S(method, "type") != "card" || P(method, "card").ValueKind != JsonValueKind.Object
            || !Empty(P(method, "card"), "wallet") || !Empty(charge, "application_fee_amount") || !Empty(charge, "transfer_data")) throw Mismatch();
        var refunds = new List<MerchantRefundSnapshot>(); var seen = new HashSet<string>(StringComparer.Ordinal); string? cursor = null;
        for (;;)
        {
            var list = await Client.GetAsync("/v1/refunds?charge=" + chargeId + "&limit=100" + (cursor is null ? "" : "&starting_after=" + cursor), expected.AccountId, ct);
            if (S(list, "object") != "list" || P(list, "data").ValueKind != JsonValueKind.Array || B(list, "has_more") is not { } hasMore) throw Mismatch();
            var pageCount = 0;
            foreach (var refund in P(list, "data").EnumerateArray())
            {
                var refundId = RequireId(refund, "re"); var status = S(refund, "status");
                if (refunds.Count >= 100 || !seen.Add(refundId) || S(refund, "object") != "refund" || ObjectId(refund, "charge") != chargeId || ObjectId(refund, "payment_intent") != intentId
                    || S(refund, "currency") != "usd" || N(refund, "amount") is not { } amount || amount <= 0 || status is not ("succeeded" or "pending" or "failed" or "canceled" or "requires_action")) throw Mismatch();
                refunds.Add(new(refundId, chargeId, intentId, amount, status)); cursor = refundId; pageCount++;
            }
            if (!hasMore) break;
            if (pageCount == 0 || refunds.Count >= 100) throw Mismatch();
        }
        if (N(charge, "amount_refunded") is not { } refunded || refunded < 0 || refunds.Where(r => r.Status == "succeeded").Sum(r => r.AmountCents) != refunded || refunded > expected.AmountCents) throw Mismatch();
        return new(sessionId, "paid", null, intentId, chargeId, expires, refunds);
    }
    private static string RequireId(JsonElement value, string prefix) => Id(S(value, "id"), prefix) ? S(value, "id")! : throw Mismatch();
    private static string Number(long value) => value.ToString(CultureInfo.InvariantCulture);
    private static Dictionary<string, string> Metadata(MerchantCheckoutParameters p) => new()
    { ["purpose"] = MerchantPaymentOptions.Purpose, ["tide_tenant_id"] = p.TenantId, ["tide_order_id"] = p.OrderId, ["tide_attempt_id"] = p.AttemptId, ["tide_request_hash"] = p.RequestHash };
    private static bool Matches(JsonElement metadata, MerchantCheckoutParameters expected) => metadata.ValueKind == JsonValueKind.Object && Metadata(expected).All(pair => S(metadata, pair.Key) == pair.Value);
    private static MerchantFailure Mismatch() => new("This payment connection needs review. No payment has been marked complete.", 409, "merchant_review");
    public void Dispose() => transport?.Dispose();
}
