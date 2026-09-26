using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.AspNetCore.DataProtection;
using Npgsql;

namespace TideCasa.Blazor.Features.Collect;

/// <summary>Isolated, encrypted pilot aggregates. Revision checks cover every state mutation.</summary>
public sealed class CollectRepository : IDisposable
{
    private readonly IDataProtector protector;
    private readonly NpgsqlDataSource? source;
    private readonly string? folder;
    private readonly bool createSchema;
    private readonly SemaphoreSlim gate = new(1, 1);
    private bool initialized;
    public CollectRepository(IConfiguration config, IWebHostEnvironment environment, IDataProtectionProvider protection)
    {
        protector = protection.CreateProtector("TideCasa.Collect.Aggregates.v1");
        createSchema = environment.IsDevelopment() && config.GetValue("Collect:CreateSchema", true);
        var connection = config.GetConnectionString("Application");
        if (!string.IsNullOrWhiteSpace(connection))
        {
            var settings = new NpgsqlConnectionStringBuilder(connection) { MaxPoolSize = 2, MinPoolSize = 0, Timeout = 10, CommandTimeout = 15, IncludeErrorDetail = false, LogParameters = false, ApplicationName = "TideCasa.Collect" };
            if (!environment.IsDevelopment() && settings.SslMode != SslMode.VerifyFull) throw new InvalidOperationException("Collect requires verified TLS database connections.");
            source = NpgsqlDataSource.Create(settings.ConnectionString);
        }
        else if (environment.IsDevelopment())
        {
            folder = Path.GetFullPath(config["Collect:DevelopmentPath"] ?? "App_Data/collect", environment.ContentRootPath);
            Directory.CreateDirectory(folder);
        }
        // Production without a configured database serves the landing page, but cannot create a preview workspace.
    }
    private async Task Initialize(CancellationToken ct)
    {
        if (initialized) return;
        await gate.WaitAsync(ct);
        try
        {
            if (initialized) return;
            if (source != null)
            {
                await using var command = source.CreateCommand(createSchema ? """
                    CREATE SCHEMA IF NOT EXISTS tide_collect;
                    CREATE TABLE IF NOT EXISTS tide_collect.preview_workspaces (
                      id text PRIMARY KEY, revision integer NOT NULL CHECK(revision>=0), expires_at timestamptz NOT NULL,
                      ciphertext text NOT NULL, created_at timestamptz NOT NULL DEFAULT now());
                    CREATE INDEX IF NOT EXISTS collect_preview_expiry ON tide_collect.preview_workspaces(expires_at);
                    CREATE TABLE IF NOT EXISTS tide_collect.preview_access_log (
                      id text PRIMARY KEY, workspace_id text NOT NULL REFERENCES tide_collect.preview_workspaces(id) ON DELETE CASCADE,
                      accessed_at timestamptz NOT NULL, ciphertext text NOT NULL);
                    CREATE INDEX IF NOT EXISTS collect_preview_access_workspace ON tide_collect.preview_access_log(workspace_id);
                    """ : """
                    SELECT id, revision, expires_at, ciphertext, created_at FROM tide_collect.preview_workspaces WHERE false;
                    SELECT id, workspace_id, accessed_at, ciphertext FROM tide_collect.preview_access_log WHERE false;
                    """);
                await command.ExecuteNonQueryAsync(ct);
            }
            initialized = true;
        }
        finally { gate.Release(); }
    }
    private IDataProtector For(string id) => protector.CreateProtector(id);
    private string Protect(CollectClient client) => For(client.Id).Protect(JsonSerializer.Serialize(client));
    private CollectClient Unprotect(string id, string value) => JsonSerializer.Deserialize<CollectClient>(For(id).Unprotect(value)) ?? throw new CryptographicException();
    private static void ValidId(string id) => CollectRules.Require(id.Length == 32 && id.All(Uri.IsHexDigit), "Workspace unavailable.", 404);
    public async Task<CollectClient?> Read(string id, DateTimeOffset now, CancellationToken ct = default)
    {
        ValidId(id); await Initialize(ct);
        string? payload = null;
        if (source != null)
        {
            await using var command = source.CreateCommand("SELECT ciphertext FROM tide_collect.preview_workspaces WHERE id=@id AND expires_at>@now");
            command.Parameters.AddWithValue("id", id); command.Parameters.AddWithValue("now", now);
            payload = await command.ExecuteScalarAsync(ct) as string;
        }
        else if (folder != null)
        {
            await gate.WaitAsync(ct);
            try { var path = Path.Combine(folder, id + ".protected"); if (File.Exists(path)) payload = await File.ReadAllTextAsync(path, ct); }
            finally { gate.Release(); }
        }
        if (payload == null) return null;
        var state = Unprotect(id, payload);
        return state.Id == id && state.Preview && state.Expires > now ? state : null;
    }
    public async Task Create(CollectClient client, DateTimeOffset now, CancellationToken ct = default)
    {
        ValidId(client.Id); CollectRules.Require(client.Preview && client.Expires > now, "Only fictional pilot workspaces may be created.", 409);
        await Initialize(ct);
        if (source != null)
        {
            await using var connection = await source.OpenConnectionAsync(ct); await using var transaction = await connection.BeginTransactionAsync(ct);
            await using var command = connection.CreateCommand(); command.Transaction = transaction;
            command.CommandText = "SELECT pg_advisory_xact_lock(68412271); DELETE FROM tide_collect.preview_workspaces WHERE expires_at<=@now;";
            command.Parameters.AddWithValue("now", now); await command.ExecuteNonQueryAsync(ct);
            command.CommandText = "SELECT count(*) FROM tide_collect.preview_workspaces";
            CollectRules.Require(Convert.ToInt64(await command.ExecuteScalarAsync(ct)) < 500, "The preview is busy. Please try again later.", 429);
            command.CommandText = "INSERT INTO tide_collect.preview_workspaces(id,revision,expires_at,ciphertext) VALUES(@id,0,@expiry,@data)";
            command.Parameters.AddWithValue("id", client.Id); command.Parameters.AddWithValue("expiry", client.Expires!.Value); command.Parameters.AddWithValue("data", Protect(client));
            await command.ExecuteNonQueryAsync(ct); await transaction.CommitAsync(ct);
        }
        else if (folder != null)
        {
            await gate.WaitAsync(ct);
            try
            {
                foreach (var old in Directory.EnumerateFiles(folder, "*.protected")) if (File.GetLastWriteTimeUtc(old) < now.AddDays(-2).UtcDateTime) File.Delete(old);
                CollectRules.Require(Directory.EnumerateFiles(folder, "*.protected").Count() < 500, "The preview is busy.", 429);
                await using var stream = new FileStream(Path.Combine(folder, client.Id + ".protected"), FileMode.CreateNew, FileAccess.Write, FileShare.None);
                await using var writer = new StreamWriter(stream); await writer.WriteAsync(Protect(client));
            }
            finally { gate.Release(); }
        }
        else throw new CollectFault(503, "The preview workspace is temporarily unavailable.");
    }
    public async Task Save(CollectClient client, int expected, DateTimeOffset now, CancellationToken ct = default)
    {
        ValidId(client.Id); CollectRules.Require(client.Revision == expected && client.Preview && client.Expires > now, "This page changed or expired. Reload before trying again.", 409);
        await Initialize(ct); client.Revision = checked(expected + 1);
        var payload = Protect(client);
        CollectRules.Require(payload.Length <= 2 * 1024 * 1024, "This pilot workspace has reached its size limit.", 409);
        if (source != null)
        {
            await using var command = source.CreateCommand("UPDATE tide_collect.preview_workspaces SET ciphertext=@data, revision=@next WHERE id=@id AND revision=@expected AND expires_at>@now");
            command.Parameters.AddWithValue("data", payload); command.Parameters.AddWithValue("next", client.Revision); command.Parameters.AddWithValue("id", client.Id); command.Parameters.AddWithValue("expected", expected); command.Parameters.AddWithValue("now", now);
            CollectRules.Require(await command.ExecuteNonQueryAsync(ct) == 1, "Another action changed this page. Reload to see the current version.", 409);
        }
        else if (folder != null)
        {
            await gate.WaitAsync(ct);
            try
            {
                var path = Path.Combine(folder, client.Id + ".protected");
                CollectRules.Require(File.Exists(path) && Unprotect(client.Id, await File.ReadAllTextAsync(path, ct)).Revision == expected, "Another action changed this page. Reload to see the current version.", 409);
                var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
                try { await File.WriteAllTextAsync(temporary, payload, ct); File.Move(temporary, path, true); }
                finally { if (File.Exists(temporary)) File.Delete(temporary); }
            }
            finally { gate.Release(); }
        }
        else throw new CollectFault(503, "The preview workspace is temporarily unavailable.");
    }
    public async Task Delete(string id, CancellationToken ct = default)
    {
        ValidId(id); await Initialize(ct);
        if (source != null) { await using var command = source.CreateCommand("DELETE FROM tide_collect.preview_workspaces WHERE id=@id"); command.Parameters.AddWithValue("id", id); await command.ExecuteNonQueryAsync(ct); }
        else if (folder != null) { await gate.WaitAsync(ct); try { File.Delete(Path.Combine(folder, id + ".protected")); File.Delete(Path.Combine(folder, id + ".access")); } finally { gate.Release(); } }
    }
    public async Task RecordAccess(string id, CollectAudit audit, CancellationToken ct = default)
    {
        ValidId(id); await Initialize(ct);
        var payload = For(id).Protect(JsonSerializer.Serialize(audit));
        if (source != null)
        {
            await using var command = source.CreateCommand("INSERT INTO tide_collect.preview_access_log(id,workspace_id,accessed_at,ciphertext) SELECT @event,id,@now,@data FROM tide_collect.preview_workspaces WHERE id=@id AND expires_at>@now");
            command.Parameters.AddWithValue("event", audit.Id); command.Parameters.AddWithValue("now", audit.At); command.Parameters.AddWithValue("id", id); command.Parameters.AddWithValue("data", payload);
            CollectRules.Require(await command.ExecuteNonQueryAsync(ct) == 1, "Workspace expired.", 404);
        }
        else if (folder != null)
        {
            await gate.WaitAsync(ct);
            try { CollectRules.Require(File.Exists(Path.Combine(folder, id + ".protected")), "Workspace expired.", 404); await File.AppendAllTextAsync(Path.Combine(folder, id + ".access"), payload + Environment.NewLine, ct); }
            finally { gate.Release(); }
        }
        else throw new CollectFault(503, "Access logging is temporarily unavailable.");
    }
    public void Dispose() { source?.Dispose(); gate.Dispose(); }
}
