using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using TideCasa.Api.Features.Authentication;
using TideCasa.Api.Infrastructure;
using TideCasa.Contracts;
using static TideCasa.Api.Features.ServiceBilling.ServiceBillingProvider;

namespace TideCasa.Api.Features.ServiceBilling;

public sealed partial class ServiceBillingStore
{
    public async Task<ServiceBillingActionResult> DiscardGuestAsync(GuestPurchaseReset request, CancellationToken ct)
    {
        if (request.CheckoutKey is null || !Regex.IsMatch(request.CheckoutKey, "^[a-f0-9]{64}$")) throw Missing();
        var hash = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(request.CheckoutKey)));
        Order? order;
        await using (var db = await database.OpenAsync(ct))
            order = (await Orders(db, null, "WHERE tenant_id=@tenant AND environment=@environment AND status NOT IN('expired','failed')", ct, ("@tenant", "purchase-" + hash[..32]), ("@environment", provider.Environment))).SingleOrDefault();
        if (order is null) return new("No open purchase remains.");
        using var snapshot = JsonDocument.Parse(order.RequestJson);
        if (!B(snapshot.RootElement, "guest") || S(snapshot.RootElement, "checkoutHash") != hash) throw Missing();
        if (order.Status == "paid" || order.SessionId is null) throw new BillingException("This purchase must be reviewed before changing options.", 409, "checkout_review");
        await provider.CheckAccountAsync(ct);
        var session = await provider.GetAsync("/v1/checkout/sessions/" + ProviderId(order.SessionId, "cs"), ct); ValidateSession(order, session);
        if (S(session, "status") == "complete" || S(session, "payment_status") == "paid") throw new BillingException("Payment is already being confirmed.", 409, "payment_pending");
        if (S(session, "status") == "open") await provider.PostAsync("/v1/checkout/sessions/" + order.SessionId + "/expire", new Dictionary<string, string>(), "tide-dotnet:" + order.Id + ":expire", ct);
        session = await provider.GetAsync("/v1/checkout/sessions/" + order.SessionId, ct); ValidateSession(order, session);
        if (S(session, "status") != "expired" || S(session, "payment_status") == "paid") throw Changed();
        await using (var db = await database.OpenAsync(ct))
        {
            using var tx = db.BeginTransaction(deferred: false);
            if (await Run(db, tx, "UPDATE tide_service_orders SET status='expired',subscription_revision=subscription_revision+1,updated_at=@now WHERE id=@id AND subscription_revision=@revision AND status<>'paid'", ct, ("@now", Now()), ("@id", order.Id), ("@revision", order.Revision)) != 1) throw Changed();
            await tx.CommitAsync(ct);
        }
        return new("Unpaid checkout closed.");
    }
    // A pending purchase has no account owner. The immutable order snapshot marks
    // it as claimable; ordinary existing workspaces can never use this path.
    public async Task<GuestCheckoutLink> GuestCheckoutAsync(GuestCheckoutRequest request, CancellationToken ct)
    {
        if (!provider.GuestCheckoutReady) throw Unavailable();
        if (request.Plan is not ("business" or "restaurant") || request.CheckoutKey is null || !Regex.IsMatch(request.CheckoutKey, "^[a-f0-9]{64}$")) throw Missing();
        if (!request.AcceptedTerms || request.TermsVersion != TermsVersion) throw new BillingException("Review the first payment and monthly renewal.", 400, "terms_required");
        await provider.CheckMethodsAsync(ct);
        var hash = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(request.CheckoutKey)));
        var tenant = "purchase-" + hash[..32];
        Order order;
        await using (var db = await database.OpenAsync(ct))
        {
            using var tx = db.BeginTransaction(deferred: false);
            var existing = (await Orders(db, tx, "WHERE tenant_id=@tenant AND environment=@environment AND status NOT IN('expired','failed')", ct, ("@tenant", tenant), ("@environment", provider.Environment))).SingleOrDefault();
            if (existing is not null)
            {
                using var doc = JsonDocument.Parse(existing.RequestJson);
                if (!B(doc.RootElement, "guest") || S(doc.RootElement, "checkoutHash") != hash) throw Missing();
                if (existing.Status == "paid" && existing.SessionId is not null)
                    return new(existing.Id, "/purchase/complete/" + existing.Id + "/" + existing.SessionId, existing.Environment, true);
                if (S(doc.RootElement, "termsVersion") != TermsVersion)
                    throw new BillingException("Pricing has changed. Close this unpaid checkout and review the current terms.", 409, "guest_options_saved");
                // Changing options must first close the old unpaid Stripe session.
                if (existing.AppStores != request.AppStores || S(doc.RootElement, "plan") != request.Plan)
                    throw new BillingException("A checkout is already open. Return to its saved options or close it before choosing again.", 409, "guest_options_saved");
                order = existing;
            }
            else
            {
                var now = Now(); var id = Guid.NewGuid().ToString("D");
                var vertical = request.Plan == "restaurant" ? "bartide" : "tide-casa";
                var name = request.Plan == "restaurant" ? "New BarTide app purchase" : "New Tide Casa app purchase";
                var menu = JsonSerializer.Serialize(new { schema = "bartide-menu/1", venue = new { name, vertical, currency = "USD", area = "", website_url = "", tagline = "", hours_text = "", service_note = "" }, categories = new[] { new { id = vertical == "bartide" ? "food" : "services", label = vertical == "bartide" ? "Food" : "Services" } }, items = Array.Empty<object>() });
                await Run(db, tx, "INSERT INTO bartide_customers(id,slug,email,user_id,name,menu_json,version,status,enrollment_note,vertical,requested_plan,created_at,updated_at) VALUES(@id,@id,@email,NULL,@name,@menu,0,'draft','Awaiting payment and buyer details',@vertical,'enhanced',@now,@now) ON CONFLICT(id) DO NOTHING", ct,
                    ("@id", tenant), ("@email", hash + "@purchase.invalid"), ("@name", name), ("@menu", menu), ("@vertical", vertical), ("@now", now));
                if (await Count(db, tx, "SELECT COUNT(*) FROM bartide_customers WHERE id=@id AND user_id IS NULL AND email=@email AND status='draft'", ct, ("@id", tenant), ("@email", hash + "@purchase.invalid")) != 1) throw Review();
                var initial = 60000 + (request.AppStores ? 30000 : 0);
                var snapshot = JsonSerializer.Serialize(new { guest = true, checkoutHash = hash, plan = request.Plan, origin = provider.Origin, expires = DateTimeOffset.UtcNow.AddHours(23).ToUnixTimeSeconds(), termsVersion = TermsVersion, consentedAt = now, appStores = request.AppStores,
                    methodConfigurationId = provider.MethodConfiguration, quote = new { setupCents = 60000, storesCents = request.AppStores ? 30000 : 0, initialCents = initial, monthlyCents = MonthlyCents, maintenanceDelayDays = MaintenanceDelayDays, firstCents = initial, discountCents = 0 } });
                await Run(db, tx, "INSERT INTO tide_service_orders(id,tenant_id,environment,request_json,initial_cents,monthly_cents,total_cents,app_stores,created_at,updated_at) VALUES(@id,@tenant,@environment,@request,@initial,@monthly,@total,@stores,@now,@now)", ct,
                    ("@id", id), ("@tenant", tenant), ("@environment", provider.Environment), ("@request", snapshot), ("@initial", initial), ("@total", initial), ("@monthly", MonthlyCents), ("@stores", request.AppStores ? 1 : 0), ("@now", now));
                order = (await Orders(db, tx, "WHERE id=@id", ct, ("@id", id))).Single();
            }
            await tx.CommitAsync(ct);
        }
        JsonElement session;
        if (order.SessionId is not null) session = await provider.GetAsync("/v1/checkout/sessions/" + ProviderId(order.SessionId, "cs"), ct);
        else
        {
            if (!DateTimeOffset.TryParse(order.CreatedAt, out var created) || created < DateTimeOffset.UtcNow.AddHours(-23)) throw new BillingException("This purchase needs review before retrying.", 409, "checkout_review");
            session = await provider.PostAsync("/v1/checkout/sessions", CheckoutParameters(order), "tide-dotnet:" + order.Id + ":checkout", ct);
            ValidateSession(order, session, requireMethods: true);
            await using var db = await database.OpenAsync(ct); using var tx = db.BeginTransaction(deferred: false);
            await Run(db, tx, "UPDATE tide_service_orders SET session_id=@session,updated_at=@now WHERE id=@id AND session_id IS NULL", ct, ("@session", S(session, "id")), ("@now", Now()), ("@id", order.Id));
            order = (await Orders(db, tx, "WHERE id=@id", ct, ("@id", order.Id))).Single();
            if (order.SessionId != S(session, "id")) throw Review();
            await tx.CommitAsync(ct);
        }
        ValidateSession(order, session, requireMethods: true);
        if (S(session, "status") == "complete") return new(order.Id, "/purchase/complete/" + order.Id + "/" + S(session, "id"), order.Environment, true);
        if (S(session, "status") != "open") throw new BillingException("The saved checkout has expired. Please refresh after its status updates.", 409, "checkout_closed");
        return new(order.Id, HostedUrl(S(session, "url"), "checkout.stripe.com") ?? throw Unavailable(), order.Environment);
    }

    private async Task<Order> GuestOrder(string orderId, string sessionId, CancellationToken ct)
    {
        if (!Guid.TryParseExact(orderId, "D", out _) || !Id(sessionId, "cs")) throw Missing();
        await using var db = await database.OpenAsync(ct);
        var order = (await Orders(db, null, "WHERE id=@id AND session_id=@session AND environment=@environment", ct, ("@id", orderId), ("@session", sessionId), ("@environment", provider.Environment))).SingleOrDefault() ?? throw Missing();
        using var doc = JsonDocument.Parse(order.RequestJson);
        if (!B(doc.RootElement, "guest")) throw Missing();
        return order;
    }

    public async Task<GuestPurchaseStatus> GuestStatusAsync(string orderId, string sessionId, CancellationToken ct)
    {
        var order = await GuestOrder(orderId, sessionId, ct);
        using var snapshot = JsonDocument.Parse(order.RequestJson);
        // A redirect or a browser-supplied status never marks a payment successful.
        return new(order.Status, S(snapshot.RootElement, "plan")!, order.AppStores, order.Total, order.Environment);
    }

    public async Task<WorkspaceRegistration> ClaimGuestAsync(string orderId, string sessionId, GuestPurchaseClaim request, AuthUser user, CancellationToken ct)
    {
        static string Field(string? value)
        {
            if (string.IsNullOrWhiteSpace(value) || value.Length > 120 || value.Any(char.IsControl)) throw new BillingException("Enter your business name, contact name and location.", 400, "details_required");
            return value.Trim();
        }
        var name = Field(request.BusinessName); var contact = Field(request.ContactName); var area = Field(request.Area);
        var order = await GuestOrder(orderId, sessionId, ct);
        if (order.Status != "paid") throw new BillingException("Payment confirmation is still pending.", 409, "payment_pending");
        await provider.CheckAccountAsync(ct);
        var session = await provider.GetAsync("/v1/checkout/sessions/" + ProviderId(sessionId, "cs"), ct);
        ValidateSession(order, session);
        var buyerEmail = S(P(session, "customer_details"), "email");
        if (S(session, "status") != "complete" || S(session, "payment_status") != "paid" || !SupabaseAuthClient.ValidEmail(buyerEmail)
            || !string.Equals(buyerEmail, user.Email, StringComparison.OrdinalIgnoreCase)) throw new BillingException("Sign in with the verified email used at checkout to connect this purchase.", 403, "buyer_email_required");
        await using var db = await database.OpenAsync(ct); using var tx = db.BeginTransaction(deferred: false);
        var tenant = (await Rows(db, tx, "SELECT user_id,slug,menu_json,email FROM bartide_customers WHERE id=@id", r => (User: Nullable(r, 0), Slug: r.GetString(1), Menu: r.GetString(2), Email: r.GetString(3)), ct, ("@id", order.TenantId))).Single();
        if (tenant.User == user.UserId) return new(order.TenantId, tenant.Slug, false);
        if (tenant.User is not null) throw Missing();
        using var saved = JsonDocument.Parse(order.RequestJson);
        if (tenant.Email != S(saved.RootElement, "checkoutHash") + "@purchase.invalid") throw Review();
        if (await Count(db, tx, db.Sql("SELECT COUNT(*) FROM bartide_customers WHERE id<>@id AND (user_id=@user OR email=@email COLLATE NOCASE)", "SELECT COUNT(*) FROM bartide_customers WHERE id<>@id AND (user_id=@user OR lower(email)=lower(@email))"), ct,
            ("@id", order.TenantId), ("@user", user.UserId), ("@email", user.Email)) != 0) throw new BillingException("Your purchase is saved. Contact hello@tide.casa to connect it to your existing workspace.", 409, "existing_workspace");
        var menu = System.Text.Json.Nodes.JsonNode.Parse(tenant.Menu) ?? throw Review();
        menu["venue"]!["name"] = name; menu["venue"]!["area"] = area;
        if (await Run(db, tx, "UPDATE bartide_customers SET user_id=@user,email=@email,name=@name,contact_name=@contact,menu_json=@menu,version=version+1,updated_at=@now WHERE id=@id AND user_id IS NULL", ct,
            ("@user", user.UserId), ("@email", user.Email.ToLowerInvariant()), ("@name", name), ("@contact", contact), ("@menu", menu.ToJsonString()), ("@now", Now()), ("@id", order.TenantId)) != 1) throw Changed();
        await tx.CommitAsync(ct);
        return new(order.TenantId, tenant.Slug, true);
    }
}
