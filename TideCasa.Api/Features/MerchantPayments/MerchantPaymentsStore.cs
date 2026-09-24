using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Data.Common;
using TideCasa.Api.Infrastructure;
using TideCasa.Api.Infrastructure.Payments;
using TideCasa.Api.Features.RestaurantOrdering;
using TideCasa.Contracts;

namespace TideCasa.Api.Features.MerchantPayments;

public sealed record MerchantAccountBinding(string TenantId, string Slug, string Name, string AccountId);
public sealed record MerchantPhoneReservation(string AttemptId, RestaurantOrderReceipt Receipt);

public sealed partial class MerchantPaymentsStore(ApplicationDatabase database, MerchantPaymentOptions options,
    MerchantStripeProvider provider, RestaurantOrderingStore ordering)
{
    private sealed record Owner(string Id, string Slug, string Name, string Email, bool Active);
    internal static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    internal static string Now() => DateTimeOffset.UtcNow.ToString("O");
    internal static string Hash(string value) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(value)));
    private static string Key(string? value) => Guid.TryParseExact(value, "D", out _) ? value! : throw new MerchantFailure("Refresh this form and try again.");

    public async Task<MerchantPaymentStatus> StatusAsync(string tenant, AuthUser user, CancellationToken ct)
    {
        Owner owner; (string? Account, string State, string? Checked)? binding;
        await using (var db = await database.OpenAsync(ct))
        {
            using var tx = db.BeginTransaction(deferred: false); owner = await Authorize(db, tx, tenant, user, ct);
            binding = (await Rows(db, tx, "SELECT account_id,state,checked_at FROM tide_merchant_accounts WHERE tenant_id=@tenant AND environment='test'", r => ((string?)NullString(r, 0), r.GetString(1), NullString(r, 2)), ct, ("@tenant", tenant))).Cast<(string?, string, string?)?>().SingleOrDefault(); tx.Commit();
        }
        MerchantPaymentStatus Summary(string state, string message, string action, MerchantAccountSnapshot? snapshot = null, string? checkedAt = null, bool stale = false) =>
            new(tenant, owner.Name, state, true, options.OnboardingEnabled, owner.Active && !stale && snapshot?.Ready == true && options.CheckoutEnabled, checkedAt ?? binding?.Checked, message)
            {
                ConnectionState = snapshot?.ConnectionState ?? state, CardPaymentsState = snapshot?.CardPaymentsState ?? "unknown", PayoutsState = snapshot?.PayoutsState ?? "unknown",
                CheckoutState = !options.CheckoutEnabled ? "disabled" : !owner.Active ? "awaiting_launch" : !stale && snapshot?.Ready == true ? "ready_for_test" : "blocked",
                NextAction = action, Stale = stale, RequirementCategories = snapshot?.RequirementCategories ?? [], BusinessEmail = owner.Email,
                DashboardUrl = binding?.Account is { } saved && MerchantPaymentOptions.AccountId(saved) ? "https://dashboard.stripe.com/" + saved : null,
                TestOrderUrl = owner.Active && !stale && snapshot?.Ready == true && options.CheckoutEnabled ? options.PublicBase + "/order/" + Uri.EscapeDataString(owner.Slug) : null,
                EmbeddedOnboardingAvailable = !stale && snapshot is not null && snapshot.State != "restricted" && options.EmbeddedOnboardingEnabled
            };
        if (!options.OnboardingEnabled) return Summary("disabled", "Restaurant card payments are being prepared. Pay staff remains available where enabled.", "unavailable");
        if (binding?.Account is { } account)
        {
            try
            {
                var checkedAt = Now(); var fresh = await provider.AccountAsync(account, tenant, ct);
                await SaveReadiness(tenant, fresh, checkedAt, ct);
                var action = fresh.ConnectionState switch
                {
                    "restricted" => "review_connection", "unknown" => "refresh", "needs_input" => "continue_setup", "pending_review" => "wait_for_review",
                    _ => fresh.PayoutsState == "pending" ? "wait_for_review" : fresh.PayoutsState != "active" ? "continue_setup" : !owner.Active ? "prepare_launch" : options.CheckoutEnabled ? "test_order" : "verify_checkout"
                };
                var message = action switch
                {
                    "test_order" => "Your sandbox connection is ready for a test order. No real charges are collected.",
                    "prepare_launch" => "Your sandbox Stripe connection is prepared. Finish business setup and launch review before guest checkout becomes available.",
                    "verify_checkout" => "Your Stripe connection is ready. BarTide phone checkout still needs its separate verification checks.",
                    "wait_for_review" => "Stripe is reviewing your information. Refresh later to check card acceptance and payouts separately.",
                    "review_connection" => "This saved connection needs review. Open your Stripe Dashboard or contact Tide Casa support.",
                    "refresh" => "Stripe returned incomplete readiness information. Refresh before testing payments.",
                    _ => "Continue Stripe setup to resolve the remaining requirements for this saved account."
                };
                return Summary(fresh.State, message, action, fresh, checkedAt);
            }
            catch (StripeTransportException)
            { return Summary("unavailable", "Stripe could not be reached. Your connection is saved; refresh to verify its current status.", "refresh", stale: true); }
            catch (MerchantFailure error) when (error.Code == "merchant_review")
            { return Summary("restricted", "This saved connection could not be verified. Contact Tide Casa support before testing payments.", "review_connection", stale: true); }
        }
        var state = binding?.State ?? "unconnected";
        return Summary(state, state == "review" ? "The previous setup attempt needs review before another account can be created."
            : state == "creating" ? "Your connection is being created. Continue to safely resume the saved attempt."
            : "Confirm your saved business details, then connect your restaurant to receive its own guest payments.", state == "review" ? "review_connection" : state == "creating" ? "continue_setup" : "start_setup");
    }

    public async Task<MerchantEmbeddedSession> EmbeddedSessionAsync(string tenant, AuthUser user, StartMerchantOnboardingRequest request, CancellationToken ct)
    {
        if (request is null || !request.ConfirmUsBusiness) throw new MerchantFailure("Confirm the saved business details before continuing.");
        _ = Key(request.RequestKey); string? account;
        await using (var db = await database.OpenAsync(ct))
        {
            using var tx = db.BeginTransaction(deferred: false); _ = await Authorize(db, tx, tenant, user, ct);
            account = (await Rows(db, tx, "SELECT account_id FROM tide_merchant_accounts WHERE tenant_id=@tenant AND environment='test'", r => NullString(r, 0), ct, ("@tenant", tenant))).SingleOrDefault(); tx.Commit();
        }
        if (!options.EmbeddedOnboardingEnabled) throw new MerchantFailure("Embedded setup is not available. Use Stripe-hosted setup.", 503, "embedded_unavailable");
        if (account is null) throw new MerchantFailure("Start Stripe setup first to save this restaurant’s connection.", 409, "merchant_connection_required");
        var snapshot = await provider.AccountAsync(account, tenant, ct);
        if (snapshot.State == "restricted") throw new MerchantFailure("This connection needs review before setup can continue.", 409, "merchant_review");
        return await provider.AccountSessionAsync(account, ct);
    }

    public async Task<MerchantHostedLink> OnboardAsync(string tenant, AuthUser user, StartMerchantOnboardingRequest request, CancellationToken ct)
    {
        options.RequireOnboarding();
        if (request is null || !request.ConfirmUsBusiness) throw new MerchantFailure("Confirm that this is a US business before continuing.");
        var requestKey = Key(request.RequestKey); var lease = Guid.NewGuid().ToString("D");
        string? accountId; string createKey, createJson; bool create;
        await using (var db = await database.OpenAsync(ct))
        {
            using var tx = db.BeginTransaction(deferred: false); var owner = await Authorize(db, tx, tenant, user, ct);
            var rows = await Rows(db, tx, "SELECT account_id,request_key,request_json,state,created_at,lease_until FROM tide_merchant_accounts WHERE tenant_id=@tenant AND environment='test'", r => new[] { NullString(r, 0), r.GetString(1), r.GetString(2), r.GetString(3), r.GetString(4), NullString(r, 5) }, ct, ("@tenant", tenant));
            if (rows.Count == 0)
            {
                accountId = null; createKey = requestKey; createJson = JsonSerializer.Serialize(new MerchantAccountCreate(tenant, owner.Name, owner.Email), Json);
                await Run(db, tx, "INSERT INTO tide_merchant_accounts(tenant_id,environment,request_key,request_json,state,lease_key,lease_until,created_at,updated_at) VALUES(@tenant,'test',@key,@json,'creating',@lease,@until,@now,@now)", ct,
                    ("@tenant", tenant), ("@key", createKey), ("@json", createJson), ("@lease", lease), ("@until", DateTimeOffset.UtcNow.AddMinutes(2).ToString("O")), ("@now", Now()));
            }
            else
            {
                var row = rows[0]; accountId = row[0]; createKey = row[1]!; createJson = row[2]!;
                if (accountId is null)
                {
                    if (row[3] == "review" || DateTimeOffset.Parse(row[4]!, CultureInfo.InvariantCulture) < DateTimeOffset.UtcNow.AddHours(-23)) throw new MerchantFailure("This account setup attempt needs review before it can be retried.", 409, "merchant_review");
                    if (row[5] is { } until && DateTimeOffset.Parse(until, CultureInfo.InvariantCulture) > DateTimeOffset.UtcNow) throw new MerchantFailure("Account setup is already in progress. Please try again shortly.", 409, "merchant_pending");
                    await Run(db, tx, "UPDATE tide_merchant_accounts SET lease_key=@lease,lease_until=@until,updated_at=@now WHERE tenant_id=@tenant AND environment='test'", ct,
                        ("@lease", lease), ("@until", DateTimeOffset.UtcNow.AddMinutes(2).ToString("O")), ("@now", Now()), ("@tenant", tenant));
                }
            }
            create = accountId is null; tx.Commit();
        }
        if (create)
        {
            try
            {
                var snapshot = await provider.CreateAccountAsync(JsonSerializer.Deserialize<MerchantAccountCreate>(createJson, Json)!, createKey, ct);
                await using var db = await database.OpenAsync(ct); using var tx = db.BeginTransaction(deferred: false);
                if (await Run(db, tx, "UPDATE tide_merchant_accounts SET account_id=@account,state=@state,checked_at=@now,version=version+1,lease_key=NULL,lease_until=NULL,updated_at=@now WHERE tenant_id=@tenant AND environment='test' AND account_id IS NULL AND lease_key=@lease", ct,
                    ("@account", snapshot.AccountId), ("@state", snapshot.State), ("@now", Now()), ("@tenant", tenant), ("@lease", lease)) != 1) throw new MerchantFailure("Account setup is being reconciled. Try again shortly.", 409, "merchant_pending");
                tx.Commit(); accountId = snapshot.AccountId;
            }
            catch
            {
                // Retain original parameters/key after an uncertain provider result.
                await ReleaseAccountLease(tenant, lease, CancellationToken.None); throw;
            }
        }
        _ = await provider.AccountAsync(accountId!, tenant, ct);
        return new(await provider.OnboardingLinkAsync(accountId!, tenant, requestKey, ct));
    }

    public async Task<MerchantAccountBinding> ReadyAsync(string slug, CancellationToken ct)
    {
        options.RequireCheckout();
        MerchantAccountBinding binding;
        await using (var db = await database.OpenAsync(ct))
        {
            using var tx = db.BeginTransaction(deferred: true);
            binding = (await Rows(db, tx, "SELECT c.id,c.slug,c.name,a.account_id FROM bartide_customers c JOIN tide_merchant_accounts a ON a.tenant_id=c.id AND a.environment='test' WHERE c.slug=@slug AND c.status='active' AND c.vertical='bartide' AND a.account_id IS NOT NULL", r => new MerchantAccountBinding(r.GetString(0), r.GetString(1), r.GetString(2), r.GetString(3)), ct, ("@slug", slug))).SingleOrDefault()
                ?? throw new MerchantFailure("This restaurant has not connected phone payments.", 409, "phone_unavailable"); tx.Commit();
        }
        var checkedAt = Now(); var current = await provider.AccountAsync(binding.AccountId, binding.TenantId, ct);
        await SaveReadiness(binding.TenantId, current, checkedAt, ct);
        if (!current.Ready) throw new MerchantFailure("The restaurant’s card connection is not ready. Choose pay staff.", 409, "phone_unavailable");
        return binding;
    }

    private async Task SaveReadiness(string tenant, MerchantAccountSnapshot snapshot, string checkedAt, CancellationToken ct)
    {
        await using var db = await database.OpenAsync(ct); using var tx = db.BeginTransaction(deferred: false);
        await Run(db, tx, "UPDATE tide_merchant_accounts SET state=@state,checked_at=@checked,updated_at=@now,version=version+1 WHERE tenant_id=@tenant AND environment='test' AND account_id=@account AND (checked_at IS NULL OR checked_at<=@checked)", ct,
            ("@state", snapshot.State), ("@checked", checkedAt), ("@now", Now()), ("@tenant", tenant), ("@account", snapshot.AccountId)); tx.Commit();
    }
    private async Task ReleaseAccountLease(string tenant, string lease, CancellationToken ct)
    {
        await using var db = await database.OpenAsync(ct); using var tx = db.BeginTransaction(deferred: false);
        await Run(db, tx, "UPDATE tide_merchant_accounts SET lease_key=NULL,lease_until=NULL WHERE tenant_id=@tenant AND environment='test' AND lease_key=@lease", ct, ("@tenant", tenant), ("@lease", lease)); tx.Commit();
    }
    private static async Task<Owner> Authorize(DbConnection db, DbTransaction tx, string tenant, AuthUser user, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(user.UserId) || string.IsNullOrWhiteSpace(user.Email)) throw new MerchantFailure("Sign in again.", 401, "sign_in");
        var rows = await Rows(db, tx, "SELECT c.id,c.slug,c.name,c.email,COALESCE(c.user_id,''),COALESCE(x.settings_json,'{}'),c.status FROM bartide_customers c LEFT JOIN bartide_enhanced_configs x ON x.tenant_id=c.id WHERE c.id=@tenant AND c.status IN ('active','draft','building') AND c.vertical='bartide'", r => Enumerable.Range(0, 7).Select(r.GetString).ToArray(), ct, ("@tenant", tenant));
        if (rows.Count != 1) throw new MerchantFailure("This restaurant is unavailable.", 404, "merchant_missing");
        var row = rows[0]; using var config = JsonDocument.Parse(row[5]);
        if (row[6] == "active" && (!config.RootElement.TryGetProperty("enabled", out var enabled) || enabled.ValueKind != JsonValueKind.True)) throw new MerchantFailure("Restaurant payments require an active workspace.", 403, "merchant_forbidden");
        if (row[4].Length == 0 && !user.IsPlatformOwner && row[3].Equals(user.Email.Trim(), StringComparison.OrdinalIgnoreCase))
        {
            await Run(db, tx, "UPDATE bartide_customers SET user_id=@user WHERE id=@id AND user_id IS NULL", ct, ("@user", user.UserId), ("@id", tenant)); row[4] = user.UserId;
        }
        if (!user.IsPlatformOwner && row[4] != user.UserId) throw new MerchantFailure("Business owner access is required.", 403, "merchant_forbidden");
        return new(row[0], row[1], row[2], row[3], row[6] == "active");
    }
    private static string? NullString(DbDataReader reader, int index) => reader.IsDBNull(index) ? null : reader.GetString(index);
    internal static DbCommand Command(DbConnection db, DbTransaction tx, string sql, params (string Key, object? Value)[] args)
    { var cmd = db.CreateCommand(); cmd.Transaction = tx; cmd.CommandText = sql; foreach (var (key, value) in args) cmd.Parameters.AddWithValue(key, value ?? DBNull.Value); return cmd; }
    internal static async Task<int> Run(DbConnection db, DbTransaction tx, string sql, CancellationToken ct, params (string Key, object? Value)[] args)
    { await using var cmd = Command(db, tx, sql, args); return await cmd.ExecuteNonQueryAsync(ct); }
    internal static async Task<List<T>> Rows<T>(DbConnection db, DbTransaction tx, string sql, Func<DbDataReader, T> map, CancellationToken ct, params (string Key, object? Value)[] args)
    { await using var cmd = Command(db, tx, sql, args); await using var reader = await cmd.ExecuteReaderAsync(ct); var rows = new List<T>(); while (await reader.ReadAsync(ct)) rows.Add(map(reader)); return rows; }
}
