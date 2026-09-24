using System.Data.Common;
using System.Text.Json;
using System.Text.Json.Nodes;
using TideCasa.Api.Features.Authentication;
using TideCasa.Api.Infrastructure;
using TideCasa.Contracts;

namespace TideCasa.Api.Features.RestaurantOrdering;

public sealed partial class RestaurantOrderingStore
{
    private static bool LocationEnabled(Venue v) => Boolean(v.Config, "delivery_location_enabled", false)
        && Boolean(v.Config, "delivery_workflow_enabled", false) && Boolean(v.Config, "delivery_enabled", false);
    private sealed record LocationRow(string Order, string Driver, string User, string Assignment, string Hash,
        bool Simulated, long Sequence, string Started, string Received, string Position);
    private static async Task<List<LocationRow>> LocationRows(DbConnection db, DbTransaction tx, string tenant, CancellationToken ct)
    {
        var rows = new List<LocationRow>();
        await using var q = Command(db, tx, "SELECT order_id,driver_id,user_id,assignment_at,session_hash,simulated,sequence,started_at,received_at,position_json FROM tide_delivery_locations WHERE tenant_id=@tenant", ("@tenant", tenant));
        await using var r = await q.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct)) rows.Add(new(r.GetString(0),r.GetString(1),r.GetString(2),r.GetString(3),r.GetString(4),Convert.ToInt64(r.GetValue(5)) == 1,Convert.ToInt64(r.GetValue(6)),r.GetString(7),r.GetString(8),r.GetString(9)));
        return rows;
    }
    private static async Task<JsonObject?> LocationOrder(DbConnection db, DbTransaction tx, string tenant, string order, string driver, string user, CancellationToken ct)
    {
        await using var q = Command(db, tx, """
            SELECT o.payload_json FROM bartide_enhanced_orders o
            JOIN bartide_enhanced_members m ON m.tenant_id=o.tenant_id AND m.id=o.driver_id
            WHERE o.tenant_id=@tenant AND o.id=@order AND o.driver_id=@driver AND o.status='out_for_delivery'
              AND m.role='driver' AND m.active=1 AND m.user_id=@user
            """, ("@tenant",tenant),("@order",order),("@driver",driver),("@user",user));
        var json = await q.ExecuteScalarAsync(ct) as string;
        return json is null ? null : Parse(json);
    }
    private static bool CurrentLocation(LocationRow row, JsonObject? order) => order?["delivery"] is JsonObject d
        && String(d,"assigned_at") == row.Assignment && !string.IsNullOrEmpty(String(d,"acknowledged_at"))
        && DateTimeOffset.TryParse(row.Received, out var received) && DateTimeOffset.UtcNow - received < TimeSpan.FromMinutes(5);
    private static DeliveryLocationView LocationView(LocationRow row, JsonObject order)
    {
        var point = row.Position == "" ? null : JsonSerializer.Deserialize<DeliveryLocationPoint>(row.Position, Json);
        var now = DateTimeOffset.UtcNow;
        var fresh = point is not null && DateTimeOffset.TryParse(point.CapturedAt,out var captured) && now-captured < TimeSpan.FromSeconds(60)
            && now-DateTimeOffset.Parse(row.Received) < TimeSpan.FromSeconds(60);
        return new(row.Order, String(order["delivery"] as JsonObject ?? new(),"driver_name","Your driver"),row.Simulated,
            point is null ? "waiting" : fresh ? "recent" : "stale",point,point is null ? null : row.Received,now.ToString("O"));
    }
    private static async Task DeleteLocation(DbConnection db, DbTransaction tx, string tenant, string order, CancellationToken ct)
    {
        await using var q = Command(db, tx,"DELETE FROM tide_delivery_locations WHERE tenant_id=@tenant AND order_id=@order",("@tenant",tenant),("@order",order));
        await q.ExecuteNonQueryAsync(ct);
    }
    public async Task<DeliveryLocationBoard> LocationsAsync(string id, AuthUser user, bool simulated, CancellationToken ct)
    {
        await using var db = await database.OpenAsync(ct); using var tx = db.BeginTransaction(deferred:false);
        var v = await ReadVenueAsync(db,tx,id,true,ct); var access = await OperatorAsync(db,tx,v,user,ct);
        if (access.Role is not ("owner" or "manager" or "driver")) throw new OrderingException("Delivery location access is restricted.",403,"forbidden");
        var views = new List<DeliveryLocationView>();
        if (LocationEnabled(v)) foreach (var row in await LocationRows(db,tx,id,ct))
        {
            if (access.Role == "driver" && (row.Driver != access.MemberId || row.User != user.UserId)) continue;
            var order = await LocationOrder(db,tx,id,row.Order,row.Driver,row.User,ct);
            if (CurrentLocation(row,order)) views.Add(LocationView(row,order!));
        }
        await tx.CommitAsync(ct);
        return new(LocationEnabled(v),simulated,v.ConfigVersion,views);
    }
    public async Task<DeliveryLocationBoard> SetLocationsAsync(string id, AuthUser user, DeliveryLocationSettings r, bool simulated, CancellationToken ct)
    {
        await using var db = await database.OpenAsync(ct); using var tx = db.BeginTransaction(deferred:false);
        var v = await ReadVenueAsync(db,tx,id,true,ct); await RequireManagerAsync(db,tx,v,user,ct);
        if (r.ExpectedVersion != v.ConfigVersion) throw new OrderingException("Settings changed. Refresh before saving.",409,"stale_settings");
        if (r.Enabled && (!Boolean(v.Config,"delivery_workflow_enabled",false) || !Boolean(v.Config,"delivery_enabled",false)))
            throw new OrderingException("Enable the delivery workflow first.");
        v.Config["delivery_location_enabled"] = r.Enabled;
        await using var q = Command(db,tx,"UPDATE bartide_enhanced_configs SET settings_json=@json,version=version+1,updated_at=@at WHERE tenant_id=@id AND version=@version",
            ("@json",v.Config.ToJsonString(Json)),("@at",Stamp()),("@id",id),("@version",r.ExpectedVersion));
        if (await q.ExecuteNonQueryAsync(ct) != 1) throw new OrderingException("Settings changed.",409,"stale_settings");
        if (!r.Enabled) { await using var stop = Command(db,tx,"DELETE FROM tide_delivery_locations WHERE tenant_id=@id",("@id",id)); await stop.ExecuteNonQueryAsync(ct); }
        await tx.CommitAsync(ct); return await LocationsAsync(id,user,simulated,ct);
    }
    public async Task<DeliveryLocationSession> StartLocationAsync(string id, string orderId, AuthUser user, DeliveryLocationStart r, bool simulated, CancellationToken ct)
    {
        if (!r.Consent) throw new OrderingException("Choose to share this delivery's location first.");
        await using var db = await database.OpenAsync(ct); using var tx = db.BeginTransaction(deferred:false);
        var v = await ReadVenueAsync(db,tx,id,true,ct); var access = await OperatorAsync(db,tx,v,user,ct);
        if (access.Role != "driver" || access.MemberId is null) throw new OrderingException("Only the assigned driver can share their location.",403,"forbidden");
        if (!LocationEnabled(v)) throw new OrderingException("Location sharing is disabled.",409,"location_disabled");
        var order = await LocationOrder(db,tx,id,orderId,access.MemberId,user.UserId,ct);
        if (order?["delivery"] is not JsonObject d || string.IsNullOrEmpty(String(d,"acknowledged_at"))) throw new OrderingException("Collect your assigned delivery before sharing.",409,"delivery_inactive");
        foreach(var existing in await LocationRows(db,tx,id,ct))
        {
            if (existing.Driver != access.MemberId) continue;
            var previous = await LocationOrder(db,tx,id,existing.Order,existing.Driver,existing.User,ct);
            if (CurrentLocation(existing,previous) && existing.Order != orderId) throw new OrderingException("Stop sharing your other delivery first.",409,"other_delivery_active");
            await DeleteLocation(db,tx,id,existing.Order,ct);
        }
        var token = Token(); var now = Stamp();
        var row = new LocationRow(orderId,access.MemberId,user.UserId,String(d,"assigned_at"),Hash(token),simulated,0,now,now,"");
        await using var insert = Command(db,tx,"""
            INSERT INTO tide_delivery_locations(tenant_id,driver_id,order_id,user_id,assignment_at,session_hash,simulated,sequence,started_at,received_at,position_json)
            VALUES(@tenant,@driver,@order,@user,@assignment,@hash,@simulated,0,@now,@now,'')
            """,("@tenant",id),("@driver",row.Driver),("@order",orderId),("@user",user.UserId),("@assignment",row.Assignment),("@hash",row.Hash),("@simulated",simulated?1:0),("@now",now));
        await insert.ExecuteNonQueryAsync(ct); await tx.CommitAsync(ct);
        return new(token,simulated,LocationView(row,order));
    }
    public async Task<DeliveryLocationView> UpdateLocationAsync(string id, string orderId, AuthUser user, DeliveryLocationUpdate r, bool simulated, CancellationToken ct)
    {
        if (!ValidTrackingKey(r.Session) || r.Sequence < 1 || r.Sequence > 1_000_000) throw new OrderingException("Invalid location session.");
        // Shared demonstration accounts can NEVER upload a visitor's coordinates.
        if (simulated && r.Point is not null) throw new OrderingException("The shared demo accepts simulated locations only.",403,"simulation_only");
        var now = DateTimeOffset.UtcNow;
        if (!simulated && (r.Point is not { } p || !double.IsFinite(p.Latitude) || !double.IsFinite(p.Longitude) || !double.IsFinite(p.AccuracyMeters)
            || p.Latitude is < -90 or > 90 || p.Longitude is < -180 or > 180 || p.AccuracyMeters is < 0 or > 10000
            || !DateTimeOffset.TryParse(p.CapturedAt,out var at) || now-at > TimeSpan.FromSeconds(60) || at-now > TimeSpan.FromSeconds(10)))
            throw new OrderingException("A fresh, valid location is required.");
        await using var db = await database.OpenAsync(ct); using var tx = db.BeginTransaction(deferred:false);
        var v = await ReadVenueAsync(db,tx,id,true,ct); var access = await OperatorAsync(db,tx,v,user,ct);
        if (access.Role != "driver") throw new OrderingException("Driver access required.",403,"forbidden");
        var row = (await LocationRows(db,tx,id,ct)).FirstOrDefault(x=>x.Order == orderId && x.Driver == access.MemberId && x.User == user.UserId && x.Hash == Hash(r.Session));
        if (row is null || row.Simulated != simulated || !LocationEnabled(v)) throw new OrderingException("Start location sharing again.",409,"session_expired");
        var order = await LocationOrder(db,tx,id,orderId,row.Driver,row.User,ct);
        if (!CurrentLocation(row,order)) throw new OrderingException("This delivery is no longer sharing.",409,"session_expired");
        if (r.Sequence <= row.Sequence) throw new OrderingException("An older update was ignored.",409,"old_location");
        if (row.Sequence > 0 && now-DateTimeOffset.Parse(row.Received) < TimeSpan.FromSeconds(15)) throw new OrderingException("Please wait for the next location update.",429,"location_throttled");
        if (!simulated && row.Position != "" && DateTimeOffset.Parse(r.Point!.CapturedAt) <= DateTimeOffset.Parse(JsonSerializer.Deserialize<DeliveryLocationPoint>(row.Position,Json)!.CapturedAt))
            throw new OrderingException("An older location was ignored.",409,"old_location");
        // A fixed fictional St Pete Beach journey, independent of any customer address.
        var step = Math.Min(20,(now-DateTimeOffset.Parse(row.Started)).TotalSeconds/15);
        var point = simulated ? new DeliveryLocationPoint(27.7256+step*0.00015,-82.7415+step*0.00008,18,now.ToString("O")) : r.Point!;
        var changed = row with { Sequence=r.Sequence,Received=now.ToString("O"),Position=JsonSerializer.Serialize(point,Json) };
        await using var q = Command(db,tx,"UPDATE tide_delivery_locations SET sequence=@seq,received_at=@received,position_json=@point WHERE tenant_id=@tenant AND order_id=@order AND session_hash=@hash",
            ("@seq",r.Sequence),("@received",changed.Received),("@point",changed.Position),("@tenant",id),("@order",orderId),("@hash",row.Hash));
        await q.ExecuteNonQueryAsync(ct); await tx.CommitAsync(ct); return LocationView(changed,order!);
    }
    public async Task<bool> StopLocationAsync(string id,string orderId,AuthUser user,DeliveryLocationStop r,CancellationToken ct)
    {
        if (!ValidTrackingKey(r.Session)) throw new OrderingException("Invalid location session.");
        await using var db=await database.OpenAsync(ct); using var tx=db.BeginTransaction(deferred:false);
        var v=await ReadVenueAsync(db,tx,id,true,ct);var access=await OperatorAsync(db,tx,v,user,ct);
        if (access.Role != "driver") throw new OrderingException("Driver access required.",403,"forbidden");
        await using var q=Command(db,tx,"DELETE FROM tide_delivery_locations WHERE tenant_id=@tenant AND order_id=@order AND driver_id=@driver AND user_id=@user AND session_hash=@hash",
            ("@tenant",id),("@order",orderId),("@driver",access.MemberId!),("@user",user.UserId),("@hash",Hash(r.Session)));
        await q.ExecuteNonQueryAsync(ct);await tx.CommitAsync(ct);return true;
    }
    public async Task<DeliveryLocationView?> CustomerLocationAsync(string slug, RestaurantTrackingRequest request,CancellationToken ct)
    {
        // Reuse the receipt's secret-bound authentication; no public order-number lookup.
        await TrackAsync(slug,request,ct);
        await using var db=await database.OpenAsync(ct);using var tx=db.BeginTransaction(deferred:true);
        var v=await ReadVenueAsync(db,tx,slug,false,ct);if(!LocationEnabled(v))return null;
        var row=(await LocationRows(db,tx,v.Id,ct)).FirstOrDefault(x=>x.Order==request.OrderId);
        if(row is null)return null;
        var order=await LocationOrder(db,tx,v.Id,row.Order,row.Driver,row.User,ct);
        return CurrentLocation(row,order)?LocationView(row,order!):null;
    }
}
