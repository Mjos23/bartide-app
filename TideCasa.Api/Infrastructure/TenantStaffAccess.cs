using System.Data.Common;
using TideCasa.Contracts;

namespace TideCasa.Api.Infrastructure;

/// <summary>Resolves active restaurant staff from durable tenant membership, never identity metadata.</summary>
internal static class TenantStaffAccess
{
    internal sealed record Member(string Id, string Name, string Role);

    // The caller owns this transaction and commits a successful invitation binding with its operation.
    public static async Task<Member?> FindAsync(DbConnection db, DbTransaction tx, string tenant, AuthUser user, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(user.UserId) || string.IsNullOrWhiteSpace(user.Email)) return null;
        await using (var eligible = Command(db, tx, db.Sql("""
            SELECT COUNT(*) FROM bartide_customers c JOIN bartide_enhanced_configs e ON e.tenant_id=c.id
            WHERE c.id=@tenant AND c.status='active' AND c.vertical='bartide'
              AND CASE WHEN json_valid(e.settings_json) THEN COALESCE(json_type(e.settings_json,'$.enabled')='true',0) ELSE 0 END=1
            """, """
            SELECT COUNT(*) FROM bartide_customers c JOIN bartide_enhanced_configs e ON e.tenant_id=c.id
            WHERE c.id=@tenant AND c.status='active' AND c.vertical='bartide'
              AND COALESCE(tide_json(e.settings_json)->'enabled'='true'::jsonb,false)
            """), ("@tenant", tenant)))
            if (Convert.ToInt64(await eligible.ExecuteScalarAsync(ct)) != 1) return null;

        var existing = await ReadMembers(db, tx, tenant, user.UserId, ct);
        if (existing.Count != 0) return existing.Count == 1 ? existing[0] : null;

        // Only a verified email can claim an unbound invitation. Existing bindings are never replaced.
        await using (var bind = Command(db, tx, db.Sql("""
            UPDATE bartide_enhanced_members SET user_id=@user
            WHERE tenant_id=@tenant AND active=1 AND user_id IS NULL AND email=@email COLLATE NOCASE
              AND role IN('manager','bartender','server','kitchen','driver')
            """, """
            UPDATE bartide_enhanced_members SET user_id=@user
            WHERE tenant_id=@tenant AND active=1 AND user_id IS NULL
              AND translate(email,'ABCDEFGHIJKLMNOPQRSTUVWXYZ','abcdefghijklmnopqrstuvwxyz')=translate(@email,'ABCDEFGHIJKLMNOPQRSTUVWXYZ','abcdefghijklmnopqrstuvwxyz') COLLATE "C"
              AND role IN('manager','bartender','server','kitchen','driver')
            """), ("@tenant", tenant), ("@user", user.UserId), ("@email", user.Email.Trim())))
            await bind.ExecuteNonQueryAsync(ct);

        var bound = await ReadMembers(db, tx, tenant, user.UserId, ct);
        return bound.Count == 1 ? bound[0] : null;
    }

    private static async Task<List<Member>> ReadMembers(DbConnection db, DbTransaction tx, string tenant, string userId, CancellationToken ct)
    {
        await using var query = Command(db, tx, """
            SELECT id,name,role FROM bartide_enhanced_members
            WHERE tenant_id=@tenant AND user_id=@user AND active=1
              AND role IN('manager','bartender','server','kitchen','driver') LIMIT 2
            """, ("@tenant", tenant), ("@user", userId));
        await using var reader = await query.ExecuteReaderAsync(ct);
        var members = new List<Member>(2);
        while (await reader.ReadAsync(ct)) members.Add(new(reader.GetString(0), reader.GetString(1), reader.GetString(2)));
        return members;
    }

    public static async Task<bool> IsManagerAsync(DbConnection db, DbTransaction tx, string tenant, AuthUser user, CancellationToken ct) =>
        (await FindAsync(db, tx, tenant, user, ct))?.Role == "manager";

    private static DbCommand Command(DbConnection db, DbTransaction tx, string sql, params (string Key, object? Value)[] values)
    {
        var command = db.CreateCommand(); command.Transaction = tx; command.CommandText = sql;
        foreach (var (key, value) in values) command.Parameters.AddWithValue(key, value ?? DBNull.Value);
        return command;
    }
}
