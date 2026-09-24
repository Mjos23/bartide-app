using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using Npgsql;
using TideCasa.Api.Features.Authentication;

namespace TideCasa.Api.Features.PublicDemo;

/// <summary>Fail-closed configuration for the separately deployed fictional presentation app.</summary>
public sealed class PublicDemoOptions
{
    public const string Tenant = "gulf-lantern";
    public const string Schema = "tide_demo_gulf_lantern";
    public bool Enabled { get; }
    private readonly Dictionary<string, VerifiedIdentity> people = new(StringComparer.Ordinal);
    private readonly Dictionary<string, VerifiedIdentity> identities = new(StringComparer.Ordinal);

    public PublicDemoOptions(IConfiguration configuration, IHostEnvironment environment)
    {
        Enabled = configuration.GetValue<bool>("PublicDemo:Enabled");
        if (!Enabled) return;
        Require(string.Equals(configuration["Storage:Provider"], "PostgreSql", StringComparison.OrdinalIgnoreCase)
            && configuration["Storage:PostgresSchema"] == Schema, "dedicated PostgreSQL schema");
        NpgsqlConnectionStringBuilder database;
        try { database = new(configuration.GetConnectionString("Application")); }
        catch { throw Invalid("dedicated database login"); }
        Require(Regex.IsMatch(database.Username ?? "", "^tide_demo_api(?:\\.[a-z0-9]{20})?$", RegexOptions.CultureInvariant), "dedicated database login");
        Require(configuration["Media:Provider"] == "demo-bundled", "immutable bundled media");

        foreach (var key in new[] { "Auth:Enabled", "Auth:AllowLocalTestProvider", "ServiceBilling:LiveEnabled",
            "ServiceBilling:CheckoutEnabled", "ServiceBilling:GuestCheckoutEnabled", "ServiceBilling:AllowLocalTestProvider",
            "ServiceBilling:CardsOnlyVerified", "Stripe:CheckoutEnabled", "Stripe:InvoicesEnabled",
            "MerchantPayments:OnboardingEnabled", "MerchantPayments:CheckoutEnabled", "MerchantPayments:CardsOnlyVerified",
            "MerchantPayments:AllowLocalTestProvider", "WebPush:Enabled", "WebPush:AllowDevelopmentLoopback", "Notifications:AllowLocalProvider" })
            Require(!configuration.GetValue<bool>(key), "disabled external services");
        foreach (var key in new[] { "Auth:SupabaseUrl", "Auth:PublishableKey", "Auth:PlatformOwnerUserId",
            "ServiceBilling:RestrictedKey", "ServiceBilling:WebhookSecret", "ServiceBilling:AccountId", "ServiceBilling:DevelopmentApiBase",
            "Stripe:SecretKey", "Stripe:PublishableKey", "Stripe:WebhookSecret", "Stripe:RestrictedKey",
            "MerchantPayments:RestrictedKey", "MerchantPayments:PlatformAccountId", "MerchantPayments:ConnectWebhookSecret",
            "MerchantPayments:AccountWebhookSecret", "MerchantPayments:ApiBaseUrl", "Notifications:ResendApiKey", "Notifications:LocalEndpoint",
            "WebPush:VapidPublicKey", "WebPush:VapidPrivateKey", "WebPush:VapidSubject", "WebPush:DevelopmentPushOrigin",
            "Media:R2:AccountId", "Media:R2:AccessKeyId", "Media:R2:SecretAccessKey" })
            Require(string.IsNullOrWhiteSpace(configuration[key]), "no external provider credentials");
        Require((configuration["Notifications:Mode"] ?? "disabled") == "disabled"
            && (configuration["ServiceBilling:Environment"] ?? "sandbox") == "sandbox"
            && (configuration["Stripe:Mode"] ?? "sandbox") == "sandbox", "disabled sending and live payments");

        var path = configuration["SampleBar:FixturePath"];
        Require(!string.IsNullOrWhiteSpace(path), "fictional people fixture");
        try
        {
            var file = new FileInfo(Path.GetFullPath(path!, environment.ContentRootPath));
            Require(file.Exists && file.Length is > 0 and <= 131072, "bounded people fixture");
            using var json = JsonDocument.Parse(File.ReadAllBytes(file.FullName), new JsonDocumentOptions { MaxDepth = 16 });
            var venue = json.RootElement;
            Require(venue.GetProperty("id").GetString() == Tenant && venue.GetProperty("slug").GetString() == Tenant
                && venue.GetProperty("fictional").GetBoolean(), "fictional Gulf Lantern venue");
            var rows = venue.GetProperty("people");
            Require(rows.ValueKind == JsonValueKind.Array && rows.GetArrayLength() == People.Count, "twelve fixed demo people");
            foreach (var row in rows.EnumerateArray())
            {
                var key = row.GetProperty("key").GetString() ?? "";
                Require(People.TryGetValue(key, out var expected), "known fictional person");
                var id = row.GetProperty("id").GetString();
                var email = row.GetProperty("email").GetString();
                var name = row.GetProperty("name").GetString();
                Require(id == expected.Id && row.GetProperty("role").GetString() == expected.Role
                    && email == key + "@gulf-lantern.example.invalid"
                    && name is { Length: > 0 and <= 100 } && !name.Any(char.IsControl), "fixed fictional identity");
                var identity = new VerifiedIdentity(id!, email!, name);
                Require(people.TryAdd(key, identity) && identities.TryAdd(id!, identity), "unique fictional identity");
            }
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException or KeyNotFoundException or ArgumentException)
        { throw Invalid("readable fictional people fixture"); }
    }

