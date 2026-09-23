using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using TideCasa.Api.Features.EmailTracking;
using TideCasa.Api.Infrastructure;
using TideCasa.Contracts;
using System.Text.Json;
using Npgsql;

var settings = new Dictionary<string, string?> { ["Storage:DatabasePath"] = Path.GetFullPath(".tools/email-tracking-checks/" + Guid.NewGuid().ToString("N") + ".db") };
NpgsqlConnection? admin = null;
string? schema = null;
if (args.Length == 1)
{
    using var fixture = JsonDocument.Parse(File.ReadAllText(args[0])); var f = fixture.RootElement;
    if (f.GetProperty("host").GetString() != "127.0.0.1") throw new Exception("Local PostgreSQL fixture required.");
    schema = "tide_email_check_" + Guid.NewGuid().ToString("N");
    var connection = new NpgsqlConnectionStringBuilder { Host = "127.0.0.1", Port = f.GetProperty("port").GetInt32(), Database = f.GetProperty("database").GetString(), Username = f.GetProperty("user").GetString(), Password = f.GetProperty("password").GetString(), SslMode = SslMode.Disable, Pooling = false };
    admin = new(connection.ConnectionString); await admin.OpenAsync();
    await using var create = admin.CreateCommand(); create.CommandText = "CREATE SCHEMA " + schema; await create.ExecuteNonQueryAsync();
    settings["Storage:Provider"] = "PostgreSql"; settings["Storage:PostgresSchema"] = schema; settings["ConnectionStrings:Application"] = connection.ConnectionString;
}
var environment = new LocalEnvironment(); var config = new ConfigurationBuilder().AddInMemoryCollection(settings).Build();
var passed = 0;
void Check(string label, bool value) { if (!value) throw new Exception(label); passed++; Console.WriteLine("PASS " + label); }
async Task Reject(string label, Func<Task> run, int status)
{ try { await run(); throw new Exception(label + " accepted"); } catch (EmailTrackingFailure e) { Check(label, e.Status == status); } }
var owner = new AuthUser("fixture-owner", "owner@example.invalid", "Owner", null, true);
var ordinary = owner with { IsPlatformOwner = false };
try
{
    using var database = new ApplicationDatabase(config, environment);
    var migration = new EmailTrackingSchema(database, environment);
    await migration.InitializeAsync(); await migration.InitializeAsync();
    var store = new EmailTrackingStore(database); await store.InitializeAsync(); await store.InitializeAsync();
    var test = await store.ReportAsync(owner, true, null, default);
    Check("Migration and seed restart idempotently", test.Campaigns.Count == 1 && test.Campaigns[0].Visits == 0);
    await Reject("Non-owner cannot read reports", async () => await store.ReportAsync(ordinary, true, null, default), 403);
    var create = new CreateEmailCampaign(Guid.NewGuid().ToString(), "Real campaign", false);
    await Reject("Non-owner cannot create campaign", async () => await store.CreateAsync(ordinary, create, default), 403);
    var campaign = await store.CreateAsync(owner, create, default);
    Check("Campaign create retry is idempotent", (await store.CreateAsync(owner, create, default)).LinkToken == campaign.LinkToken);
    await Reject("Conflicting retry rejected", async () => await store.CreateAsync(owner, create with { IsTest = true }, default), 409);
    var first = await store.StartAsync(new(EmailCampaignDefaults.TestToken), default);
    Check("Visit uses random opaque ID", first.SessionId.Length == 48);
    await Reject("Unknown campaign rejected", async () => await store.StartAsync(new(new string('a',32)), default), 404);
    await Reject("Malformed campaign rejected", async () => await store.StartAsync(new("https://evil.invalid"), default), 400);
    await Reject("Unknown visit rejected", async () => await store.ClickAsync(new(new string('b',48), "demo"), default), 404);
    await Reject("External redirect/path input rejected", async () => await store.ClickAsync(new(first.SessionId,"https://evil.invalid"), default), 400);
    Check("Demo selection persisted", (await store.ClickAsync(new(first.SessionId,"demo"), default)).Recorded);
    Check("Duplicate click deduplicated", !(await store.ClickAsync(new(first.SessionId,"demo"), default)).Recorded);
    await store.ClickAsync(new(first.SessionId,"purchase"), default);
    var second = await store.StartAsync(new(EmailCampaignDefaults.TestToken), default);
    await store.ClickAsync(new(second.SessionId,"contact"), default);
    test = await new EmailTrackingStore(database).ReportAsync(owner, true, null, default);
    Check("Test visits and click counts survive new store instance", test.Campaigns[0] is { Visits: 2, DemoClicks: 1, PurchaseClicks: 1, ContactClicks: 1 });
    Check("Ordered click journey reported", test.Pathways.Any(p => p.Path == "Email → Home → Book a demo → Purchase page" && p.Visits == 1));
    var production = await store.ReportAsync(owner, false, test.SelectedId, default);
    Check("Test data never appears in production", production.Campaigns.Count == 1 && production.Campaigns[0].Visits == 0 && production.Pathways.Count == 0 && production.SelectedId == campaign.Id);
    for (var i=0;i<25;i++) await store.ClickAsync(new(second.SessionId,i%2==0 ? "demo" : "purchase"),default);
    await using (var db = await database.OpenAsync())
    {
        await using var count = db.CreateCommand(); count.CommandText = "SELECT COUNT(*) FROM tide_email_clicks WHERE visit_id=@id"; count.Parameters.AddWithValue("@id",second.SessionId);
        Check("Per-visit event capacity bounded", Convert.ToInt32(await count.ExecuteScalarAsync()) == 20);
        await using var expire = db.CreateCommand(); expire.CommandText = "UPDATE tide_email_visits SET expires_at='2000-01-01T00:00:00.0000000+00:00' WHERE id=@id"; expire.Parameters.AddWithValue("@id",first.SessionId); await expire.ExecuteNonQueryAsync();
    }
    await Reject("Expired visit cannot append events", async () => await store.ClickAsync(new(first.SessionId,"contact"), default), 404);
    await using (var db = await database.OpenAsync())
    {
        await using var age = db.CreateCommand(); age.CommandText = "UPDATE tide_email_visits SET created_at='2000-01-01T00:00:00.0000000+00:00'"; await age.ExecuteNonQueryAsync();
    }
    Check("Reports exclude records older than 90 days", (await store.ReportAsync(owner,true,null,default)).Campaigns[0].Visits == 0);
    await store.InitializeAsync();
    await using (var db = await database.OpenAsync())
    {
        await using var count = db.CreateCommand(); count.CommandText = "SELECT COUNT(*) FROM tide_email_clicks";
        Check("Retention cleanup cascades to click events", Convert.ToInt32(await count.ExecuteScalarAsync()) == 0);
    }
    Console.WriteLine(JsonSerializer.Serialize(new { passed, provider = database.IsPostgreSql ? "PostgreSql" : "Sqlite" }));
}
finally
{
    if (admin is not null) { await using var drop = admin.CreateCommand(); drop.CommandText = "DROP SCHEMA " + schema + " CASCADE"; await drop.ExecuteNonQueryAsync(); await admin.DisposeAsync(); }
}
sealed class LocalEnvironment : IHostEnvironment
{
    public string EnvironmentName { get; set; } = "Development";
    public string ApplicationName { get; set; } = "EmailTrackingChecks";
    public string ContentRootPath { get; set; } = Directory.GetCurrentDirectory();
    public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
}
