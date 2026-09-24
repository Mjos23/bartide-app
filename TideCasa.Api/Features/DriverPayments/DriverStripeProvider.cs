using System.Globalization;
using System.Text.Json;
using TideCasa.Api.Features.RestaurantOrdering;
using TideCasa.Api.Infrastructure.Payments;
using static TideCasa.Api.Infrastructure.Payments.StripeJson;

namespace TideCasa.Api.Features.DriverPayments;

public sealed record DriverRecipientCreate(string UserId, string Name, string Email);
public sealed record DriverRecipient(string AccountId, bool Ready);
public sealed record DriverChargeRequest(string PaymentId, string AttemptId, string TenantId, string DriverUserId,
    string AccountId, int DriverPayCents, int FeeCents, int TotalCents, string ReturnPath, long ExpiresAt, int ReturnPage = 0);
public sealed record DriverChargeSnapshot(string SessionId, string State, string? Url);

/// <summary>Platform destination charges: driver allocation is proven from Stripe's transfer and fee records.</summary>
public sealed class DriverStripeProvider : IDisposable
{
    private readonly DriverPaymentOptions settings;
    private readonly StripeHttpTransport? transport;
    private StripeHttpTransport Client => transport ?? throw new OrderingException("Driver payments are not configured.", 503, "driver_payments_disabled");
    private static readonly string[] AccountIncludes = ["configuration.recipient", "defaults", "requirements", "identity"];
    public DriverStripeProvider(DriverPaymentOptions settings)
    {
        this.settings = settings;
        if (settings.Configured) transport = new(settings.Key, settings.ApiBase ?? StripeHttpTransport.ApiOrigin, settings.LocalTest);
    }

    public async Task<DriverRecipient> CreateRecipientAsync(DriverRecipientCreate saved, string key, CancellationToken ct)
    {
        settings.RequireSetup();
        var account = await Client.PostJsonAsync("/v2/core/accounts", new
        {
            contact_email = saved.Email, display_name = saved.Name, dashboard = "express",
            identity = new { country = "us", entity_type = "individual" },
            configuration = new { recipient = new { capabilities = new { stripe_balance = new { stripe_transfers = new { requested = true } } } } },
            defaults = new { responsibilities = new { fees_collector = "application", losses_collector = "application" } },
            metadata = new { tide_driver_user_id = saved.UserId, purpose = DriverPaymentOptions.Purpose }, include = AccountIncludes
        }, "tide.driver.recipient." + key, ct);
        return RecipientSnapshot(account, saved.UserId);
    }

    public async Task<DriverRecipient> RecipientAsync(string account, string user, CancellationToken ct)
    {
        if (!DriverPaymentOptions.AccountId(account)) throw Mismatch();
        var query = string.Join("&", AccountIncludes.Select((value, i) => $"include%5B{i}%5D=" + Uri.EscapeDataString(value)));
        var result = await Client.GetAsync("/v2/core/accounts/" + account + "?" + query, null, ct);
        if (S(result, "id") != account) throw Mismatch();
        return RecipientSnapshot(result, user);
    }

    private DriverRecipient RecipientSnapshot(JsonElement account, string user)
    {
        var id = S(account, "id"); var metadata = P(account, "metadata");
        if (!DriverPaymentOptions.AccountId(id) || S(account, "object") != "v2.core.account" || !Mode(account)
            || S(metadata, "tide_driver_user_id") != user || S(metadata, "purpose") != DriverPaymentOptions.Purpose) throw Mismatch();
        var identity = P(account, "identity"); var responsibilities = P(P(account, "defaults"), "responsibilities");
        var recipient = P(P(account, "configuration"), "recipient");
        var balance = P(P(recipient, "capabilities"), "stripe_balance");
        var transfers = P(balance, "stripe_transfers"); var payouts = P(balance, "payouts");
        var requirements = P(account, "requirements"); var entries = P(requirements, "entries");
        var summary = P(requirements, "summary"); var deadline = P(summary, "minimum_deadline");
        var requirementsKnown = entries.ValueKind == JsonValueKind.Array && summary.ValueKind == JsonValueKind.Object
            && (deadline.ValueKind == JsonValueKind.Null || S(deadline, "status") is "past_due" or "currently_due" or "eventually_due")
            && entries.EnumerateArray().All(e => S(e, "awaiting_action_from") is "user" or "stripe");
        var ready = B(account, "closed") == false && S(account, "dashboard") == "express"
            && S(identity, "country")?.ToLowerInvariant() == "us" && S(identity, "entity_type") == "individual"
            && S(responsibilities, "fees_collector") == "application" && S(responsibilities, "losses_collector") == "application"
            && B(recipient, "applied") == true && S(transfers, "status") == "active" && S(payouts, "status") == "active" && requirementsKnown
            && (deadline.ValueKind == JsonValueKind.Null || S(deadline, "status") == "eventually_due")
            && !entries.EnumerateArray().Any(e => S(e, "awaiting_action_from") == "stripe");
        return new(id!, ready);
    }

