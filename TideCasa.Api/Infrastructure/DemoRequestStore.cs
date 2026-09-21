using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using TideCasa.Api.Features.DemoRequests;
using TideCasa.Api.Features.SalesPipeline;
using TideCasa.Contracts;

namespace TideCasa.Api.Infrastructure;

public sealed class DemoRequestStore(ApplicationDatabase database)
{
    public async Task InitializeAsync()
    {
        if (database.IsPostgreSql) return; // The versioned PostgreSQL baseline includes this table.
        await using var connection = await database.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = """
            PRAGMA journal_mode=WAL;
            CREATE TABLE IF NOT EXISTS demo_requests (
                id TEXT PRIMARY KEY, payload_hash TEXT NOT NULL, email_hash TEXT NOT NULL,
                request_json TEXT NOT NULL, status TEXT NOT NULL DEFAULT 'requested',
                notification_state TEXT NOT NULL DEFAULT 'pending', created_at TEXT NOT NULL
            );
            CREATE INDEX IF NOT EXISTS demo_requests_email_time ON demo_requests(email_hash,created_at);
            """;
        await command.ExecuteNonQueryAsync();
    }

    public async Task<DemoSaveResult> SaveAsync(DemoRequest request, CancellationToken cancellationToken)
    {
        var json = JsonSerializer.Serialize(request);
        static string Hash(string value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)));
        var hash = Hash(json);
        await using var connection = await database.OpenAsync(cancellationToken);
        using var transaction = connection.BeginTransaction(deferred: false);
        await using var existing = connection.CreateCommand();
        existing.Transaction = transaction;
        existing.CommandText = "SELECT payload_hash FROM demo_requests WHERE id=@id";
        existing.Parameters.AddWithValue("@id", request.Id.ToString());
        var priorHash = await existing.ExecuteScalarAsync(cancellationToken) as string;
        if (priorHash is not null)
        {
            transaction.Commit();
            return priorHash == hash ? new(Receipt(request.Id), false) : new(null, false, Conflict: true);
        }
        await using var insert = connection.CreateCommand();
        insert.Transaction = transaction;
        insert.CommandText = """
            INSERT INTO demo_requests(id,payload_hash,email_hash,request_json,created_at)
            SELECT @id,@hash,@email,@json,@now
            WHERE (SELECT COUNT(*) FROM demo_requests WHERE email_hash=@email AND created_at>@day)<3
              AND (SELECT COUNT(*) FROM demo_requests WHERE created_at>@day)<100
              AND (SELECT COUNT(*) FROM demo_requests)<10000
            """;
        insert.Parameters.AddWithValue("@id", request.Id.ToString());
        insert.Parameters.AddWithValue("@hash", hash);
        insert.Parameters.AddWithValue("@email", Hash(request.Email));
        insert.Parameters.AddWithValue("@json", json);
        insert.Parameters.AddWithValue("@now", DateTimeOffset.UtcNow.ToString("O"));
        insert.Parameters.AddWithValue("@day", DateTimeOffset.UtcNow.AddDays(-1).ToString("O"));
        var created = await insert.ExecuteNonQueryAsync(cancellationToken) == 1;
        if (created)
        {
            try { await SalesPipelineStore.LinkDemoAsync(connection, transaction, request, DateTimeOffset.UtcNow.ToString("O"), cancellationToken); }
            catch (SalesPipelineFailure error) when (error.Status == 429)
            {
                transaction.Rollback();
                return new(null, false, Limited: true);
            }
            await using var notification = connection.CreateCommand();
            notification.Transaction = transaction;
            notification.CommandText = "INSERT INTO tide_demo_notifications(request_id,state,next_attempt_at,created_at,updated_at) VALUES(@id,'pending',@now,@now,@now)";
            notification.Parameters.AddWithValue("@id", request.Id.ToString());
            notification.Parameters.AddWithValue("@now", DateTimeOffset.UtcNow.ToString("O"));
            await notification.ExecuteNonQueryAsync(cancellationToken);
        }
        transaction.Commit();
        return created ? new(Receipt(request.Id), true) : new(null, false, Limited: true);
    }

    private static DemoRequestReceipt Receipt(Guid id) => new(id, "requested", "Your demo request is saved. We’ll be in touch to agree on a time. Your appointment is not confirmed yet.");
}
