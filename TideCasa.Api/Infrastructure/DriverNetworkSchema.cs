namespace TideCasa.Api.Infrastructure;

/// <summary>Additive driver marketplace records; hosted PostgreSQL requires owner provisioning.</summary>
public sealed class DriverNetworkSchema(ApplicationDatabase database, IHostEnvironment environment)
{
    public const string Sql = """
        CREATE TABLE IF NOT EXISTS tide_network_drivers (
            id TEXT PRIMARY KEY, user_id TEXT NOT NULL UNIQUE, email TEXT NOT NULL,
            name TEXT NOT NULL, bio TEXT NOT NULL, zips_json TEXT NOT NULL,
            listed BIGINT NOT NULL DEFAULT 0 CHECK(listed IN (0,1)),
            capacity BIGINT NOT NULL DEFAULT 1 CHECK(capacity BETWEEN 1 AND 10),
            version BIGINT NOT NULL DEFAULT 1, created_at TEXT NOT NULL, updated_at TEXT NOT NULL
        );
        CREATE TABLE IF NOT EXISTS tide_network_hires (
            id TEXT PRIMARY KEY, tenant_id TEXT NOT NULL REFERENCES bartide_customers(id),
            driver_id TEXT NOT NULL REFERENCES tide_network_drivers(id),
            status TEXT NOT NULL CHECK(status IN ('offered','active','declined','ended')),
            pay_per_delivery_cents BIGINT NOT NULL CHECK(pay_per_delivery_cents BETWEEN 50 AND 100000),
            notes TEXT NOT NULL, member_id TEXT UNIQUE REFERENCES bartide_enhanced_members(id),
            version BIGINT NOT NULL DEFAULT 1, created_at TEXT NOT NULL, updated_at TEXT NOT NULL,
            updated_by TEXT NOT NULL, accepted_at TEXT, ended_at TEXT,
            UNIQUE(tenant_id,driver_id)
        );
        CREATE INDEX IF NOT EXISTS ix_network_hires_driver ON tide_network_hires(driver_id,status);
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
        command.CommandText = "SELECT id,user_id,email,name,bio,zips_json,listed,capacity,version,created_at,updated_at FROM tide_network_drivers WHERE 1=0";
        await using (var reader = await command.ExecuteReaderAsync(ct)) { }
        command.CommandText = "SELECT id,tenant_id,driver_id,status,pay_per_delivery_cents,notes,member_id,version,created_at,updated_at,updated_by,accepted_at,ended_at FROM tide_network_hires WHERE 1=0";
        await using (var reader = await command.ExecuteReaderAsync(ct)) { }
        await tx.CommitAsync(ct);
    }
}