    public async Task<string> OnboardingLinkAsync(string account, string user, string key, CancellationToken ct)
    {
        settings.RequireSetup();
        await RecipientAsync(account, user, ct);
        var link = await Client.PostJsonAsync("/v2/core/account_links", new
        {
            account, use_case = new { type = "account_onboarding", account_onboarding = new
            {
                configurations = new[] { "recipient" }, collection_options = new { fields = "eventually_due" },
                return_url = settings.PublicBase + "/driver/earnings?payment_setup=returned",
                refresh_url = settings.PublicBase + "/driver/earnings?payment_setup=refresh"
            } }
        }, "tide.driver.link." + key, ct);
        if (S(link, "object") != "v2.core.account_link" || !Mode(link) || S(link, "account") != account || !settings.HostedUrl(S(link, "url"), true)) throw Mismatch();
        return S(link, "url")!;
    }

    public async Task<DriverChargeSnapshot> CreateCheckoutAsync(DriverChargeRequest saved, CancellationToken ct)
    {
        settings.RequirePayments(); Validate(saved);
        if (!(await RecipientAsync(saved.AccountId, saved.DriverUserId, ct)).Ready)
            throw new OrderingException("The driver must finish payment setup before receiving this payment.", 409, "driver_payout_not_ready");
        var fields = new Dictionary<string, string>
        {
            ["mode"] = "payment", ["client_reference_id"] = saved.PaymentId,
            ["success_url"] = ReturnUrl(saved, true), ["cancel_url"] = ReturnUrl(saved, false),
            ["expires_at"] = Number(saved.ExpiresAt), ["payment_intent_data[capture_method]"] = "automatic",
            ["payment_intent_data[transfer_data][destination]"] = saved.AccountId,
            ["allowed_payment_method_types[0]"] = "card", ["wallet_options[link][display]"] = "never",
            ["line_items[0][quantity]"] = "1", ["line_items[0][price_data][currency]"] = "usd",
            ["line_items[0][price_data][unit_amount]"] = Number(saved.DriverPayCents),
            ["line_items[0][price_data][product_data][name]"] = "Completed delivery driver pay"
        };
        if (saved.FeeCents > 0)
        {
            fields["payment_intent_data[application_fee_amount]"] = Number(saved.FeeCents);
            fields["line_items[1][quantity]"] = "1"; fields["line_items[1][price_data][currency]"] = "usd";
            fields["line_items[1][price_data][unit_amount]"] = Number(saved.FeeCents);
            fields["line_items[1][price_data][product_data][name]"] = "BarTide network delivery fee (5%)";
        }
        foreach (var pair in Metadata(saved))
        { fields["metadata[" + pair.Key + "]"] = pair.Value; fields["payment_intent_data[metadata][" + pair.Key + "]"] = pair.Value; }
        // Every retry uses the durable attempt ID and exactly the same saved amount and expiry.
        var session = await Client.PostFormAsync("/v1/checkout/sessions", fields, null, "tide.driver.checkout." + saved.AttemptId, ct);
        if (!Mode(session) || S(session, "object") != "checkout.session" || !Matches(P(session, "metadata"), saved)) throw Mismatch();
        return await CheckoutAsync(saved, RequireId(session, "cs"), ct);
    }

