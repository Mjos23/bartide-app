namespace TideCasa.Api.Infrastructure;

public sealed class DriverPaymentsSchema(ApplicationDatabase database, IHostEnvironment environment)
{
    public const string Sql = """
        CREATE TABLE IF NOT EXISTS tide_driver_payables (
            id TEXT PRIMARY KEY, tenant_id TEXT NOT NULL, order_id TEXT NOT NULL UNIQUE,
            member_id TEXT NOT NULL, user_id TEXT NOT NULL, driver_name TEXT NOT NULL,
            source TEXT NOT NULL CHECK(source IN ('network','own')), hire_id TEXT,
            agreed_pay_cents BIGINT NOT NULL, completed_at TEXT NOT NULL, order_number TEXT NOT NULL
        );
        CREATE INDEX IF NOT EXISTS tide_driver_payables_tenant ON tide_driver_payables(tenant_id,completed_at);
        CREATE INDEX IF NOT EXISTS tide_driver_payables_user ON tide_driver_payables(user_id,completed_at);
        CREATE TABLE IF NOT EXISTS tide_driver_payment_states (
            payment_id TEXT NOT NULL, environment TEXT NOT NULL,
            driver_pay_cents BIGINT NOT NULL, fee_cents BIGINT NOT NULL, total_cents BIGINT NOT NULL,
            status TEXT NOT NULL, version BIGINT NOT NULL, current_attempt_id TEXT,
            approved_by TEXT NOT NULL, approved_at TEXT NOT NULL, updated_at TEXT NOT NULL,
            PRIMARY KEY(payment_id,environment)
        );
        CREATE TABLE IF NOT EXISTS tide_driver_payment_attempts (
            id TEXT PRIMARY KEY, payment_id TEXT NOT NULL, environment TEXT NOT NULL,
            account_id TEXT NOT NULL, user_id TEXT NOT NULL, request_json TEXT NOT NULL,
            status TEXT NOT NULL, session_id TEXT, checkout_url TEXT,
            created_at TEXT NOT NULL, updated_at TEXT NOT NULL, lease_key TEXT, lease_until TEXT,
            UNIQUE(environment,session_id)
        );
        CREATE TABLE IF NOT EXISTS tide_driver_payout_accounts (
            user_id TEXT NOT NULL, environment TEXT NOT NULL, account_id TEXT,
            create_key TEXT NOT NULL, create_json TEXT NOT NULL, state TEXT NOT NULL,
            created_at TEXT NOT NULL, updated_at TEXT NOT NULL, lease_key TEXT, lease_until TEXT,
            PRIMARY KEY(user_id,environment), UNIQUE(environment,account_id)
        );
        """;
    public async Task InitializeAsync(CancellationToken ct = default)
    {
        await using var db = await database.OpenAsync(ct); using var tx = db.BeginTransaction(deferred: false);
        await using var command = db.CreateCommand(); command.Transaction = tx;
        if (!database.IsPostgreSql || environment.IsDevelopment()) { command.CommandText = Sql; await command.ExecuteNonQueryAsync(ct); }
        foreach (var query in new[] {
            "SELECT id,tenant_id,order_id,member_id,user_id,driver_name,source,hire_id,agreed_pay_cents,completed_at,order_number FROM tide_driver_payables WHERE 1=0",
            "SELECT payment_id,environment,driver_pay_cents,fee_cents,total_cents,status,version,current_attempt_id,approved_by,approved_at,updated_at FROM tide_driver_payment_states WHERE 1=0",
            "SELECT id,payment_id,environment,account_id,user_id,request_json,status,session_id,checkout_url,created_at,updated_at,lease_key,lease_until FROM tide_driver_payment_attempts WHERE 1=0",
            "SELECT user_id,environment,account_id,create_key,create_json,state,created_at,updated_at,lease_key,lease_until FROM tide_driver_payout_accounts WHERE 1=0" })
        { command.CommandText = query; await using var reader = await command.ExecuteReaderAsync(ct); }
        await tx.CommitAsync(ct);
    }
}