    public VerifiedIdentity? Person(string? key) => Enabled && key is not null && people.TryGetValue(key, out var person) ? person : null;
    public VerifiedIdentity? Identity(string id) => Enabled && identities.TryGetValue(id, out var identity) ? identity : null;
    public static bool IsDemoToken(string token) => Regex.IsMatch(token, "^demo\\.[0-9a-f]{64}\\.session$", RegexOptions.CultureInvariant);
    private static void Require(bool valid, string reason) { if (!valid) throw Invalid(reason); }
    private static InvalidOperationException Invalid(string reason) => new("Public demo isolation requires " + reason + ".");

    // These are synthetic IDs from sample-bar-data.py, never arbitrary provider accounts.
    private static readonly Dictionary<string, (string Id, string Role)> People = new(StringComparer.Ordinal)
    {
        ["owner"] = ("be839b47-6b15-53a1-8cb8-fda359cdf3e1", "owner"),
        ["manager"] = ("18464824-b2b5-5daf-85fa-884c9c1c6149", "manager"),
        ["bartender"] = ("ce9b3046-cfc4-538a-b15c-f1fa365a5678", "bartender"),
        ["server-maya"] = ("ba144872-0b90-593a-a29a-3a11e8348339", "server"),
        ["server-eli"] = ("544811b6-d897-547d-bfb3-d17421fd9ea1", "server"),
        ["kitchen"] = ("a9b09cac-35ef-546f-8997-9f89a48daf2c", "kitchen"),
        ["driver"] = ("9966c5a2-77b9-5570-b011-1e7a66708f86", "driver"),
        ["avery"] = ("eefe928f-a53e-5d89-8d86-d070c10d62ba", "customer"),
        ["morgan"] = ("d51f07ca-7a39-55c4-bad1-6aea3a6cc117", "customer"),
        ["taylor"] = ("30488753-4c87-58c1-baf6-0e2d8c65d823", "customer"),
        ["quinn"] = ("7b5abec4-3c32-5ca0-9b20-f7e67e0f90ec", "customer"),
        ["reese"] = ("69738b48-0895-5265-a7c8-957f09a07881", "customer")
    };
}

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record DemoSwitchRequest(string? PersonKey);
