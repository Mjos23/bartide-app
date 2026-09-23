using TideCasa.Api.Infrastructure;

namespace TideCasa.Api.Features.EmailTracking;

/// <summary>Additive tables; production PostgreSQL DDL is provisioned separately by the schema owner.</summary>
public sealed class EmailTrackingSchema(ApplicationDatabase database, IHostEnvironment environment)
{
    public const string Sql = """
        CREATE TABLE tide_email_campaigns (
            id TEXT PRIMARY KEY, name TEXT NOT NULL, is_test BIGINT NOT NULL,
            link_token TEXT NOT NULL UNIQUE, created_at TEXT NOT NULL
        );
        CREATE TABLE tide_email_visits (
            id TEXT PRIMARY KEY, campaign_id TEXT NOT NULL REFERENCES tide_email_campaigns(id),
            created_at TEXT NOT NULL, expires_at TEXT NOT NULL
        );
        CREATE INDEX tide_email_visits_campaign ON tide_email_visits(campaign_id,created_at);
        CREATE TABLE tide_email_clicks (
            visit_id TEXT NOT NULL REFERENCES tide_email_visits(id) ON DELETE CASCADE,
            sequence BIGINT NOT NULL, path TEXT NOT NULL, created_at TEXT NOT NULL,
            PRIMARY KEY(visit_id,sequence)
        );
        """;

    public async Task InitializeAsync(CancellationToken ct = default)
    {
        await using var db = await database.OpenAsync(ct); using var tx = db.BeginTransaction(deferred: false);
        await using var exists = db.CreateCommand(); exists.Transaction = tx;
        exists.CommandText = database.IsPostgreSql
            ? "SELECT COUNT(*) FROM information_schema.tables WHERE table_schema=current_schema() AND table_name='tide_email_campaigns'"
            : "SELECT COUNT(*) FROM sqlite_schema WHERE type='table' AND name='tide_email_campaigns'";
        if (Convert.ToInt64(await exists.ExecuteScalarAsync(ct)) == 0)
        {
            if (database.IsPostgreSql && !environment.IsDevelopment())
                throw new InvalidOperationException("Apply the email-tracking extension with the schema owner before deploying this release.");
            await using var create = db.CreateCommand(); create.Transaction = tx; create.CommandText = Sql;
            await create.ExecuteNonQueryAsync(ct);
        }
        await using var probe = db.CreateCommand(); probe.Transaction = tx;
        probe.CommandText = "SELECT c.id,c.name,c.is_test,c.link_token,c.created_at,v.id,v.campaign_id,v.created_at,v.expires_at,e.visit_id,e.sequence,e.path,e.created_at FROM tide_email_campaigns c LEFT JOIN tide_email_visits v ON v.campaign_id=c.id LEFT JOIN tide_email_clicks e ON e.visit_id=v.id WHERE 1=0";
        await using (var reader = await probe.ExecuteReaderAsync(ct)) { }
        await tx.CommitAsync(ct);
    }
}
