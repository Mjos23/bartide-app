using System.Data.Common;
using TideCasa.Api.Features.Authentication;
using TideCasa.Api.Infrastructure;
using TideCasa.Contracts;

namespace TideCasa.Api.Features.Accounts;

/// <summary>
/// Resolves tenant associations for an already authenticated, provider-verified user.
/// Edit/preparation flags are separate from association: paused owners still need billing
/// and support access. Feature mutations must also recheck their own status/role guards.
/// </summary>
public sealed class WorkspaceAccessStore(ApplicationDatabase database)
{
    public async Task<AccountOverview> GetOverviewAsync(AuthUser user, CancellationToken cancellationToken = default) =>
        new(user, await ResolveAsync(user, null, cancellationToken));

    public async Task<WorkspaceAccess?> GetAccessAsync(AuthUser user, string tenantId, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(tenantId) || tenantId.Length > 200) return null;
        var workspaces = await ResolveAsync(user, tenantId, cancellationToken);
        return workspaces.Count == 0 ? null : workspaces[0];
    }

    private async Task<IReadOnlyList<WorkspaceAccess>> ResolveAsync(AuthUser user, string? tenantId, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(user.UserId) || string.IsNullOrWhiteSpace(user.Email))
            throw new AuthFailureException("Please sign in again.", 401);
        var email = user.Email.Trim().ToLowerInvariant();
        try
        {
            await using var connection = await database.OpenAsync(cancellationToken);
            using var transaction = connection.BeginTransaction(deferred: false);
            if (!user.IsPlatformOwner)
                await BindPendingAsync(connection, transaction, user.UserId, email, tenantId, cancellationToken);

            var tenants = new List<TenantAccess>();
            await using (var query = connection.CreateCommand())
            {
                query.Transaction = transaction;
                // SQLite NOCASE folds ASCII only. PostgreSQL uses the same fold and a
                // deterministic collation, preserving invitation matching and list order.
                query.CommandText = connection.Sql("""
                    SELECT c.id,c.slug,c.name,c.status,c.user_id,c.vertical,
                           CASE WHEN json_valid(e.settings_json)
                             THEN COALESCE(json_type(e.settings_json,'$.enabled')='true',0) ELSE 0 END
                    FROM bartide_customers c
                    LEFT JOIN bartide_enhanced_configs e ON e.tenant_id=c.id
                    WHERE (@tenant IS NULL OR c.id=@tenant)
                      AND (@platform=1 OR c.user_id=@user OR
                        (c.status='active' AND (CASE WHEN json_valid(e.settings_json)
                          THEN COALESCE(json_type(e.settings_json,'$.enabled')='true',0) ELSE 0 END)=1 AND
                          (EXISTS (SELECT 1 FROM bartide_enhanced_members m WHERE m.tenant_id=c.id
                                   AND m.user_id=@user AND m.active=1 AND m.role IN ('kitchen','driver'))
                           OR (c.vertical IN ('bartide','beach-glam','fit-tide') AND
                             EXISTS (SELECT 1 FROM fit_learners l WHERE l.tenant_id=c.id
                                     AND l.user_id=@user AND l.active=1)))))
                    ORDER BY c.name COLLATE NOCASE,c.id
                    """, """
                    SELECT c.id,c.slug,c.name,c.status,c.user_id,c.vertical,
                           CASE WHEN COALESCE(tide_json(e.settings_json)->'enabled'='true'::jsonb,false) THEN 1 ELSE 0 END
                    FROM bartide_customers c
                    LEFT JOIN bartide_enhanced_configs e ON e.tenant_id=c.id
                    WHERE (@tenant IS NULL OR c.id=@tenant)
                      AND (@platform=1 OR c.user_id=@user OR
                        (c.status='active' AND (CASE WHEN COALESCE(tide_json(e.settings_json)->'enabled'='true'::jsonb,false) THEN 1 ELSE 0 END)=1 AND
                          (EXISTS (SELECT 1 FROM bartide_enhanced_members m WHERE m.tenant_id=c.id
                                   AND m.user_id=@user AND m.active=1 AND m.role IN ('kitchen','driver'))
                           OR (c.vertical IN ('bartide','beach-glam','fit-tide') AND
                             EXISTS (SELECT 1 FROM fit_learners l WHERE l.tenant_id=c.id
                                     AND l.user_id=@user AND l.active=1)))))
                    ORDER BY translate(c.name,'ABCDEFGHIJKLMNOPQRSTUVWXYZ','abcdefghijklmnopqrstuvwxyz') COLLATE "C",c.id
                    """);
                query.Parameters.AddWithValue("@tenant", (object?)tenantId ?? DBNull.Value);
                query.Parameters.AddWithValue("@platform", user.IsPlatformOwner ? 1 : 0);
                query.Parameters.AddWithValue("@user", user.UserId);
                await using var reader = await query.ExecuteReaderAsync(cancellationToken);
                while (await reader.ReadAsync(cancellationToken))
                    tenants.Add(new(reader.GetString(0), reader.GetString(1), reader.GetString(2), reader.GetString(3),
                        reader.IsDBNull(4) ? null : reader.GetString(4), reader.GetString(5), reader.ReadInt64(6) == 1));
            }

            var result = new List<WorkspaceAccess>();
            foreach (var tenant in tenants)
            {
                var ownsTenant = tenant.UserId == user.UserId;
                var manages = user.IsPlatformOwner || ownsTenant;
                var canPrepare = user.IsPlatformOwner || ownsTenant && (tenant.Status is "draft" or "building" or "active");
                var canEdit = user.IsPlatformOwner || ownsTenant && tenant.Status == "active";
                string? staffRole = null, staffMemberId = null, learnerId = null;
                if (!manages && tenant.Status == "active" && tenant.Enabled)
                {
                    await using (var staff = connection.CreateCommand())
                    {
                        staff.Transaction = transaction;
                        staff.CommandText = """
                            SELECT id,role FROM bartide_enhanced_members
                            WHERE tenant_id=@tenant AND user_id=@user AND active=1 AND role IN ('kitchen','driver')
                            ORDER BY id LIMIT 2
                            """;
                        staff.Parameters.AddWithValue("@tenant", tenant.Id);
                        staff.Parameters.AddWithValue("@user", user.UserId);
                        await using var reader = await staff.ExecuteReaderAsync(cancellationToken);
                        if (await reader.ReadAsync(cancellationToken))
                        {
                            staffMemberId = reader.GetString(0);
                            staffRole = reader.GetString(1);
                            if (await reader.ReadAsync(cancellationToken))
                                throw new AuthFailureException("Your team membership needs an account review.", 409);
                        }
                    }
                    if (tenant.Vertical is "bartide" or "beach-glam" or "fit-tide")
                    {
                        await using var learner = connection.CreateCommand();
                        learner.Transaction = transaction;
                        learner.CommandText = "SELECT id FROM fit_learners WHERE tenant_id=@tenant AND user_id=@user AND active=1 ORDER BY id LIMIT 2";
                        learner.Parameters.AddWithValue("@tenant", tenant.Id);
                        learner.Parameters.AddWithValue("@user", user.UserId);
                        await using var reader = await learner.ExecuteReaderAsync(cancellationToken);
                        if (await reader.ReadAsync(cancellationToken))
                        {
                            learnerId = reader.GetString(0);
                            if (await reader.ReadAsync(cancellationToken))
                                throw new AuthFailureException("Your course membership needs an account review.", 409);
                        }
                    }
                }
                result.Add(new(tenant.Id, tenant.Slug, tenant.Name, tenant.Status,
                    canPrepare, canEdit, staffRole, staffMemberId, learnerId, manages));
            }
            await transaction.CommitAsync(cancellationToken);
            return result.AsReadOnly();
        }
        catch (DbException)
        {
            throw new AuthFailureException("Account access is temporarily unavailable. Please try again.", 503);
        }
    }

    private static async Task BindPendingAsync(DbConnection connection, DbTransaction transaction,
        string userId, string email, string? tenantId, CancellationToken cancellationToken)
    {
        // A verified email may claim an invitation only while its durable user ID is null.
        // No existing owner, employee or learner binding is ever replaced by an email match.
        string[] statements =
        [
            connection.Sql("""
                UPDATE bartide_customers SET user_id=@user
                WHERE user_id IS NULL AND email=@email COLLATE NOCASE AND (@tenant IS NULL OR id=@tenant)
                """, """
                UPDATE bartide_customers SET user_id=@user
                WHERE user_id IS NULL AND translate(email,'ABCDEFGHIJKLMNOPQRSTUVWXYZ','abcdefghijklmnopqrstuvwxyz')=translate(@email,'ABCDEFGHIJKLMNOPQRSTUVWXYZ','abcdefghijklmnopqrstuvwxyz') COLLATE "C" AND (@tenant IS NULL OR id=@tenant)
                """),
            connection.Sql("""
                UPDATE bartide_enhanced_members SET user_id=@user
                WHERE user_id IS NULL AND email=@email COLLATE NOCASE AND active=1
                  AND role IN ('kitchen','driver') AND (@tenant IS NULL OR tenant_id=@tenant)
                  AND EXISTS (SELECT 1 FROM bartide_customers c JOIN bartide_enhanced_configs e ON e.tenant_id=c.id
                              WHERE c.id=bartide_enhanced_members.tenant_id AND c.status='active'
                                AND (CASE WHEN json_valid(e.settings_json)
                                  THEN COALESCE(json_type(e.settings_json,'$.enabled')='true',0) ELSE 0 END)=1)
                """, """
                UPDATE bartide_enhanced_members SET user_id=@user
                WHERE user_id IS NULL AND translate(email,'ABCDEFGHIJKLMNOPQRSTUVWXYZ','abcdefghijklmnopqrstuvwxyz')=translate(@email,'ABCDEFGHIJKLMNOPQRSTUVWXYZ','abcdefghijklmnopqrstuvwxyz') COLLATE "C" AND active=1
                  AND role IN ('kitchen','driver') AND (@tenant IS NULL OR tenant_id=@tenant)
                  AND EXISTS (SELECT 1 FROM bartide_customers c JOIN bartide_enhanced_configs e ON e.tenant_id=c.id
                              WHERE c.id=bartide_enhanced_members.tenant_id AND c.status='active'
                                AND (CASE WHEN COALESCE(tide_json(e.settings_json)->'enabled'='true'::jsonb,false) THEN 1 ELSE 0 END)=1)
                """),
            connection.Sql("""
                UPDATE fit_learners SET user_id=@user
                WHERE user_id IS NULL AND email=@email COLLATE NOCASE AND active=1
                  AND (@tenant IS NULL OR tenant_id=@tenant)
                  AND EXISTS (SELECT 1 FROM bartide_customers c JOIN bartide_enhanced_configs e ON e.tenant_id=c.id
                              WHERE c.id=fit_learners.tenant_id AND c.status='active'
                                AND c.vertical IN ('bartide','beach-glam','fit-tide')
                                AND (CASE WHEN json_valid(e.settings_json)
                                  THEN COALESCE(json_type(e.settings_json,'$.enabled')='true',0) ELSE 0 END)=1)
                """, """
                UPDATE fit_learners SET user_id=@user
                WHERE user_id IS NULL AND translate(email,'ABCDEFGHIJKLMNOPQRSTUVWXYZ','abcdefghijklmnopqrstuvwxyz')=translate(@email,'ABCDEFGHIJKLMNOPQRSTUVWXYZ','abcdefghijklmnopqrstuvwxyz') COLLATE "C" AND active=1
                  AND (@tenant IS NULL OR tenant_id=@tenant)
                  AND EXISTS (SELECT 1 FROM bartide_customers c JOIN bartide_enhanced_configs e ON e.tenant_id=c.id
                              WHERE c.id=fit_learners.tenant_id AND c.status='active'
                                AND c.vertical IN ('bartide','beach-glam','fit-tide')
                                AND (CASE WHEN COALESCE(tide_json(e.settings_json)->'enabled'='true'::jsonb,false) THEN 1 ELSE 0 END)=1)
                """)
        ];
        foreach (var statement in statements)
        {
            await using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = statement;
            command.Parameters.AddWithValue("@user", userId);
            command.Parameters.AddWithValue("@email", email);
            command.Parameters.AddWithValue("@tenant", (object?)tenantId ?? DBNull.Value);
            await command.ExecuteNonQueryAsync(cancellationToken);
        }
    }

    private sealed record TenantAccess(string Id, string Slug, string Name, string Status,
        string? UserId, string Vertical, bool Enabled);
}
