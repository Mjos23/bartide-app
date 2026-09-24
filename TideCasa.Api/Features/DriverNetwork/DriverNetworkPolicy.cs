using System.Data.Common;
using TideCasa.Api.Features.RestaurantOrdering;
using TideCasa.Api.Infrastructure;

namespace TideCasa.Api.Features.DriverNetwork;

public sealed record NetworkAssignment(string HireId, string DriverId, string DriverUserId, int PayPerDeliveryCents);

/// <summary>Call while holding the same writer transaction used to assign or operate an order.</summary>
public static class DriverNetworkPolicy
{
    private sealed record Hire(NetworkAssignment Assignment, string Status, int Capacity, bool MemberActive);

    private static async Task<Hire?> ReadAsync(DbConnection db, DbTransaction tx, string tenant, string member, CancellationToken ct)
    {
        await using var command = DriverNetworkStore.Command(db, tx, """
            SELECT h.id,h.driver_id,d.user_id,h.pay_per_delivery_cents,h.status,d.capacity,
                   CASE WHEN h.member_id=@member AND m.active=1 AND m.role='driver' AND m.user_id=d.user_id THEN 1 ELSE 0 END
            FROM tide_network_hires h JOIN tide_network_drivers d ON d.id=h.driver_id
            LEFT JOIN bartide_enhanced_members m ON m.id=@member AND m.tenant_id=h.tenant_id
            WHERE h.tenant_id=@tenant AND (h.member_id=@member OR m.user_id=d.user_id)
            """, ("@tenant", tenant), ("@member", member));
        await using var reader = await command.ExecuteReaderAsync(ct);
        return await reader.ReadAsync(ct) ? new(new(reader.GetString(0), reader.GetString(1), reader.GetString(2), reader.ReadInt32(3)),
            reader.GetString(4), reader.ReadInt32(5), reader.ReadInt64(6) == 1) : null;
    }

    public static async Task RequireActiveHireAsync(DbConnection db, DbTransaction tx, string tenant, string member, CancellationToken ct)
    {
        var hire = await ReadAsync(db, tx, tenant, member, ct);
        if (hire is not null && (hire.Status != "active" || !hire.MemberActive))
            throw new OrderingException("An active, accepted client hire is required.", 403, "forbidden");
    }

    public static async Task<NetworkAssignment?> ReadAssignmentAsync(DbConnection db, DbTransaction tx, string tenant, string member, CancellationToken ct)
    {
        var hire = await ReadAsync(db, tx, tenant, member, ct);
        if (hire is not null && (hire.Status != "active" || !hire.MemberActive))
            throw new OrderingException("This network driver needs an active, accepted hire.", 409, "driver_unavailable");
        // The profile capacity follows the verified person, including that person's
        // in-house jobs. The hire still determines payment origin independently.
        string? user = hire?.Assignment.DriverUserId;
        var capacity = hire?.Capacity ?? 0;
        if (hire is null)
        {
            await using var profile = DriverNetworkStore.Command(db, tx, """
                SELECT d.user_id,d.capacity FROM tide_network_drivers d
                JOIN bartide_enhanced_members m ON m.user_id=d.user_id
                WHERE m.tenant_id=@tenant AND m.id=@member AND m.active=1 AND m.role='driver'
                """, ("@tenant", tenant), ("@member", member));
            await using var reader = await profile.ExecuteReaderAsync(ct);
            if (!await reader.ReadAsync(ct)) return null;
            user = reader.GetString(0); capacity = reader.ReadInt32(1);
        }
        await using var command = DriverNetworkStore.Command(db, tx, """
            SELECT COUNT(*) FROM bartide_enhanced_orders o
            JOIN bartide_enhanced_members m ON m.tenant_id=o.tenant_id AND m.id=o.driver_id
            WHERE m.user_id=@user AND o.status NOT IN ('completed','cancelled','canceled','delivered')
            """, ("@user", user));
        if (Convert.ToInt64(await command.ExecuteScalarAsync(ct)) >= capacity)
            throw new OrderingException("This driver has reached their delivery capacity across clients.", 409, "driver_unavailable");
        return hire?.Assignment;
    }
}
