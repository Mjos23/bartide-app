using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Data.Common;
using TideCasa.Api.Infrastructure;
using TideCasa.Api.Features.RestaurantOrdering;
using TideCasa.Contracts;

namespace TideCasa.Api.Features.MerchantPayments;

public sealed record MerchantAccountBinding(string TenantId, string Slug, string Name, string AccountId);
public sealed record MerchantPhoneReservation(string AttemptId, RestaurantOrderReceipt Receipt);

public sealed partial class MerchantPaymentsStore(ApplicationDatabase database, MerchantPaymentOptions options,
    MerchantStripeProvider provider, RestaurantOrderingStore ordering)
{
    private sealed record Owner(string Id, string Slug, string Name, string Email);
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
        if (!options.OnboardingEnabled) return new(tenant, owner.Name, "disabled", true, false, false, binding?.Checked, "Restaurant card payments are being prepared. Pay staff remains available where enabled.");
        if (binding?.Account is { } account)
        {
            var checkedAt = Now(); var fresh = await provider.AccountAsync(account, tenant, ct);
            await SaveReadiness(tenant, fresh, checkedAt, ct);
            return new(tenant, owner.Name, fresh.State, true, true, fresh.Ready && options.CheckoutEnabled, checkedAt,
                fresh.Ready ? options.CheckoutEnabled ? "Your sandbox connection is ready for test card payments." : "Account setup is complete. Phone checkout still needs its separate verification checks." : "Continue Stripe setup to finish your restaurant’s payment details.");
        }
        return new(tenant, owner.Name, binding?.State ?? "unconnected", true, true, false, binding?.Checked,
            binding?.State == "review" ? "The previous setup attempt needs review before another account can be created." : "Connect your restaurant so its orders are paid directly into its own Stripe account.");
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
        var rows = await Rows(db, tx, "SELECT c.id,c.slug,c.name,c.email,COALESCE(c.user_id,''),COALESCE(x.settings_json,'{}') FROM bartide_customers c LEFT JOIN bartide_enhanced_configs x ON x.tenant_id=c.id WHERE c.id=@tenant AND c.status='active' AND c.vertical='bartide'", r => Enumerable.Range(0, 6).Select(r.GetString).ToArray(), ct, ("@tenant", tenant));
        if (rows.Count != 1) throw new MerchantFailure("This restaurant is unavailable.", 404, "merchant_missing");
        var row = rows[0]; using var config = JsonDocument.Parse(row[5]);
        if (!config.RootElement.TryGetProperty("enabled", out var enabled) || enabled.ValueKind != JsonValueKind.True) throw new MerchantFailure("Restaurant payments require an active workspace.", 403, "merchant_forbidden");
        if (row[4].Length == 0 && !user.IsPlatformOwner && row[3].Equals(user.Email.Trim(), StringComparison.OrdinalIgnoreCase))
        {
            await Run(db, tx, "UPDATE bartide_customers SET user_id=@user WHERE id=@id AND user_id IS NULL", ct, ("@user", user.UserId), ("@id", tenant)); row[4] = user.UserId;
        }
        if (!user.IsPlatformOwner && row[4] != user.UserId) throw new MerchantFailure("Business owner access is required.", 403, "merchant_forbidden");
        return new(row[0], row[1], row[2], row[3]);
    }
    private static string? NullString(DbDataReader reader, int index) => reader.IsDBNull(index) ? null : reader.GetString(index);
    internal static DbCommand Command(DbConnection db, DbTransaction tx, string sql, params (string Key, object? Value)[] args)
    { var cmd = db.CreateCommand(); cmd.Transaction = tx; cmd.CommandText = sql; foreach (var (key, value) in args) cmd.Parameters.AddWithValue(key, value ?? DBNull.Value); return cmd; }
    internal static async Task<int> Run(DbConnection db, DbTransaction tx, string sql, CancellationToken ct, params (string Key, object? Value)[] args)
    { await using var cmd = Command(db, tx, sql, args); return await cmd.ExecuteNonQueryAsync(ct); }
    internal static async Task<List<T>> Rows<T>(DbConnection db, DbTransaction tx, string sql, Func<DbDataReader, T> map, CancellationToken ct, params (string Key, object? Value)[] args)
    { await using var cmd = Command(db, tx, sql, args); await using var reader = await cmd.ExecuteReaderAsync(ct); var rows = new List<T>(); while (await reader.ReadAsync(ct)) rows.Add(map(reader)); return rows; }
}