    public async Task<DriverChargeSnapshot> CheckoutAsync(DriverChargeRequest saved, string sessionId, CancellationToken ct)
    {
        Validate(saved);
        if (!Id(sessionId, "cs")) throw Mismatch();
        var session = await Client.GetAsync("/v1/checkout/sessions/" + sessionId, null, ct);
        if (S(session, "id") != sessionId || S(session, "object") != "checkout.session" || !Mode(session)
            || S(session, "mode") != "payment" || S(session, "client_reference_id") != saved.PaymentId
            || S(session, "currency") != "usd" || N(session, "amount_total") != saved.TotalCents
            || N(session, "expires_at") != saved.ExpiresAt || !Matches(P(session, "metadata"), saved)
            || S(session, "success_url") != ReturnUrl(saved, true) || S(session, "cancel_url") != ReturnUrl(saved, false)
            || !Cards(P(session, "payment_method_types"))) throw Mismatch();
        var status = S(session, "status"); var paymentStatus = S(session, "payment_status");
        if (status == "open" && paymentStatus == "unpaid")
        {
            if (!settings.HostedUrl(S(session, "url"), false)) throw Mismatch();
            return new(sessionId, "open", S(session, "url"));
        }
        if (status == "expired" && paymentStatus == "unpaid") return new(sessionId, "expired", null);
        var intentId = ObjectId(session, "payment_intent");
        if (status != "complete" || paymentStatus != "paid") return new(sessionId, "review", null);
        if (!Id(intentId, "pi")) throw Mismatch();
        var intent = await Client.GetAsync("/v1/payment_intents/" + intentId + "?expand%5B0%5D=latest_charge", null, ct);
        if (S(intent, "id") != intentId || S(intent, "object") != "payment_intent" || !Mode(intent)
            || S(intent, "status") != "succeeded" || N(intent, "amount") != saved.TotalCents || N(intent, "amount_received") != saved.TotalCents
            || S(intent, "currency") != "usd" || !Matches(P(intent, "metadata"), saved) || !Cards(P(intent, "payment_method_types"))
            || !FeeAmount(intent, saved.FeeCents) || !Destination(intent, saved) || !Empty(intent, "on_behalf_of")) throw Mismatch();
        var charge = P(intent, "latest_charge"); var chargeId = RequireId(charge, "ch");
        if (S(charge, "object") != "charge" || !Mode(charge) || S(charge, "status") != "succeeded"
            || B(charge, "paid") != true || B(charge, "captured") != true || ObjectId(charge, "payment_intent") != intentId
            || S(charge, "currency") != "usd" || N(charge, "amount") != saved.TotalCents || N(charge, "amount_captured") != saved.TotalCents
            || !FeeAmount(charge, saved.FeeCents) || !Destination(charge, saved) || !Empty(charge, "on_behalf_of")
            || S(P(charge, "payment_method_details"), "type") != "card" || P(P(charge, "payment_method_details"), "card").ValueKind != JsonValueKind.Object
            || B(charge, "disputed") is not { } disputed || N(charge, "amount_refunded") is not { } refunded || refunded < 0 || refunded > saved.TotalCents
            || B(charge, "refunded") is not { } fullyRefunded || fullyRefunded != (refunded == saved.TotalCents)) throw Mismatch();
        if (disputed) return new(sessionId, "disputed", null);
        if (refunded > 0) return new(sessionId, "refunded", null);
        var transferId = ObjectId(charge, "transfer");
        if (!Id(transferId, "tr")) throw Mismatch();
        var transfer = await Client.GetAsync("/v1/transfers/" + transferId, null, ct);
        var destinationPayment = ObjectId(transfer, "destination_payment");
        if (S(transfer, "object") != "transfer" || S(transfer, "id") != transferId || !Mode(transfer)
            || ObjectId(transfer, "destination") != saved.AccountId || ObjectId(transfer, "source_transaction") != chargeId
            || N(transfer, "amount") != saved.TotalCents || S(transfer, "currency") != "usd"
            || !Id(destinationPayment, "py") || B(transfer, "reversed") != false || N(transfer, "amount_reversed") != 0) throw Mismatch();
        var feeId = ObjectId(charge, "application_fee"); long actualFee = 0;
        if (saved.FeeCents > 0)
        {
            if (!Id(feeId, "fee")) throw Mismatch();
            var fee = await Client.GetAsync("/v1/application_fees/" + feeId, null, ct);
            if (S(fee, "id") != feeId || S(fee, "object") != "application_fee" || !Mode(fee)
                || ObjectId(fee, "account") != saved.AccountId || ObjectId(fee, "charge") != destinationPayment
                || ObjectId(fee, "originating_transaction") != chargeId || S(fee, "currency") != "usd"
                || N(fee, "amount") != saved.FeeCents || N(fee, "amount_refunded") != 0 || B(fee, "refunded") != false) throw Mismatch();
            actualFee = N(fee, "amount")!.Value;
        }
        else if (!Empty(charge, "application_fee")) throw Mismatch();
        if (N(transfer, "amount") - actualFee != saved.DriverPayCents) throw Mismatch();
        return new(sessionId, "paid", null);
    }

