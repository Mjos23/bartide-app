using System.Text.Json;
using Stripe;
using Stripe.Checkout;
using V2 = Stripe.V2.Core;

namespace TideCasa.Api.Features.MerchantPayments;

public sealed record MerchantAccountSnapshot(string AccountId, string State, bool Ready);
public sealed record MerchantAccountCreate(string TenantId, string Name, string Email);
public sealed record MerchantCheckoutLine(string Name, long AmountCents, long Quantity);
public sealed record MerchantCheckoutParameters(string TenantId, string Slug, string OrderId, string AttemptId,
    string RequestHash, string AccountId, int AmountCents, string SuccessUrl, string CancelUrl, long ExpiresAt,
    IReadOnlyList<MerchantCheckoutLine> Lines);
public sealed record MerchantPaymentSnapshot(string SessionId, string State, string? Url, string? IntentId, string? ChargeId,
    string ExpiresAt, IReadOnlyList<MerchantRefundSnapshot> Refunds);
public sealed record MerchantRefundSnapshot(string Id, string ChargeId, string IntentId, long AmountCents, string Status);

/// <summary>The provider boundary always uses the pinned official SDK and a saved merchant account context.</summary>
public sealed class MerchantStripeProvider(MerchantPaymentOptions settings)
{
    private StripeClient? client;
    public StripeClient Client => client ??= settings.Configured ? settings.CreateClient() : throw new MerchantFailure("Restaurant payments are not configured.", 503, "merchant_disabled");
    public async Task<MerchantAccountSnapshot> CreateAccountAsync(MerchantAccountCreate saved, string key, CancellationToken ct)
    {
        settings.RequireOnboarding();
        var account = await Client.V2.Core.Accounts.CreateAsync(new()
        {
            ContactEmail = saved.Email, DisplayName = saved.Name, Dashboard = "full",
            Identity = new() { Country = "us" },
            Configuration = new() { Merchant = new() { Capabilities = new() { CardPayments = new() { Requested = true } } } },
            Defaults = new() { Responsibilities = new() { FeesCollector = "stripe", LossesCollector = "stripe" } },
            Metadata = new() { ["tide_tenant_id"] = saved.TenantId, ["purpose"] = "tide_merchant" },
            Include = ["configuration.merchant", "defaults", "requirements", "identity"]
        }, new() { IdempotencyKey = "tide.merchant.account." + key }, ct);
        return AccountSnapshot(account, saved.TenantId);
    }
    public async Task<MerchantAccountSnapshot> AccountAsync(string accountId, string tenant, CancellationToken ct)
    {
        var account = await Client.V2.Core.Accounts.GetAsync(accountId,
            new() { Include = ["configuration.merchant", "defaults", "requirements", "identity"] }, cancellationToken: ct);
        if (account.Id != accountId) throw Mismatch();
        return AccountSnapshot(account, tenant);
    }
    private static MerchantAccountSnapshot AccountSnapshot(V2.Account account, string tenant)
    {
        if (!MerchantPaymentOptions.AccountId(account.Id) || account.Livemode || account.Metadata?.GetValueOrDefault("tide_tenant_id") != tenant || account.Metadata?.GetValueOrDefault("purpose") != "tide_merchant") throw Mismatch();
        var compatible = account.Closed == false && account.Dashboard == "full" && account.Identity?.Country?.ToLowerInvariant() == "us"
            && account.Defaults?.Responsibilities?.FeesCollector == "stripe" && account.Defaults?.Responsibilities?.LossesCollector == "stripe";
        var merchant = account.Configuration?.Merchant;
        var requirements = account.Requirements;
        var ready = compatible && merchant?.Applied == true
            && merchant.Capabilities?.CardPayments?.Status == "active" && requirements is not null
            && requirements.Summary?.MinimumDeadline?.Status is not ("past_due" or "currently_due");
        return new(account.Id, ready ? "ready" : compatible ? "onboarding" : "restricted", ready);
    }
    public async Task<string> OnboardingLinkAsync(string accountId, string tenant, string requestKey, CancellationToken ct)
    {
        settings.RequireOnboarding();
        var path = settings.PublicBase + "/workspace/" + Uri.EscapeDataString(tenant) + "/payments";
        var link = await Client.V2.Core.AccountLinks.CreateAsync(new()
        {
            Account = accountId, UseCase = new() { Type = "account_onboarding", AccountOnboarding = new()
            { Configurations = ["merchant"], CollectionOptions = new() { Fields = "eventually_due" }, ReturnUrl = path + "?returned=true", RefreshUrl = settings.PublicBase + "/merchant-payments/" + Uri.EscapeDataString(tenant) + "/refresh" } }
        }, new() { IdempotencyKey = "tide.merchant.link." + tenant + "." + requestKey }, ct);
        if (link.Livemode || link.Account != accountId || !settings.HostedUrl(link.Url, true)) throw Mismatch();
        return link.Url;
    }
    public async Task<MerchantPaymentSnapshot> CreateCheckoutAsync(MerchantCheckoutParameters saved, CancellationToken ct)
    {
        settings.RequireCheckout();
        var metadata = Metadata(saved);
        var session = await new SessionService(Client).CreateAsync(new()
        {
            Mode = "payment", ClientReferenceId = saved.OrderId, SuccessUrl = saved.SuccessUrl, CancelUrl = saved.CancelUrl,
            ExpiresAt = DateTimeOffset.FromUnixTimeSeconds(saved.ExpiresAt).UtcDateTime, Metadata = metadata,
            PaymentIntentData = new() { Metadata = metadata, CaptureMethod = "automatic" },
            LineItems = saved.Lines.Select(line => new SessionLineItemOptions { Quantity = line.Quantity, PriceData = new()
            { Currency = "usd", UnitAmount = line.AmountCents, ProductData = new() { Name = line.Name } } }).ToList(),
            WalletOptions = new() { Link = new() { Display = "never" } },
            ExtraParams = new Dictionary<string, object> { ["allowed_payment_method_types"] = new[] { "card" } }
        }, Request(saved.AccountId, "tide.restaurant.checkout." + saved.AttemptId), ct);
        // A replayed POST response can describe the originally open Session.
        // Re-read its current state before deciding whether payment completed.
        if (session.Livemode || !Matches(session.Metadata, saved)) throw Mismatch();
        return await CheckoutAsync(saved, session.Id, ct);
    }
    public async Task<MerchantPaymentSnapshot> CheckoutAsync(MerchantCheckoutParameters saved, string sessionId, CancellationToken ct)
    {
        var session = await new SessionService(Client).GetAsync(sessionId, requestOptions: Request(saved.AccountId), cancellationToken: ct);
        if (session.Id != sessionId) throw Mismatch();
        return await InspectAsync(saved, session, ct);
    }
    public async Task<MerchantPaymentSnapshot> ExpireAsync(MerchantCheckoutParameters saved, string sessionId, CancellationToken ct)
    {
        var current = await CheckoutAsync(saved, sessionId, ct);
        if (current.State != "open") return current;
        var session = await new SessionService(Client).ExpireAsync(sessionId, requestOptions: Request(saved.AccountId, "tide.restaurant.expire." + saved.AttemptId), cancellationToken: ct);
        return await InspectAsync(saved, session, ct);
    }
    private async Task<MerchantPaymentSnapshot> InspectAsync(MerchantCheckoutParameters expected, Session session, CancellationToken ct)
    {
        if (session.Livemode || session.Mode != "payment" || session.ClientReferenceId != expected.OrderId || session.Currency != "usd"
            || session.AmountTotal != expected.AmountCents || !Matches(session.Metadata, expected)) throw Mismatch();
        var expires = new DateTimeOffset(DateTime.SpecifyKind(session.ExpiresAt, DateTimeKind.Utc)).ToString("O");
        if (session.Status == "open" && session.PaymentStatus == "unpaid")
        {
            if (!settings.HostedUrl(session.Url, false)) throw Mismatch();
            return new(session.Id, "open", session.Url, session.PaymentIntentId, null, expires, []);
        }
        if (session.Status == "expired" && session.PaymentStatus == "unpaid") return new(session.Id, "expired", null, session.PaymentIntentId, null, expires, []);
        if (session.Status != "complete" || session.PaymentStatus != "paid" || string.IsNullOrEmpty(session.PaymentIntentId)) return new(session.Id, "review", null, session.PaymentIntentId, null, expires, []);
        var intent = await new PaymentIntentService(Client).GetAsync(session.PaymentIntentId, new() { Expand = ["latest_charge"] }, Request(expected.AccountId), ct);
        if (intent.Livemode || intent.Id != session.PaymentIntentId || intent.Status != "succeeded" || intent.Amount != expected.AmountCents || intent.AmountReceived != expected.AmountCents
            || intent.Currency != "usd" || !Matches(intent.Metadata, expected) || intent.ApplicationFeeAmount is not null || intent.TransferData is not null || intent.LatestCharge is not { } charge) throw Mismatch();
        if (charge.Livemode || charge.Status != "succeeded" || !charge.Paid || !charge.Captured || charge.Amount != expected.AmountCents || charge.AmountCaptured != expected.AmountCents || charge.Currency != "usd"
            || charge.PaymentIntentId != intent.Id || charge.PaymentMethodDetails?.Type != "card" || charge.PaymentMethodDetails.Card?.Wallet is not null || charge.ApplicationFeeAmount is not null || charge.TransferData is not null) throw Mismatch();
        var refunds = new List<MerchantRefundSnapshot>();
        await foreach (var refund in new RefundService(Client).ListAutoPagingAsync(new() { Charge = charge.Id, Limit = 100 }, Request(expected.AccountId), ct))
        {
            if (refunds.Count >= 100 || refund.ChargeId != charge.Id || refund.PaymentIntentId != intent.Id || refund.Currency != "usd" || refund.Amount <= 0 || refund.Status is not ("succeeded" or "pending" or "failed" or "canceled" or "requires_action")) throw Mismatch();
            refunds.Add(new(refund.Id, charge.Id, intent.Id, refund.Amount, refund.Status));
        }
        if (refunds.Where(r => r.Status == "succeeded").Sum(r => r.AmountCents) != charge.AmountRefunded || charge.AmountRefunded > expected.AmountCents) throw Mismatch();
        return new(session.Id, "paid", null, intent.Id, charge.Id, expires, refunds);
    }
    private static Dictionary<string, string> Metadata(MerchantCheckoutParameters p) => new()
    { ["purpose"] = MerchantPaymentOptions.Purpose, ["tide_tenant_id"] = p.TenantId, ["tide_order_id"] = p.OrderId, ["tide_attempt_id"] = p.AttemptId, ["tide_request_hash"] = p.RequestHash };
    private static bool Matches(Dictionary<string, string>? metadata, MerchantCheckoutParameters expected) => metadata is not null && Metadata(expected).All(pair => metadata.GetValueOrDefault(pair.Key) == pair.Value);
    private static RequestOptions Request(string account, string? key = null) => new() { StripeAccount = account, IdempotencyKey = key };
    private static MerchantFailure Mismatch() => new("This payment connection needs review. No payment has been marked complete.", 409, "merchant_review");
}
