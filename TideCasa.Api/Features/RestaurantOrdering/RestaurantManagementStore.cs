using TideCasa.Api.Infrastructure;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Data.Common;
using TideCasa.Contracts;

namespace TideCasa.Api.Features.RestaurantOrdering;

public sealed partial class RestaurantOrderingStore
{
    public async Task<RestaurantMenuEditor> EditorAsync(string id, AuthUser user, CancellationToken ct)
    {
        await using var db = await database.OpenAsync(ct);
        using var tx = db.BeginTransaction(deferred: false);
        var venue = await ReadVenueAsync(db, tx, id, true, ct, allowPreparation: true);
        await RequireMenuManagerAsync(db, tx, venue, user, ct);
        var raw = await RawMenuAsync(db, tx, id, ct);
        var profile = (JsonObject)raw["venue"]!;
        // Editor reports stored availability, not the temporary ordering block list.
        var items = venue.Items.Select(item => item with { Available = Boolean(((JsonArray)raw["items"]!).OfType<JsonObject>()
            .Single(value => String(value, "id") == item.Id), "available", false) }).ToArray();
        var photos = new List<RestaurantPhotoOption>();
        await using (var query = Command(db, tx, "SELECT id,created_at FROM bartide_photos WHERE tenant_id=@id AND status='ready' ORDER BY created_at DESC LIMIT 100", ("@id", id)))
        {
            await using var reader = await query.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct)) photos.Add(new(reader.GetString(0), reader.GetString(1)));
        }
        await tx.CommitAsync(ct);
        return new(id, venue.Slug, venue.MenuVersion, new(venue.Name, String(profile, "area"), String(profile, "tagline"),
            String(profile, "hours_text"), String(profile, "website_url"), String(profile, "service_note")), venue.Categories, items, photos);
    }

    public async Task<RestaurantMenuEditor> SaveProfileAsync(string id, AuthUser user, SaveRestaurantProfileRequest request, CancellationToken ct)
    {
        if (request?.Profile is not { } p) throw new OrderingException("Enter the business details.");
        var name = Text(p.Name, "Business name", 160);
        var area = Text(p.Area, "Area", 160, true);
        var tagline = Text(p.Tagline, "Tagline", 250, true);
        var hours = Text(p.Hours, "Hours", 500, true, true);
        var website = Text(p.Website, "Website", 2048, true);
        if (website.Length > 0 && (!Uri.TryCreate(website, UriKind.Absolute, out var url) || url.Scheme != "https" || url.UserInfo.Length > 0))
            throw new OrderingException("Use an HTTPS website address.");
        var note = Text(p.ServiceNote, "Service note", 1000, true, true);
        await ChangeMenuAsync(id, user, request.ExpectedVersion, menu =>
        {
            var value = (JsonObject)menu["venue"]!;
            value["name"] = name; value["area"] = area; value["tagline"] = tagline; value["hours_text"] = hours;
            value["website_url"] = website; value["service_note"] = note;
        }, ct);
        return await EditorAsync(id, user, ct);
    }

    public async Task<RestaurantMenuEditor> SaveCategoryAsync(string id, AuthUser user, SaveRestaurantCategoryRequest request, CancellationToken ct)
    {
        if (request?.Category is not { } category || category.Id is null || !IdPattern.IsMatch(category.Id)) throw new OrderingException("Use a valid category identifier.");
        var name = Text(category.Name, "Category name", 80);
        await ChangeMenuAsync(id, user, request.ExpectedVersion, menu =>
        {
            var values = (JsonArray)menu["categories"]!;
            var entry = values.OfType<JsonObject>().FirstOrDefault(value => String(value, "id") == category.Id);
            if (entry is null) { if (values.Count >= 20) throw new OrderingException("Use no more than 20 categories."); entry = new() { ["id"] = category.Id }; values.Add(entry); }
            entry["label"] = name;
        }, ct);
        return await EditorAsync(id, user, ct);
    }

    public async Task<RestaurantMenuEditor> SaveItemAsync(string id, AuthUser user, SaveRestaurantItemRequest request, CancellationToken ct)
    {
        if (request?.Item is not { } item || item.Id is null || !IdPattern.IsMatch(item.Id) || item.CategoryId is null || !IdPattern.IsMatch(item.CategoryId))
            throw new OrderingException("Use a valid item and category identifier.");
        var name = Text(item.Name, "Item name", 160);
        var description = Text(item.Description, "Description", 1000, true, true);
        var label = Text(item.PriceLabel, "Price description", 160, item.PriceCents is not null);
        if (item.PriceCents is < 0 or > 1000000) throw new OrderingException("Price must be between $0 and $10,000.");
        if (request.PhotoId is { Length: > 0 } && !Guid.TryParseExact(request.PhotoId, "D", out _)) throw new OrderingException("Choose a photo from this restaurant.");
        await ChangeMenuAsync(id, user, request.ExpectedVersion, menu =>
        {
            if (!((JsonArray)menu["categories"]!).OfType<JsonObject>().Any(value => String(value, "id") == item.CategoryId)) throw new OrderingException("Choose a current category.");
            var values = (JsonArray)menu["items"]!;
            var entry = values.OfType<JsonObject>().FirstOrDefault(value => String(value, "id") == item.Id);
            if (entry is null) { if (values.Count >= 100) throw new OrderingException("Use no more than 100 menu items."); entry = new() { ["id"] = item.Id }; values.Add(entry); }
            entry["category"] = item.CategoryId; entry["name"] = name; entry["description"] = description;
            entry["price_cents"] = item.PriceCents; entry["price_label"] = string.IsNullOrEmpty(label) ? null : label; entry["available"] = item.Available;
            if (request.PhotoId is not null) entry["photo_src"] = request.PhotoId.Length == 0 ? null : "/media/" + request.PhotoId;
        }, ct, async (db, tx, token) =>
        {
            if (request.PhotoId is not { Length: > 0 }) return;
            await using var photo = Command(db, tx, "SELECT COUNT(*) FROM bartide_photos WHERE id=@photo AND tenant_id=@tenant AND status='ready'", ("@photo", request.PhotoId), ("@tenant", id));
            if (Convert.ToInt64(await photo.ExecuteScalarAsync(token)) != 1) throw new OrderingException("Choose a ready photo from this restaurant.");
        });
        return await EditorAsync(id, user, ct);
    }

    public async Task<RestaurantMenuEditor> RemoveEntryAsync(string id, AuthUser user, string entryId, bool category, RemoveRestaurantEntryRequest request, CancellationToken ct)
    {
        if (request is null) throw new OrderingException("Review the current menu first.");
        await ChangeMenuAsync(id, user, request.ExpectedVersion, menu =>
        {
            var values = (JsonArray)menu[category ? "categories" : "items"]!;
            var entry = values.OfType<JsonObject>().FirstOrDefault(value => String(value, "id") == entryId)
                ?? throw new OrderingException("Menu entry not found.", 404, "not_found");
            if (category && (values.Count == 1 || ((JsonArray)menu["items"]!).OfType<JsonObject>().Any(value => String(value, "category") == entryId)))
                throw new OrderingException("Move or remove this category's items first, and keep at least one category.");
            values.Remove(entry);
        }, ct);
        return await EditorAsync(id, user, ct);
    }

    private async Task ChangeMenuAsync(string id, AuthUser user, int expectedVersion, Action<JsonObject> change, CancellationToken ct,
        Func<DbConnection, DbTransaction, CancellationToken, Task>? validate = null)
    {
        await using var db = await database.OpenAsync(ct);
        using var tx = db.BeginTransaction(deferred: false);
        var venue = await ReadVenueAsync(db, tx, id, true, ct, allowPreparation: true);
        await RequireMenuManagerAsync(db, tx, venue, user, ct);
        if (venue.MenuVersion != expectedVersion) throw new OrderingException("The menu changed. Reload before saving.", 409, "stale_menu");
        var menu = await RawMenuAsync(db, tx, id, ct);
        if (validate is not null) await validate(db, tx, ct);
        change(menu);
        await using var update = Command(db, tx, "UPDATE bartide_customers SET name=@name,menu_json=@menu,version=version+1,updated_at=@now WHERE id=@id AND version=@version",
            ("@name", String((JsonObject)menu["venue"]!, "name")), ("@menu", menu.ToJsonString(Json)), ("@now", Stamp()), ("@id", id), ("@version", expectedVersion));
        if (await update.ExecuteNonQueryAsync(ct) != 1) throw new OrderingException("The menu changed. Reload before saving.", 409, "stale_menu");
        await ReadVenueAsync(db, tx, id, true, ct, allowPreparation: true);
        await tx.CommitAsync(ct);
    }

    private static async Task<JsonObject> RawMenuAsync(DbConnection db, DbTransaction tx, string id, CancellationToken ct)
    {
        await using var command = Command(db, tx, "SELECT menu_json FROM bartide_customers WHERE id=@id", ("@id", id));
        return Parse((string)(await command.ExecuteScalarAsync(ct) ?? throw Unavailable()));
    }
    private static async Task RequireMenuManagerAsync(DbConnection db, DbTransaction tx, Venue venue, AuthUser user, CancellationToken ct)
    {
        if (!user.IsPlatformOwner && user.UserId != venue.OwnerId && !await TenantStaffAccess.IsManagerAsync(db, tx, venue.Id, user, ct, allowPreparation: true))
            throw new OrderingException("Business owner or manager access is required.", 403, "forbidden");
    }

    public async Task<RestaurantOrderingSettings> SettingsAsync(string id, AuthUser user, CancellationToken ct)
    {
        await using var db = await database.OpenAsync(ct); using var tx = db.BeginTransaction(deferred: false);
        var venue = await ReadVenueAsync(db, tx, id, true, ct, allowPreparation: true); await RequireManagerAsync(db, tx, venue, user, ct, allowPreparation: true);
        var c = venue.Config; var options = Options(venue);
        await tx.CommitAsync(ct);
        return new(venue.ConfigVersion, venue.Status == "active" && Boolean(c, "accepting_orders", false), options.PickupEnabled, Boolean(c, "delivery_enabled", false),
            options.PayStaffEnabled, options.TipsEnabled, options.TaxBasisPoints, options.DeliveryFeeCents, options.DeliveryMinimumCents,
            Integer(c, "delivery_capacity", 1, 30, 5), options.DeliveryZips, options.PickupInstructions, options.PaymentInstructions, Boolean(c, "delivery_workflow_enabled", false), options.ContactPhone);
    }

    public async Task<RestaurantOrderingSettings> SaveSettingsAsync(string id, AuthUser user, SaveRestaurantOrderingSettingsRequest request, CancellationToken ct)
    {
        if (request is null || request.TaxBasisPoints is < 0 or > 2500 || request.DeliveryFeeCents is < 0 or > 5000
            || request.DeliveryMinimumCents is < 0 or > 100000 || request.DeliveryCapacity is < 1 or > 30
            || request.DeliveryZips is null || request.DeliveryZips.Count > 50 || request.DeliveryZips.Any(zip => zip is null || !ZipPattern.IsMatch(zip))
            || request.DeliveryZips.Distinct().Count() != request.DeliveryZips.Count) throw new OrderingException("Check tax, delivery amounts, capacity and ZIP codes.");
        if (request.AcceptingOrders && (request.TaxBasisPoints is null || !request.PayStaffEnabled))
            throw new OrderingException("Confirm the tax rate and enable Pay staff before opening orders. Phone payments are not ready yet.");
        if (request.DeliveryEnabled && request.DeliveryZips.Count == 0) throw new OrderingException("Add at least one delivery ZIP code.");
        var pickup = Text(request.PickupInstructions, "Pickup instructions", 500, true, true);
        var payment = Text(request.PaymentInstructions, "Payment instructions", 500, true, true);
        var phone = Text(request.ContactPhone, "Restaurant contact phone", 30, true);
        if (phone.Length > 0 && (phone.Count(char.IsAsciiDigit) is < 7 or > 15 || phone.Any(c => !char.IsAsciiDigit(c) && !"+()- .".Contains(c)))) throw new OrderingException("Enter a valid restaurant phone number.");
        await using var db = await database.OpenAsync(ct); using var tx = db.BeginTransaction(deferred: false);
        var venue = await ReadVenueAsync(db, tx, id, true, ct, allowPreparation: true); await RequireManagerAsync(db, tx, venue, user, ct, allowPreparation: true);
        if (venue.Status != "active" && request.AcceptingOrders)
            throw new OrderingException("Orders can open after your approved app launches. Save service settings with orders closed.", 409, "launch_required");
        if (venue.ConfigVersion != request.ExpectedVersion) throw new OrderingException("Settings changed. Reload before saving.", 409, "stale_settings");
        var c = venue.Config; c["delivery_workflow_enabled"] = request.DeliveryWorkflowEnabled; var checkout = c["checkout"] as JsonObject;
        if (checkout is null) { checkout = new(); c["checkout"] = checkout; }
        checkout["pickup_enabled"] = request.PickupEnabled; checkout["pay_staff_enabled"] = request.PayStaffEnabled; checkout["tips_enabled"] = request.TipsEnabled;
        c["accepting_orders"] = venue.Status == "active" && request.AcceptingOrders; c["delivery_enabled"] = request.DeliveryEnabled; c["tax_basis_points"] = request.TaxBasisPoints;
        c["delivery_fee_cents"] = request.DeliveryFeeCents; c["delivery_minimum_cents"] = request.DeliveryMinimumCents; c["delivery_capacity"] = request.DeliveryCapacity;
        c["delivery_zips"] = JsonSerializer.SerializeToNode(request.DeliveryZips, Json); c["pickup_instructions"] = pickup; c["payment_instructions"] = payment; c["contact_phone"] = phone;
        await using var save = Command(db, tx, "UPDATE bartide_enhanced_configs SET settings_json=@json,version=version+1,updated_at=@now WHERE tenant_id=@id AND version=@version",
            ("@json", c.ToJsonString(Json)), ("@now", Stamp()), ("@id", id), ("@version", request.ExpectedVersion));
        if (await save.ExecuteNonQueryAsync(ct) != 1) throw new OrderingException("Settings changed. Reload before saving.", 409, "stale_settings");
        if (!request.DeliveryWorkflowEnabled || !request.DeliveryEnabled)
        {
            await using var revokeLocations = Command(db, tx, "DELETE FROM tide_delivery_locations WHERE tenant_id=@id", ("@id", id));
            await revokeLocations.ExecuteNonQueryAsync(ct);
        }
        await tx.CommitAsync(ct);
        return await SettingsAsync(id, user, ct);
    }

    public async Task<RestaurantOperationsWorkspace> OperationsAsync(string id, AuthUser user, CancellationToken ct)
    {
        await using var db = await database.OpenAsync(ct); using var tx = db.BeginTransaction(deferred: false);
        var venue = await ReadVenueAsync(db, tx, id, true, ct);
        var access = await OperatorAsync(db, tx, venue, user, ct);
        var drivers = new List<RestaurantDriver>();
        if (access.Role is "owner" or "manager")
        {
            await using var command = Command(db, tx, "SELECT id,name,user_id FROM bartide_enhanced_members WHERE tenant_id=@id AND role='driver' AND active=1 ORDER BY name", ("@id", id));
            await using var reader = await command.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct)) drivers.Add(new(reader.GetString(0), reader.GetString(1), !reader.IsDBNull(2) && reader.GetString(2).Length > 0));
        }
        var orders = new List<RestaurantOperationOrder>();
        await using (var command = Command(db, tx,
            "SELECT payload_json,driver_id,version FROM bartide_enhanced_orders WHERE tenant_id=@id" + (access.Role == "driver" ? " AND driver_id=@member" : "")
            + " ORDER BY (status IN ('completed','cancelled')),created_at DESC LIMIT 200", ("@id", id), ("@member", (object?)access.MemberId ?? DBNull.Value)))
        {
            await using var reader = await command.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
            {
                var p = Parse(reader.GetString(0)); var driver = reader.IsDBNull(1) ? null : reader.GetString(1);
                var order = new RestaurantManagedOrder(Receipt(p), String(p, "customer_name"), String(p, "phone"), String(p, "address"), String(p, "zip"), String(p, "note"), reader.ReadInt32(2));
                orders.Add(new(order, driver, AllowedActions(order.Receipt, access.Role, driver, DeliveryWorkflow(venue, p)), DeliveryWorkflow(venue, p), AssignmentPlan(p)));
            }
        }
        await tx.CommitAsync(ct);
        return new(id, venue.Name, access.Role, drivers, orders, Boolean(venue.Config, "delivery_workflow_enabled", false) || orders.Any(o => o.Order.Receipt.Delivery is not null), String(venue.Config, "pickup_instructions"));
    }

    public async Task<RestaurantOperationsWorkspace> ChangeOrderAsync(string id, string orderId, AuthUser user, ChangeRestaurantOrderRequest request, CancellationToken ct)
    {
        if (request is null || !Guid.TryParseExact(orderId, "D", out _)) throw new OrderingException("Order not found.", 404, "not_found");
        await using var db = await database.OpenAsync(ct); using var tx = db.BeginTransaction(deferred: false);
        var venue = await ReadVenueAsync(db, tx, id, true, ct); var access = await OperatorAsync(db, tx, venue, user, ct);
        JsonObject payload; string? driver; int version;
        await using (var command = Command(db, tx, "SELECT payload_json,driver_id,version FROM bartide_enhanced_orders WHERE tenant_id=@tenant AND id=@id", ("@tenant", id), ("@id", orderId)))
        {
            await using var reader = await command.ExecuteReaderAsync(ct);
            if (!await reader.ReadAsync(ct)) throw new OrderingException("Order not found.", 404, "not_found");
            payload = Parse(reader.GetString(0)); driver = reader.IsDBNull(1) ? null : reader.GetString(1); version = reader.ReadInt32(2);
        }
        if (access.Role == "driver" && driver != access.MemberId) throw new OrderingException("This delivery is not assigned to you.", 403, "forbidden");
        if (request.ExpectedVersion != version) throw new OrderingException("This order changed. Reload before updating.", 409, "stale_order");
        var receipt = Receipt(payload);
        if (!AllowedActions(receipt, access.Role, driver, DeliveryWorkflow(venue, payload)).Contains(request.Action)) throw new OrderingException("That order action is not available.", 409, "invalid_transition");
        if (DeliveryWorkflow(venue, payload))
        {
            driver = await ChangeDeliveryAsync(db, tx, id, payload, driver, access.Role, user.UserId, request, ct);
        }
        else if (request.Action == "assign-driver")
        {
            await ValidateDriverAssignment(db, tx, id, request.DriverId, payload, ct);
            await CaptureDriverSource(db, tx, id, request.DriverId!, payload, ct);
            await using var assigned = Command(db, tx, "SELECT COUNT(*) FROM bartide_enhanced_members WHERE id=@member AND tenant_id=@tenant AND active=1 AND role='driver'",
                ("@member", (object?)request.DriverId ?? DBNull.Value), ("@tenant", id));
            if (Convert.ToInt64(await assigned.ExecuteScalarAsync(ct)) != 1) throw new OrderingException("Choose an active driver for this business.");
            driver = request.DriverId; payload["driver_id"] = driver;
        }
        else if (request.Action == "mark-paid")
        {
            if (!request.PaymentCollected) throw new OrderingException("Confirm that staff collected the full total, including the tip.");
            payload["payment_status"] = "paid_in_person";
        }
        else
        {
            if (request.Action == "out_for_delivery")
            {
                await using var active = Command(db, tx, "SELECT COUNT(*) FROM bartide_enhanced_members WHERE id=@member AND tenant_id=@tenant AND active=1 AND role='driver'", ("@member", (object?)driver ?? DBNull.Value), ("@tenant", id));
                if (Convert.ToInt64(await active.ExecuteScalarAsync(ct)) != 1) throw new OrderingException("Assign an active driver before dispatching.");
            }
            payload["status"] = request.Action;
        }
        await TideCasa.Api.Features.DriverPayments.DriverPaymentsStore.RecordCompletionAsync(db, tx, id, orderId, payload, driver, ct);
        var now = Stamp(); payload["version"] = version + 1; payload["updated_at"] = now;
        var history = payload["history"] as JsonArray;
        if (history is null) { history = new(); payload["history"] = history; }
        history.Add(new JsonObject { ["status"] = String(payload, "status"), ["action"] = request.Action, ["at"] = now, ["actor_id"] = user.UserId, ["delivery_note"] = Text(request.DeliveryNote, "Delivery note", 300, true, true), ["driver_id"] = driver });
        await using var save = Command(db, tx, "UPDATE bartide_enhanced_orders SET payload_json=@json,status=@status,driver_id=@driver,version=version+1,updated_at=@now WHERE tenant_id=@tenant AND id=@id AND version=@version",
            ("@json", payload.ToJsonString(Json)), ("@status", String(payload, "status")), ("@driver", (object?)driver ?? DBNull.Value), ("@now", now), ("@tenant", id), ("@id", orderId), ("@version", version));
        if (await save.ExecuteNonQueryAsync(ct) != 1) throw new OrderingException("This order changed. Reload before updating.", 409, "stale_order");
        if (request.Action is "assign-driver" or "cancelled" or "confirm-delivery" or "completed")
            await DeleteLocation(db, tx, id, orderId, ct);
        await tx.CommitAsync(ct);
        return await OperationsAsync(id, user, ct);
    }

    private static IReadOnlyList<string> AllowedActions(RestaurantOrderReceipt receipt, string role, string? driver, bool enhanced = false)
    {
        if (enhanced) return DeliveryActions(receipt, role, driver);
        var actions = new List<string>(); var status = receipt.Status; var delivery = receipt.Quote.Fulfillment == "delivery";
        if (status is "completed" or "cancelled") return actions;
        // A verified full refund permits an owner to close the remaining work.
        // This changes fulfillment only; it never requests or assumes a refund.
        if (receipt.Quote.PaymentMethod == "phone" && receipt.PaymentStatus == "refunded")
            return role == "owner" ? ["cancelled"] : actions;
        if (receipt.Quote.PaymentMethod == "phone" && (receipt.PaymentStatus != "paid"
            || status is "awaiting_payment" or "paid_needs_review" or "payment_review")) return actions;
        if (role != "driver")
        {
            if (status == "new") actions.Add("accepted");
            if (role != "server" && status == "accepted") actions.Add("preparing");
            if (role != "server" && status == "preparing") actions.Add("ready");
        }
        if (role != "server" && delivery && status == "ready" && driver is not null) actions.Add("out_for_delivery");
        if ((delivery ? status == "out_for_delivery" && role != "server" : status == "ready" && role != "driver") && receipt.PaymentStatus is "paid" or "paid_in_person") actions.Add("completed");
        if (role is "owner" or "manager")
        {
            // Cancelling a paid order requires a separately verified refund workflow.
            if (receipt.PaymentStatus == "unpaid") actions.Add("cancelled");
            if (delivery) actions.Add("assign-driver");
        }
        if ((role is "owner" or "manager" or "server") && receipt.Quote.PaymentMethod == "staff" && receipt.PaymentStatus == "unpaid") actions.Add("mark-paid");
        return actions;
    }

    private static async Task<(string Role, string? MemberId)> OperatorAsync(DbConnection db, DbTransaction tx, Venue venue, AuthUser user, CancellationToken ct)
    {
        if (!Boolean(venue.Config, "enabled", false)) throw new OrderingException("Restaurant operations are unavailable.", 403, "forbidden");
        if (user.IsPlatformOwner || user.UserId == venue.OwnerId) return ("owner", null);
        var member = await TenantStaffAccess.FindAsync(db, tx, venue.Id, user, ct);
        if (member is null) throw new OrderingException("Active team access is required.", 403, "forbidden");
        if (member.Role == "driver") await TideCasa.Api.Features.DriverNetwork.DriverNetworkPolicy.RequireActiveHireAsync(db, tx, venue.Id, member.Id, ct);
        return (member.Role, member.Id);
    }
}
