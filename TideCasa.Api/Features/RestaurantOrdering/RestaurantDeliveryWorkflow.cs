using System.Data.Common;
using System.Text.Json.Nodes;
using TideCasa.Api.Infrastructure;
using TideCasa.Contracts;

namespace TideCasa.Api.Features.RestaurantOrdering;

public sealed partial class RestaurantOrderingStore
{
    private static bool DeliveryWorkflow(Venue venue, JsonObject payload) => String(payload, "fulfillment") == "delivery"
        && (Boolean(venue.Config, "delivery_workflow_enabled", false) || payload["delivery"] is JsonObject);

    private static RestaurantDeliveryProgress? DeliveryProgress(JsonObject payload)
    {
        if (payload["delivery"] is not JsonObject d) return null;
        string? Value(string key) => d[key]?.GetValue<string>();
        return new(Value("driver_name"), Value("assigned_at"), Value("acknowledged_at"), Value("collected_at"),
            Value("delivered_at"), Value("problem_code"), String(payload, "updated_at", String(payload, "created_at")));
    }

    private static IReadOnlyList<string> DeliveryActions(RestaurantOrderReceipt receipt, string role, string? driver)
    {
        var actions = new List<string>(); var status = receipt.Status; var d = receipt.Delivery;
        var manager = role is "owner" or "manager";
        if (status is "completed" or "cancelled") return actions;
        if (receipt.Quote.PaymentMethod == "phone" && receipt.PaymentStatus != "paid")
            return receipt.PaymentStatus == "refunded" && role == "owner" ? ["cancelled"] : actions;
        if (role != "driver")
        {
            if (status == "new") actions.Add("accepted");
            if (role != "server" && status == "accepted") actions.Add("preparing");
            if (role != "server" && status == "preparing") actions.Add("ready");
        }
        if (status != "delivered")
        {
            if (manager) actions.Add("assign-driver");
            if (role == "driver" && driver is not null && d?.AcknowledgedAt is null) actions.Add("acknowledge-delivery");
            if ((manager || role == "driver") && driver is not null)
            {
                if (d?.ProblemCode is null)
                {
                    actions.Add("report-delivery-problem");
                    if (status == "ready" && d?.AcknowledgedAt is not null) actions.Add("out_for_delivery");
                    if (status == "out_for_delivery" && d?.AcknowledgedAt is not null) actions.Add("confirm-delivery");
                }
                else if (manager) actions.Add("resolve-delivery-problem");
            }
            if (manager && receipt.PaymentStatus == "unpaid") actions.Add("cancelled");
        }
        if ((manager || role == "server") && receipt.Quote.PaymentMethod == "staff" && receipt.PaymentStatus == "unpaid") actions.Add("mark-paid");
        return actions;
    }

    // Delivery evidence is additive JSON. Existing quote/payment fields retain their meaning.
    private static async Task<string?> ChangeDeliveryAsync(DbConnection db, DbTransaction tx, string tenant,
        JsonObject payload, string? driver, string role, string actor, ChangeRestaurantOrderRequest request, CancellationToken ct)
    {
        var d = payload["delivery"] as JsonObject;
        if (d is null) { d = new(); payload["delivery"] = d; }
        var now = Stamp();
        var note = Text(request.DeliveryNote, "Delivery note", 300, true, true);
        if (request.Action == "assign-driver")
        {
            if (driver == request.DriverId) throw new OrderingException("This driver is already assigned.", 409, "invalid_transition");
            if (driver is not null && note.Length == 0) throw new OrderingException("Enter a reason for reassigning this delivery.");
            await ValidateDriverAssignment(db, tx, tenant, request.DriverId, payload, ct);
            await CaptureDriverSource(db, tx, tenant, request.DriverId!, payload, ct);
            await using var command = Command(db, tx, "SELECT name FROM bartide_enhanced_members WHERE tenant_id=@tenant AND id=@driver AND active=1 AND role='driver' AND user_id IS NOT NULL AND user_id<>''",
                ("@tenant", tenant), ("@driver", (object?)request.DriverId ?? DBNull.Value));
            var name = await command.ExecuteScalarAsync(ct) as string ?? throw new OrderingException("Choose a driver with a connected sign-in account.");
            driver = request.DriverId; payload["driver_id"] = driver;
            payload.Remove("dispatch_plan");
            await using var stampDriver = Command(db, tx, "UPDATE tide_delivery_drivers SET last_assigned_at=@now WHERE tenant_id=@tenant AND driver_id=@driver",
                ("@now", now), ("@tenant", tenant), ("@driver", (object?)driver ?? DBNull.Value));
            await stampDriver.ExecuteNonQueryAsync(ct);
            d["driver_name"] = name.Split(' ', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault() ?? "Your driver";
            d["assigned_at"] = now; d["acknowledged_at"] = null; d["collected_at"] = null;
            d["problem_code"] = null;
            if (String(payload, "status") == "out_for_delivery") payload["status"] = "ready";
        }
        else if (request.Action == "acknowledge-delivery") d["acknowledged_at"] = now;
        else if (request.Action == "report-delivery-problem")
        {
            if (request.ProblemCode is not ("customer-unavailable" or "address-issue" or "unable-to-deliver")) throw new OrderingException("Choose a delivery problem.");
            d["problem_code"] = request.ProblemCode; d["problem_at"] = now;
        }
        else if (request.Action == "resolve-delivery-problem")
        {
            if (note.Length == 0) throw new OrderingException("Record how the delivery problem was resolved.");
            d["problem_code"] = null;
        }
        else if (request.Action is "out_for_delivery" or "confirm-delivery")
        {
            await using var active = Command(db, tx, "SELECT COUNT(*) FROM bartide_enhanced_members WHERE tenant_id=@tenant AND id=@driver AND role='driver' AND active=1 AND user_id IS NOT NULL AND user_id<>''", ("@tenant", tenant), ("@driver", (object?)driver ?? DBNull.Value));
            if (Convert.ToInt64(await active.ExecuteScalarAsync(ct)) != 1) throw new OrderingException("Assign an active, connected driver first.");
            if (role != "driver" && note.Length == 0) throw new OrderingException("Record a reason for confirming this step on the driver's behalf.");
            if (request.Action == "out_for_delivery") { d["collected_at"] = now; payload["status"] = "out_for_delivery"; }
            else
            {
                d["delivered_at"] = now; d["delivered_by"] = actor;
                payload["status"] = String(payload, "payment_status") is "paid" or "paid_in_person" ? "completed" : "delivered";
            }
        }
        else if (request.Action == "mark-paid")
        {
            if (!request.PaymentCollected) throw new OrderingException("Confirm that payment was actually collected.");
            payload["payment_status"] = "paid_in_person";
            if (String(payload, "status") == "delivered") payload["status"] = "completed";
        }
        else { payload["status"] = request.Action; if (request.Action == "cancelled") d["problem_code"] = null; }
        return driver;
    }
}
