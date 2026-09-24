namespace TideCasa.Api.Infrastructure;

/// <summary>Additive extension; hosted PostgreSQL is provisioned by the schema owner.</summary>
public sealed class DeliveryDispatchSchema(ApplicationDatabase database, IHostEnvironment environment)
{
    public const string Sql = """
        CREATE TABLE IF NOT EXISTS tide_delivery_dispatch (
            tenant_id TEXT PRIMARY KEY, automatic_assignment BIGINT NOT NULL DEFAULT 0,
            version BIGINT NOT NULL DEFAULT 0, updated_at TEXT NOT NULL, updated_by TEXT NOT NULL
        );
        CREATE TABLE IF NOT EXISTS tide_delivery_drivers (
            tenant_id TEXT NOT NULL, driver_id TEXT NOT NULL,
            availability TEXT NOT NULL CHECK(availability IN ('offline','available','scheduled')),
            capacity BIGINT NOT NULL CHECK(capacity BETWEEN 1 AND 10), zips_json TEXT NOT NULL,
            version BIGINT NOT NULL DEFAULT 0, last_assigned_at TEXT,
            updated_at TEXT NOT NULL, updated_by TEXT NOT NULL,
            PRIMARY KEY(tenant_id,driver_id)
        );
        """;

    public async Task InitializeAsync(CancellationToken ct = default)
    {
        await using var db = await database.OpenAsync(ct);
        using var tx = db.BeginTransaction(deferred: false);
        await using var command = db.CreateCommand(); command.Transaction = tx;
        if (!database.IsPostgreSql || environment.IsDevelopment())
        {
            command.CommandText = Sql;
            await command.ExecuteNonQueryAsync(ct);
        }
        // Fail closed before workers start when an operator has not applied the extension.
        command.CommandText = "SELECT tenant_id,automatic_assignment,version,updated_at,updated_by FROM tide_delivery_dispatch WHERE 1=0";
        await using (var reader = await command.ExecuteReaderAsync(ct)) { }
        command.CommandText = "SELECT tenant_id,driver_id,availability,capacity,zips_json,version,last_assigned_at,updated_at,updated_by FROM tide_delivery_drivers WHERE 1=0";
        await using (var reader = await command.ExecuteReaderAsync(ct)) { }
        await tx.CommitAsync(ct);
    }
}
