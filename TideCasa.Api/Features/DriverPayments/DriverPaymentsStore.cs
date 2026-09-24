using System.Data.Common;
using System.Text.Json;
using System.Text.Json.Nodes;
using TideCasa.Api.Features.RestaurantOrdering;
using TideCasa.Api.Infrastructure;
using TideCasa.Contracts;

namespace TideCasa.Api.Features.DriverPayments;

public sealed partial class DriverPaymentsStore(ApplicationDatabase database, DriverPaymentOptions options, DriverStripeProvider provider)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private static string Now() => DateTimeOffset.UtcNow.ToString("O");
    private static OrderingException Stale() => new("This payment changed. Refresh before approving.",409,"stale_payment");
    private static OrderingException Review() => new("This payment needs review. Do not send a second payment.",409,"payment_review");
    internal static int Fee(int amount, bool network) => network ? checked((amount * 5 + 50) / 100) : 0;

    // Called in the same locked transaction as handoff, so a retry cannot create another debt.
    public static async Task RecordCompletionAsync(DbConnection db, DbTransaction tx, string tenant, string orderId,
        JsonObject payload, string? driver, CancellationToken ct)
    {
        if (driver is null || payload["fulfillment"]?.GetValue<string>() != "delivery"
            || payload["status"]?.GetValue<string>() is not ("delivered" or "completed")) return;
        var members = await Rows(db,tx,"SELECT name,COALESCE(user_id,'') FROM bartide_enhanced_members WHERE tenant_id=@tenant AND id=@driver",
            r => (Name:r.GetString(0),User:r.GetString(1)),ct,("@tenant",tenant),("@driver",driver));
        if (members.Count != 1) throw new OrderingException("The completed delivery driver could not be verified.",409,"driver_unavailable");
        var network = payload["network_assignment"] as JsonObject;
        var user = network?["driver_user_id"]?.GetValue<string>() ?? members[0].User;
        var amount = network?["pay_per_delivery_cents"]?.GetValue<int>() ?? 0;
        if (network is not null && (string.IsNullOrWhiteSpace(user) || amount is < 50 or > 100000)) throw Review();
        await Run(db,tx,"""
            INSERT INTO tide_driver_payables(id,tenant_id,order_id,member_id,user_id,driver_name,source,hire_id,agreed_pay_cents,completed_at,order_number)
            VALUES(@id,@tenant,@id,@driver,@user,@name,@source,@hire,@amount,@now,@number) ON CONFLICT(order_id) DO NOTHING
            """,ct,("@id",orderId),("@tenant",tenant),("@driver",driver),("@user",user),("@name",members[0].Name),
            ("@source",network is null ? "own" : "network"),("@hire",network?["hire_id"]?.GetValue<string>()),
            ("@amount",amount),("@now",Now()),("@number",payload["number"]?.GetValue<string>() ?? orderId[..8]));
    }

    private static async Task<(string Name,bool Owner)> Access(DbConnection db,DbTransaction tx,string tenant,AuthUser user,CancellationToken ct)
    {
        var venues=await Rows(db,tx,"SELECT name,user_id FROM bartide_customers WHERE id=@id AND vertical='bartide' AND status='active'",
            r=>(Name:r.GetString(0),Owner:r.IsDBNull(1)?"":r.GetString(1)),ct,("@id",tenant));
        if(venues.Count!=1) throw new OrderingException("Restaurant not found.",404,"not_found");
        var owner=user.IsPlatformOwner || venues[0].Owner==user.UserId;
        if(!owner && !await TenantStaffAccess.IsManagerAsync(db,tx,tenant,user,ct)) throw new OrderingException("Restaurant owner or manager access required.",403,"forbidden");
        return(venues[0].Name,owner);
    }
    public async Task<ClientDriverPaymentsWorkspace> ClientWorkspaceAsync(string tenant,AuthUser user,CancellationToken ct,int page=0)
    {
        await using var db=await database.OpenAsync(ct); using var tx=db.BeginTransaction(deferred:false);
        ValidatePage(page);
        var access=await Access(db,tx,tenant,user,ct);var lines=await Lines(db,tx,tenant,null,ct,page:page);
        if(!access.Owner)lines=lines.Select(line=>line with{CheckoutUrl=null}).ToList();
        await tx.CommitAsync(ct);return new(tenant,access.Name,access.Owner,options.PaymentsEnabled,options.Sandbox,lines.Take(500).ToList(),page,lines.Count>500);
    }
    public async Task<DriverEarningsWorkspace> EarningsAsync(AuthUser user,CancellationToken ct,int page=0)
    {
        ValidatePage(page);var status=await PayoutStatusAsync(user,ct);
        await using var db=await database.OpenAsync(ct);using var tx=db.BeginTransaction(deferred:true);
        var lines=await Lines(db,tx,null,user.UserId,ct,page:page);
        return new(status,lines.Take(500).ToList(),page,lines.Count>500);
    }
    private static void ValidatePage(int page)
    {if(page is <0 or >100000)throw new OrderingException("Choose a valid payment history page.");}
    private async Task<List<DriverPaymentLine>> Lines(DbConnection db,DbTransaction tx,string? tenant,string? user,CancellationToken ct,string? paymentId=null,int page=0)
    {
        return await Rows(db,tx,"""
            SELECT p.id,p.tenant_id,c.name,p.order_id,p.order_number,p.driver_name,p.source,
              COALESCE(s.driver_pay_cents,p.agreed_pay_cents),s.fee_cents,s.total_cents,COALESCE(s.status,'pending_approval'),
              p.completed_at,COALESCE(s.version,0),a.checkout_url
            FROM tide_driver_payables p JOIN bartide_customers c ON c.id=p.tenant_id
            LEFT JOIN tide_driver_payment_states s ON s.payment_id=p.id AND s.environment=@env
            LEFT JOIN tide_driver_payment_attempts a ON a.id=s.current_attempt_id AND a.environment=s.environment
            WHERE (@tenant IS NULL OR p.tenant_id=@tenant) AND (@user IS NULL OR p.user_id=@user)
              AND (@payment IS NULL OR p.id=@payment)
            ORDER BY p.completed_at DESC,p.id LIMIT 501 OFFSET @offset
            """,r=>{
                var amount=r.ReadInt32(7);var fee=r.IsDBNull(8)?Fee(amount,r.GetString(6)=="network"):r.ReadInt32(8);
                return new DriverPaymentLine(r.GetString(0),r.GetString(1),r.GetString(2),r.GetString(3),r.GetString(4),r.GetString(5),r.GetString(6),
                    amount,fee,r.IsDBNull(9)?amount+fee:r.ReadInt32(9),r.GetString(10),r.GetString(11),r.ReadInt32(12),
                    user is null && !r.IsDBNull(13) && r.GetString(10)=="open"?r.GetString(13):null);
            },ct,("@tenant",tenant),("@user",user),("@env",options.Environment),("@payment",paymentId),("@offset",checked(page*500)));
    }
    private static async Task<int> Run(DbConnection db,DbTransaction tx,string sql,CancellationToken ct,params(string Key,object? Value)[] values)
    { await using var c=Command(db,tx,sql,values);return await c.ExecuteNonQueryAsync(ct); }
    private static async Task<List<T>> Rows<T>(DbConnection db,DbTransaction tx,string sql,Func<DbDataReader,T> read,CancellationToken ct,params(string Key,object? Value)[] values)
    { await using var c=Command(db,tx,sql,values);await using var r=await c.ExecuteReaderAsync(ct);var list=new List<T>();while(await r.ReadAsync(ct))list.Add(read(r));return list; }
    private static DbCommand Command(DbConnection db,DbTransaction tx,string sql,params(string Key,object? Value)[] values)
    { var c=db.CreateCommand();c.Transaction=tx;c.CommandText=sql;foreach(var(k,v)in values)c.Parameters.AddWithValue(k,v??DBNull.Value);return c; }
}
