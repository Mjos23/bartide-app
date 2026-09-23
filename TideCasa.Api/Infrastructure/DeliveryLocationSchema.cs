namespace TideCasa.Api.Infrastructure;

/// <summary>Additive extension to the immutable baseline. Provisioned by the database owner on hosted deployments.</summary>
public sealed class DeliveryLocationSchema(ApplicationDatabase database, IHostEnvironment environment)
{
    public const string Sql = """
        CREATE TABLE tide_delivery_locations (
            tenant_id TEXT NOT NULL, driver_id TEXT NOT NULL, order_id TEXT NOT NULL,
            user_id TEXT NOT NULL, assignment_at TEXT NOT NULL, session_hash TEXT NOT NULL,
            simulated BIGINT NOT NULL, sequence BIGINT NOT NULL,
            started_at TEXT NOT NULL, received_at TEXT NOT NULL, position_json TEXT NOT NULL,
            PRIMARY KEY (tenant_id,driver_id), UNIQUE(tenant_id,order_id)
        )
        """;
    public async Task InitializeAsync(CancellationToken ct = default)
    {
        await using var db = await database.OpenAsync(ct);
        using var tx = db.BeginTransaction(deferred: false);
        await using var exists = db.CreateCommand(); exists.Transaction = tx;
        exists.CommandText = database.IsPostgreSql
            ? "SELECT COUNT(*) FROM information_schema.tables WHERE table_schema=current_schema() AND table_name='tide_delivery_locations'"
            : "SELECT COUNT(*) FROM sqlite_schema WHERE type='table' AND name='tide_delivery_locations'";
        if (Convert.ToInt64(await exists.ExecuteScalarAsync(ct)) == 0)
        {
            if (database.IsPostgreSql && !environment.IsDevelopment())
                throw new InvalidOperationException("Apply the delivery-location extension with the schema owner before deploying this release.");
            await using var create = db.CreateCommand(); create.Transaction = tx; create.CommandText = Sql;
            await create.ExecuteNonQueryAsync(ct);
        }
        await using var probe = db.CreateCommand(); probe.Transaction = tx;
        probe.CommandText = "SELECT tenant_id,driver_id,order_id,user_id,assignment_at,session_hash,simulated,sequence,started_at,received_at,position_json FROM tide_delivery_locations WHERE 1=0";
        await using (var reader = await probe.ExecuteReaderAsync(ct)) { }
        await tx.CommitAsync(ct);
    }
}