    private static void Validate(DriverChargeRequest p)
    {
        if (!DriverPaymentOptions.AccountId(p.AccountId) || p.DriverPayCents is < 50 or > 1000000 || p.FeeCents < 0
            || p.FeeCents != 0 && p.FeeCents != (p.DriverPayCents * 5L + 50) / 100
            || p.TotalCents != (long)p.DriverPayCents + p.FeeCents || p.ExpiresAt is < 1 or > 253402300799
            || p.ReturnPage is < 0 or > 100000 || p.ReturnPath != "/workspace/" + Uri.EscapeDataString(p.TenantId) + "/driver-payments"
            || new[] { p.PaymentId, p.AttemptId, p.TenantId, p.DriverUserId }.Any(v => string.IsNullOrWhiteSpace(v) || v.Length > 180 || v.Any(char.IsControl))) throw Mismatch();
    }
    private bool Mode(JsonElement value) => B(value, "livemode") == !settings.Sandbox;
    private string ReturnUrl(DriverChargeRequest p, bool success) => settings.PublicBase + p.ReturnPath
        + (success ? "?payment_returned=true" : "?payment_canceled=true") + (p.ReturnPage > 0 ? "&page=" + Number(p.ReturnPage) : "");
    private static bool Cards(JsonElement values) => values.ValueKind == JsonValueKind.Array && values.GetArrayLength() == 1
        && values[0].ValueKind == JsonValueKind.String && values[0].GetString() == "card";
    private static bool FeeAmount(JsonElement value, int amount) => N(value, "application_fee_amount") == amount || amount == 0 && Empty(value, "application_fee_amount");
    private static bool Destination(JsonElement value, DriverChargeRequest p) => ObjectId(P(value, "transfer_data"), "destination") == p.AccountId
        && (Empty(P(value, "transfer_data"), "amount") || N(P(value, "transfer_data"), "amount") == p.TotalCents);
    private static string RequireId(JsonElement value, string prefix) => Id(S(value, "id"), prefix) ? S(value, "id")! : throw Mismatch();
    private static string Number(long value) => value.ToString(CultureInfo.InvariantCulture);
    private static Dictionary<string, string> Metadata(DriverChargeRequest p) => new()
    {
        ["purpose"] = DriverPaymentOptions.Purpose, ["tide_payment_id"] = p.PaymentId, ["tide_attempt_id"] = p.AttemptId,
        ["tide_tenant_id"] = p.TenantId, ["tide_driver_user_id"] = p.DriverUserId,
        ["driver_pay_cents"] = Number(p.DriverPayCents), ["platform_fee_cents"] = Number(p.FeeCents)
    };
    private static bool Matches(JsonElement metadata, DriverChargeRequest p) => metadata.ValueKind == JsonValueKind.Object
        && Metadata(p).All(pair => S(metadata, pair.Key) == pair.Value);
    private static OrderingException Mismatch() => new("This driver payment needs review. Payment completion could not be confirmed.", 409, "payment_review");
    public void Dispose() => transport?.Dispose();
}
