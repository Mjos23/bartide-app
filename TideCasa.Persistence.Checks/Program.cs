using System.Security.Cryptography;
using System.Text.Json;
using System.Xml.Linq;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Npgsql;
using TideCasa.Blazor.Services;

if (args is ["--ingress"]) { await AppPlatformIngressChecks.RunAsync(); return; }
if (args.Length != 1) throw new InvalidOperationException("Provide the private local PostgreSQL fixture JSON path.");
using var fixture = JsonDocument.Parse(File.ReadAllText(args[0]));
var f = fixture.RootElement;
if (f.GetProperty("host").GetString() != "127.0.0.1") throw new InvalidOperationException("Tests require the local fixture.");
var schema = "tide_keytest_" + Guid.NewGuid().ToString("N");
var settings = new NpgsqlConnectionStringBuilder
{
    Host = "127.0.0.1", Port = f.GetProperty("port").GetInt32(), Database = f.GetProperty("database").GetString(),
    Username = f.GetProperty("user").GetString(), Password = f.GetProperty("password").GetString(), SslMode = SslMode.Disable,
    MaxPoolSize = 2, Pooling = false
};
await using var admin = new NpgsqlConnection(settings.ConnectionString);
await admin.OpenAsync();
async Task Run(string sql) { await using var command = new NpgsqlCommand(sql, admin); await command.ExecuteNonQueryAsync(); }
var passed = 0;
void Check(string label, bool condition) { if (!condition) throw new InvalidOperationException(label); passed++; Console.WriteLine("PASS " + label); }
var encryptionKey = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));
IConfiguration Configuration(string? key = null, string? selectedSchema = null) => new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
{
    ["ConnectionStrings:Application"] = settings.ConnectionString,
    ["Storage:PostgresSchema"] = selectedSchema ?? schema,
    ["DataProtection:EncryptionKey"] = key ?? encryptionKey
}).Build();
try
{
    await Run($"CREATE SCHEMA {schema}; CREATE TABLE {schema}.tide_data_protection_keys(id TEXT PRIMARY KEY,friendly_name TEXT NOT NULL,ciphertext TEXT NOT NULL,created_at TEXT NOT NULL)");
    var xml = new XElement("key", new XAttribute("id", Guid.NewGuid()), new XElement("secret", "synthetic-key-canary"));
    using (var first = new PostgresKeyRepository(Configuration(), new LocalEnvironment()))
    {
        Check("New isolated key ring is empty", first.GetAllElements().Count == 0);
        first.StoreElement(xml, "first-key");
        Check("Stored key can be decrypted", XNode.DeepEquals(xml, first.GetAllElements().Single()));
    }
    using (var restarted = new PostgresKeyRepository(Configuration(), new LocalEnvironment()))
    {
        Check("New process repository retains keys", XNode.DeepEquals(xml, restarted.GetAllElements().Single()));
        await using (var stored = new NpgsqlCommand($"SELECT ciphertext FROM {schema}.tide_data_protection_keys", admin))
        {
            var value = (string)(await stored.ExecuteScalarAsync())!;
            Check("Database contains encrypted envelope", value.StartsWith("v1.") && !value.Contains("synthetic-key-canary") && !value.Contains("<key"));
        }
        using (var wrong = new PostgresKeyRepository(Configuration(Convert.ToBase64String(RandomNumberGenerator.GetBytes(32))), new LocalEnvironment()))
        {
            var rejected = false;
            try { wrong.GetAllElements(); } catch (CryptographicException) { rejected = true; }
            Check("Wrong deployment key fails closed", rejected);
        }
        await Run($"UPDATE {schema}.tide_data_protection_keys SET friendly_name='tampered'");
        var tamperingRejected = false;
        try { restarted.GetAllElements(); } catch (CryptographicException) { tamperingRejected = true; }
        Check("Record metadata tampering fails authentication", tamperingRejected);
        await Run($"UPDATE {schema}.tide_data_protection_keys SET friendly_name='first-key'");
        Check("Failed reads do not destroy retained keys", XNode.DeepEquals(xml, restarted.GetAllElements().Single()));
    }
    foreach (var unsafeSchema in new[] { "public", "auth", "storage", "tide_x;DROP SCHEMA public" })
    {
        var rejected = false;
        try { using var invalid = new PostgresKeyRepository(Configuration(selectedSchema: unsafeSchema), new LocalEnvironment()); }
        catch (InvalidOperationException) { rejected = true; }
        Check("Shared or malformed schema rejected: " + unsafeSchema.Split(';')[0], rejected);
    }
    var missingRejected = false;
    try { using var invalid = new PostgresKeyRepository(Configuration(""), new LocalEnvironment()); }
    catch (InvalidOperationException) { missingRejected = true; }
    Check("Missing encryption key cannot fall back to disk", missingRejected);
    var tlsRejected = false;
    try { using var invalid = new PostgresKeyRepository(Configuration(), new LocalEnvironment { EnvironmentName = "Production" }); }
    catch (InvalidOperationException) { tlsRejected = true; }
    Check("Production rejects unverified database TLS", tlsRejected);
    Console.WriteLine(JsonSerializer.Serialize(new { passed, completed = true }));
}
finally { await Run($"DROP SCHEMA {schema} CASCADE"); }

sealed class LocalEnvironment : IHostEnvironment
{
    public string EnvironmentName { get; set; } = "Development";
    public string ApplicationName { get; set; } = "PersistenceChecks";
    public string ContentRootPath { get; set; } = Directory.GetCurrentDirectory();
    public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
}
