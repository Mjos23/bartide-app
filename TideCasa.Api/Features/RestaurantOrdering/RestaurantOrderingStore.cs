using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using System.Data.Common;
using TideCasa.Api.Infrastructure;
using TideCasa.Contracts;
using TideCasa.Api.Features.MerchantPayments;

namespace TideCasa.Api.Features.RestaurantOrdering;

public sealed class OrderingException(string message, int status = 400, string code = "invalid_order") : Exception(message)
{
    public int Status { get; } = status;
    public string Code { get; } = code;
}

public sealed partial class RestaurantOrderingStore(ApplicationDatabase database, MerchantPaymentOptions merchantOptions)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private static readonly Regex IdPattern = new("^[a-z][a-z0-9-]{0,63}$", RegexOptions.CultureInvariant);
    private static readonly Regex TokenPattern = new("^[a-f0-9]{64}$", RegexOptions.CultureInvariant);
    private static readonly Regex ZipPattern = new("^[0-9]{5}$", RegexOptions.CultureInvariant);
    private static string Hash(string value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();
    private static string Token() => Convert.ToHexString(RandomNumberGenerator.GetBytes(32)).ToLowerInvariant();
    private static string Stamp() => DateTimeOffset.UtcNow.ToString("O");

    public async Task<RestaurantMenu> MenuAsync(string slug, string? tableToken, CancellationToken ct)
    {
        await using var db = await database.OpenAsync(ct);
        using var transaction = db.BeginTransaction(deferred: true);
        var venue = await ReadVenueAsync(db, transaction, slug, false, ct);
        var table = ResolveTable(venue, tableToken, required: false);
        return new(venue.Slug, venue.Name, venue.MenuVersion, "USD", venue.Categories, venue.Items,
            Options(venue, await PhoneAvailableAsync(db, transaction, venue.Id, ct)), table);
    }

    public async Task<RestaurantQuote> QuoteAsync(string slug, RestaurantQuoteRequest request, CancellationToken ct)
    {
        await using var db = await database.OpenAsync(ct);
        using var transaction = db.BeginTransaction(deferred: true);
        var venue = await ReadVenueAsync(db, transaction, slug, false, ct);
        return Quote(venue, request, await PhoneAvailableAsync(db, transaction, venue.Id, ct));
    }

    public Task<(RestaurantOrderReceipt Receipt, bool Created)> PlaceAsync(string slug, RestaurantOrderRequest request, string address, CancellationToken ct) => PlaceCoreAsync(slug, request, address, null, ct);

    private async Task<(RestaurantOrderReceipt Receipt, bool Created)> PlaceCoreAsync(string slug, RestaurantOrderRequest request, string address, MerchantAccountBinding? merchant, CancellationToken ct)
    {
        if (request is null || request.Order is null || !Guid.TryParseExact(request.RequestKey, "D", out _)
            || request.TrackingKey is null || !TokenPattern.IsMatch(request.TrackingKey))
            throw new OrderingException("Refresh checkout and try again.");
        var name = Text(request.CustomerName, "Name", 80);
        var phone = Text(request.Phone, "Phone", 30, request.Order.Fulfillment == "dine-in");
        if (phone.Length > 0 && (!Regex.IsMatch(phone, "^\\+?[0-9 ().-]{7,30}$") || phone.Count(char.IsAsciiDigit) is < 7 or > 15))
            throw new OrderingException("Enter a valid contact phone number.");
        var deliveryAddress = Text(request.Address, "Delivery address", 250, request.Order.Fulfillment != "delivery");
        var note = Text(request.Note, "Order note", 500, true, multiline: true);
        if (request.Order.Fulfillment != "delivery" && deliveryAddress.Length != 0) throw new OrderingException("A delivery address is only needed for delivery.");
        var fingerprint = Text(request.QuoteFingerprint, "Quote", 64);
        var normalized = request with { CustomerName = name, Phone = phone, Address = deliveryAddress, Note = note };
        var requestHash = Hash(JsonSerializer.Serialize(normalized, Json));
        await using var db = await database.OpenAsync(ct);
        using var transaction = db.BeginTransaction(deferred: false);
        var venue = await ReadVenueAsync(db, transaction, slug, false, ct);
        if (merchant is not null && (merchant.TenantId != venue.Id || merchant.Slug != slug || request.Order.PaymentMethod != "phone" || !merchantOptions.CheckoutEnabled))
            throw new OrderingException("Phone payments are unavailable.", 409, "phone_unavailable");

        await using (var previous = Command(db, transaction, "SELECT request_hash,payload_json FROM bartide_enhanced_orders WHERE tenant_id=@tenant AND request_key=@key",
            ("@tenant", venue.Id), ("@key", request.RequestKey)))
        {
            await using var reader = await previous.ExecuteReaderAsync(ct);
            if (await reader.ReadAsync(ct))
            {
                if (reader.GetString(0) != requestHash) throw new OrderingException("This checkout was already used with different details. Start a new order.", 409, "request_conflict");
                return (Receipt(Parse(reader.GetString(1))), false);
            }
        }
        var quote = Quote(venue, request.Order, merchant is not null);
        if (!quote.CanSubmit) throw new OrderingException(quote.UnavailableReason ?? "This payment choice is unavailable.", 409, "phone_unavailable");
        if (!string.Equals(fingerprint, quote.Fingerprint, StringComparison.Ordinal))
            throw new OrderingException("Your order or prices changed. Review the updated total before placing it.", 409, "stale_quote");
        await EnforceRateAsync(db, transaction, venue.Id, ct);
        if (merchant is not null) await EnforcePhoneReservationAsync(db, transaction, merchant, address, ct);
        await using (var count = Command(db, transaction,
            "SELECT COUNT(*),COALESCE(SUM(CASE WHEN fulfillment='delivery' THEN 1 ELSE 0 END),0) FROM bartide_enhanced_orders WHERE tenant_id=@tenant AND status NOT IN ('completed','cancelled','delivered')", ("@tenant", venue.Id)))
        {
            await using var reader = await count.ExecuteReaderAsync(ct);
            await reader.ReadAsync(ct);
            if (reader.ReadInt64(0) >= 100) throw new OrderingException("The restaurant's order queue is full. Please try again later.", 409, "queue_full");
            if (request.Order.Fulfillment == "delivery" && reader.ReadInt64(1) >= Integer(venue.Config, "delivery_capacity", 1, 30, 5))
                throw new OrderingException("Delivery is full right now. Choose pickup or try again later.", 409, "delivery_full");
        }
        var id = Guid.NewGuid().ToString("D");
        var now = Stamp();
        var initialStatus = merchant is null ? "new" : "awaiting_payment";
        var initialPayment = merchant is null ? "unpaid" : "pending";
        var receipt = new RestaurantOrderReceipt(id, id[..8].ToUpperInvariant(), initialStatus, initialPayment, quote, now);
        var table = ResolveTable(venue, request.Order.TableToken, required: request.Order.Fulfillment == "dine-in", label: request.Order.TableLabel);
        var payload = new JsonObject
        {
            ["id"] = id, ["number"] = receipt.Number, ["status"] = initialStatus, ["payment_status"] = initialPayment,
            ["payment_method"] = request.Order.PaymentMethod, ["fulfillment"] = request.Order.Fulfillment,
            ["table_id"] = table?.Id, ["table_label"] = table?.Label,
            ["lines"] = new JsonArray(quote.Lines.Select(line => (JsonNode)new JsonObject
                { ["item_id"] = line.ItemId, ["name"] = line.Name, ["quantity"] = line.Quantity, ["unit_cents"] = line.UnitCents }).ToArray()),
            ["subtotal_cents"] = quote.SubtotalCents, ["tax_cents"] = quote.TaxCents, ["delivery_fee_cents"] = quote.DeliveryFeeCents,
            ["tip_cents"] = quote.TipCents, ["total_cents"] = quote.TotalCents,
            ["customer_name"] = name, ["phone"] = phone, ["address"] = deliveryAddress, ["zip"] = request.Order.DeliveryZip ?? "", ["note"] = note,
            ["driver_id"] = null, ["created_at"] = now, ["updated_at"] = now, ["version"] = 0,
            ["history"] = new JsonArray(new JsonObject { ["status"] = initialStatus, ["at"] = now }),
            ["dotnet_receipt"] = JsonSerializer.SerializeToNode(receipt, Json)
        };
        if (request.Order.Fulfillment == "delivery" && Boolean(venue.Config, "delivery_workflow_enabled", false)) payload["delivery"] = new JsonObject();
        await using var insert = Command(db, transaction, """
            INSERT INTO bartide_enhanced_orders(id,tenant_id,request_key,request_hash,tracking_hash,payload_json,status,fulfillment,version,created_at,updated_at)
            VALUES (@id,@tenant,@key,@hash,@tracking,@payload,@status,@fulfillment,0,@now,@now)
            """, ("@id", id), ("@tenant", venue.Id), ("@key", request.RequestKey), ("@hash", requestHash), ("@tracking", Hash(request.TrackingKey)),
            ("@payload", payload.ToJsonString(Json)), ("@status", initialStatus), ("@fulfillment", request.Order.Fulfillment), ("@now", now));
        await insert.ExecuteNonQueryAsync(ct);
        if (merchant is not null) await InsertPhoneAttemptAsync(db, transaction, merchant, receipt, requestHash, address, ct);
        await transaction.CommitAsync(ct);
        return (Receipt(payload), true);
    }

    public async Task<RestaurantOrderReceipt> TrackAsync(string slug, RestaurantTrackingRequest request, CancellationToken ct)
    {
        if (request is null || !Guid.TryParseExact(request.OrderId, "D", out _) || !ValidTrackingKey(request.TrackingKey))
            throw new OrderingException("Order not found.", 404, "not_found");
        await using var db = await database.OpenAsync(ct);
        using var tx = db.BeginTransaction(deferred: true);
        var venue = await ReadVenueAsync(db, tx, slug, false, ct);
        await using var query = Command(db, tx, "SELECT payload_json FROM bartide_enhanced_orders WHERE id=@id AND tenant_id=@tenant AND tracking_hash=@tracking",
            ("@id", request.OrderId), ("@tenant", venue.Id), ("@tracking", Hash(request.TrackingKey)));
        var value = await query.ExecuteScalarAsync(ct) as string;
        return value is null ? throw new OrderingException("Order not found.", 404, "not_found") : Receipt(Parse(value));
    }

    public async Task<RestaurantOrderingWorkspace> WorkspaceAsync(string tenantId, AuthUser user, CancellationToken ct)
    {
        await using var db = await database.OpenAsync(ct);
        using var tx = db.BeginTransaction(deferred: false);
        var venue = await ReadVenueAsync(db, tx, tenantId, true, ct);
        await RequireManagerAsync(db, tx, venue, user, ct);
        var orders = new List<RestaurantManagedOrder>();
        await using var query = Command(db, tx, "SELECT payload_json FROM bartide_enhanced_orders WHERE tenant_id=@tenant ORDER BY created_at DESC LIMIT 100", ("@tenant", venue.Id));
        await using var reader = await query.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            var payload = Parse(reader.GetString(0));
            orders.Add(new(Receipt(payload), String(payload, "customer_name"), String(payload, "phone"), String(payload, "address"),
                String(payload, "zip"), String(payload, "note"), Integer(payload, "version", 0, int.MaxValue, 0)));
        }
        await reader.DisposeAsync();
        await tx.CommitAsync(ct);
        return new(venue.Id, venue.Slug, venue.Name, venue.ConfigVersion, true, venue.Tables, orders);
    }

    public async Task<RestaurantOrderingWorkspace> CreateTableAsync(string tenantId, AuthUser user, CreateRestaurantTableRequest request, CancellationToken ct)
    {
        if (request is null) throw new OrderingException("Enter a table name.");
        var label = Text(request.Label, "Table name", 40);
        await MutateTablesAsync(tenantId, user, request.ExpectedVersion, tables =>
        {
            if (tables.Count >= 100) throw new OrderingException("This restaurant supports up to 100 table codes.");
            if (tables.Any(table => table.Label.Equals(label, StringComparison.OrdinalIgnoreCase))) throw new OrderingException("That table name already exists.", 409, "table_exists");
            tables.Add(new(Guid.NewGuid().ToString("D"), label, Token(), true));
        }, ct);
        return await WorkspaceAsync(tenantId, user, ct);
    }

    public async Task<RestaurantOrderingWorkspace> SetTableAsync(string tenantId, string tableId, AuthUser user, SetRestaurantTableStateRequest request, CancellationToken ct)
    {
        if (request is null) throw new OrderingException("Choose a table setting.");
        await MutateTablesAsync(tenantId, user, request.ExpectedVersion, tables =>
        {
            var index = tables.FindIndex(table => table.Id == tableId);
            if (index < 0) throw new OrderingException("Table not found.", 404, "not_found");
            tables[index] = tables[index] with { Enabled = request.Enabled };
        }, ct);
        return await WorkspaceAsync(tenantId, user, ct);
    }

    private async Task MutateTablesAsync(string tenantId, AuthUser user, int expectedVersion, Action<List<RestaurantTable>> change, CancellationToken ct)
    {
        await using var db = await database.OpenAsync(ct);
        using var tx = db.BeginTransaction(deferred: false);
        var venue = await ReadVenueAsync(db, tx, tenantId, true, ct);
        await RequireManagerAsync(db, tx, venue, user, ct);
        if (venue.ConfigVersion != expectedVersion) throw new OrderingException("Table settings changed. Refresh and try again.", 409, "stale_settings");
        var tables = venue.Tables.ToList();
        change(tables);
        var checkout = venue.Config["checkout"] as JsonObject;
        if (checkout is null) { checkout = new(); venue.Config["checkout"] = checkout; }
        checkout["tables"] = JsonSerializer.SerializeToNode(tables, Json);
        checkout["dine_in_enabled"] = tables.Any(table => table.Enabled);
        await using var update = Command(db, tx, "UPDATE bartide_enhanced_configs SET settings_json=@json,version=version+1,updated_at=@now WHERE tenant_id=@tenant AND version=@version",
            ("@json", venue.Config.ToJsonString(Json)), ("@now", Stamp()), ("@tenant", tenantId), ("@version", expectedVersion));
        if (await update.ExecuteNonQueryAsync(ct) != 1) throw new OrderingException("Table settings changed. Refresh and try again.", 409, "stale_settings");
        await tx.CommitAsync(ct);
    }

    private static RestaurantQuote Quote(Venue venue, RestaurantQuoteRequest request, bool phoneReady = false)
    {
        if (request is null || request.Items is null || request.Items.Count is < 1 or > 20
            || request.Fulfillment is not ("dine-in" or "pickup" or "delivery") || request.PaymentMethod is not ("staff" or "phone"))
            throw new OrderingException("Choose menu items, an order type and a payment option.");
        var options = Options(venue, phoneReady);
        if (!options.AcceptingOrders) throw new OrderingException("This restaurant is not accepting orders right now.", 409, "ordering_closed");
        if (request.Fulfillment != "dine-in" && (!string.IsNullOrEmpty(request.TableToken) || !string.IsNullOrEmpty(request.TableLabel)))
            throw new OrderingException("Tables are only used for dine-in orders.");
        if (request.Fulfillment != "delivery" && !string.IsNullOrEmpty(request.DeliveryZip)) throw new OrderingException("A delivery ZIP is only used for delivery.");
        var table = ResolveTable(venue, request.TableToken, request.Fulfillment == "dine-in", request.TableLabel);
        if (request.Fulfillment == "dine-in" && !options.DineInEnabled || request.Fulfillment == "pickup" && !options.PickupEnabled)
            throw new OrderingException("That order type is unavailable.");
        if (request.PaymentMethod == "staff" && !options.PayStaffEnabled) throw new OrderingException("Paying staff is unavailable for online orders.");
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var lines = new List<RestaurantOrderLine>();
        foreach (var selection in request.Items)
        {
            if (selection is null || selection.ItemId is null || !seen.Add(selection.ItemId) || selection.Quantity is < 1 or > 20)
                throw new OrderingException("Choose each item once, with a quantity from 1 to 20.");
            var item = venue.Items.FirstOrDefault(item => item.Id == selection.ItemId);
            if (item is null || !item.Available || item.PriceCents is null) throw new OrderingException("An item is unavailable. Refresh the menu and review your order.", 409, "item_unavailable");
            lines.Add(new(item.Id, item.Name, selection.Quantity, item.PriceCents.Value));
        }
        if (lines.Sum(line => line.Quantity) > 50) throw new OrderingException("Please keep online orders to 50 items or fewer.");
        lines.Sort((a, b) => StringComparer.Ordinal.Compare(a.ItemId, b.ItemId));
        var subtotal = checked(lines.Sum(line => line.UnitCents * line.Quantity));
        var deliveryFee = 0;
        if (request.Fulfillment == "delivery")
        {
            if (!options.DeliveryEnabled || request.DeliveryZip is null || !ZipPattern.IsMatch(request.DeliveryZip) || !options.DeliveryZips.Contains(request.DeliveryZip))
                throw new OrderingException("Delivery is not available for this ZIP code.");
            if (subtotal < options.DeliveryMinimumCents) throw new OrderingException($"The delivery minimum is ${options.DeliveryMinimumCents / 100m:0.00} before fees, tax and tip.");
            deliveryFee = options.DeliveryFeeCents;
        }
        if (request.TipPercent is not (0 or 15 or 20 or 25) || request.CustomTipCents is < 0 or > 50000
            || request.CustomTipCents is not null && request.TipPercent != 0)
            throw new OrderingException("Choose a tip percentage or a custom amount up to $500.");
        var tip = request.CustomTipCents ?? (int)decimal.Round(subtotal * request.TipPercent / 100m, 0, MidpointRounding.AwayFromZero);
        if (tip > subtotal || !options.TipsEnabled && tip != 0) throw new OrderingException("Choose a tip no greater than the menu subtotal, or no tip.");
        // Preserve the configured legacy tax base; optional tips are separate.
        var tax = (int)decimal.Round((subtotal + deliveryFee) * options.TaxBasisPoints!.Value / 10000m, 0, MidpointRounding.AwayFromZero);
        var total = checked(subtotal + tax + deliveryFee + tip);
        var canPhone = phoneReady && total is >= 50 and <= 1_000_000;
        var quote = new RestaurantQuote(lines, subtotal, tax, deliveryFee, tip, total, "USD", request.Fulfillment,
            request.PaymentMethod, table?.Label, "", request.PaymentMethod == "staff" || canPhone, request.PaymentMethod != "phone" || canPhone ? null
                : phoneReady ? "Phone payment totals must be between $0.50 and $10,000.00." : "Phone payments are not available yet. Choose pay staff.");
        return quote with { Fingerprint = Hash(JsonSerializer.Serialize(new { venue.Id, venue.MenuVersion, venue.ConfigVersion, tableId = table?.Id, request, quote }, Json)) };
    }

    private static RestaurantCheckoutOptions Options(Venue venue, bool phoneReady = false)
    {
        var config = venue.Config;
        var checkout = config["checkout"] as JsonObject ?? new();
        int? tax = config["tax_basis_points"] is null ? null : Integer(config, "tax_basis_points", 0, 2500, 0);
        var zips = Strings(config, "delivery_zips", 50);
        if (zips.Any(zip => !ZipPattern.IsMatch(zip))) throw Unavailable();
        return new(Boolean(config, "enabled", false) && Boolean(config, "accepting_orders", false) && tax is not null,
            Boolean(checkout, "dine_in_enabled", false), Boolean(checkout, "pickup_enabled", true), Boolean(config, "delivery_enabled", false) && zips.Count > 0,
            Boolean(checkout, "pay_staff_enabled", true), phoneReady, Boolean(checkout, "tips_enabled", true), tax,
            Integer(config, "delivery_fee_cents", 0, 5000, 500), Integer(config, "delivery_minimum_cents", 0, 100000, 1500), zips,
            String(config, "pickup_instructions"), String(config, "payment_instructions"), String(config, "contact_phone"));
    }

    private static RestaurantTable? ResolveTable(Venue venue, string? token, bool required, string? label = null)
    {
        if (label is not null && (label.Length > 40 || label.Any(char.IsControl)))
            throw new OrderingException("Use the table number or name shown at your table.", 400, "invalid_table");
        label = label?.Trim();
        if (string.IsNullOrEmpty(token) && string.IsNullOrEmpty(label))
            return required ? throw new OrderingException("Enter your table number or scan the QR code at your table.", 400, "table_required") : null;
        var enabled = Boolean(venue.Config["checkout"] as JsonObject ?? new(), "dine_in_enabled", false);
        RestaurantTable? scanned = null, entered = null;
        if (!string.IsNullOrEmpty(token))
        {
            scanned = TokenPattern.IsMatch(token) ? venue.Tables.FirstOrDefault(table => table.Enabled && table.Token == token) : null;
            if (scanned is null || !enabled)
                throw new OrderingException("This table code is unavailable. Please ask a staff member.", 404, "not_found");
        }
        if (!string.IsNullOrEmpty(label))
        {
            // Resolve only configured, active tables in this restaurant; never store unchecked guest text.
            entered = venue.Tables.FirstOrDefault(table => table.Enabled && table.Label.Equals(label, StringComparison.OrdinalIgnoreCase));
            if (entered is null && label.All(char.IsAsciiDigit))
                entered = venue.Tables.FirstOrDefault(table => table.Enabled && table.Label.Equals("Table " + label, StringComparison.OrdinalIgnoreCase));
            if (entered is null || !enabled)
                throw new OrderingException("That table is unavailable. Check its number or ask a staff member.", 404, "table_unavailable");
        }
        if (scanned is not null && entered is not null && scanned.Id != entered.Id)
            throw new OrderingException("The entered table does not match the scanned table.", 400, "table_mismatch");
        return scanned ?? entered;
    }

    private static async Task<Venue> ReadVenueAsync(DbConnection db, DbTransaction tx, string key, bool byId, CancellationToken ct, bool allowPreparation = false)
    {
        if (string.IsNullOrWhiteSpace(key) || key.Length > 200) throw new OrderingException("Restaurant not found.", 404, "not_found");
        await using var query = Command(db, tx, """
            SELECT c.id,c.slug,c.name,c.menu_json,c.version,c.user_id,e.settings_json,e.version
            FROM bartide_customers c LEFT JOIN bartide_enhanced_configs e ON e.tenant_id=c.id
            WHERE
            """ + (allowPreparation && byId ? " c.vertical IN ('bartide','tide-casa') AND c.status IN ('active','draft','building') AND " : " c.vertical='bartide' AND c.status='active' AND ")
            + (byId ? " c.id=@key" : " c.slug=@key"), ("@key", key));
        await using var reader = await query.ExecuteReaderAsync(ct);
        if (!await reader.ReadAsync(ct)) throw new OrderingException("Restaurant not found.", 404, "not_found");
        var menu = Parse(reader.GetString(3));
        var config = reader.IsDBNull(6) ? new JsonObject() : Parse(reader.GetString(6));
        if (String(menu, "schema") != "bartide-menu/1" || menu["venue"] is not JsonObject menuVenue || String(menuVenue, "currency") != "USD"
            || menu["categories"] is not JsonArray categories || categories.Count is < 1 or > 20 || menu["items"] is not JsonArray items || items.Count > 100) throw Unavailable();
        var categoryList = new List<RestaurantCategory>();
        foreach (var node in categories)
        {
            if (node is not JsonObject category || !IdPattern.IsMatch(String(category, "id"))) throw Unavailable();
            categoryList.Add(new(String(category, "id"), Text(String(category, "label"), "Category", 80)));
        }
        if (categoryList.Select(category => category.Id).Distinct().Count() != categoryList.Count) throw Unavailable();
        var blocked = Strings(config, "blocked_item_ids", 100);
        var itemList = new List<RestaurantMenuItem>();
        foreach (var node in items)
        {
            if (node is not JsonObject item || !IdPattern.IsMatch(String(item, "id")) || !categoryList.Any(category => category.Id == String(item, "category"))) throw Unavailable();
            int? price = item["price_cents"] is null ? null : Integer(item, "price_cents", 0, 1000000, 0);
            itemList.Add(new(String(item, "id"), String(item, "category"), Text(String(item, "name"), "Item", 160),
                Text(String(item, "description"), "Description", 1000, true, multiline: true), price, item["price_label"] is null ? null : Text(String(item, "price_label"), "Price label", 160, true),
                Boolean(item, "available", false) && !blocked.Contains(String(item, "id")), PhotoId(String(item, "photo_src"))));
        }
        if (itemList.Select(item => item.Id).Distinct().Count() != itemList.Count) throw Unavailable();
        var tables = new List<RestaurantTable>();
        if (config["checkout"] is JsonObject checkout && checkout["tables"] is JsonArray storedTables)
        {
            if (storedTables.Count > 100) throw Unavailable();
            foreach (var node in storedTables)
            {
                var table = node?.Deserialize<RestaurantTable>(Json);
                if (table is null || !Guid.TryParseExact(table.Id, "D", out _) || table.Token is null || !TokenPattern.IsMatch(table.Token)
                    || string.IsNullOrWhiteSpace(table.Label) || table.Label.Length > 40) throw Unavailable();
                tables.Add(table);
            }
            if (tables.Select(table => table.Id).Distinct().Count() != tables.Count || tables.Select(table => table.Token).Distinct().Count() != tables.Count) throw Unavailable();
        }
        return new(reader.GetString(0), reader.GetString(1), reader.GetString(2), reader.ReadInt32(4), reader.IsDBNull(5) ? null : reader.GetString(5),
            config, reader.IsDBNull(7) ? -1 : reader.ReadInt32(7), categoryList, itemList, tables);
    }

    private static string? PhotoId(string source) => source.StartsWith("/media/", StringComparison.Ordinal)
        && Guid.TryParseExact(source[7..], "D", out _) ? source[7..] : null;

    private static async Task RequireManagerAsync(DbConnection db, DbTransaction tx, Venue venue, AuthUser user, CancellationToken ct)
    {
        if (!Boolean(venue.Config, "enabled", false) || (!user.IsPlatformOwner && user.UserId != venue.OwnerId
            && !await TenantStaffAccess.IsManagerAsync(db, tx, venue.Id, user, ct)))
            throw new OrderingException("Restaurant owner or manager access is required.", 403, "forbidden");
    }

    private static RestaurantOrderReceipt Receipt(JsonObject payload)
    {
        // Reconstruct only approved fields; never serialize contact data into public responses.
        var receipt = payload["dotnet_receipt"]?.Deserialize<RestaurantOrderReceipt>(Json);
        if (receipt is not null) return receipt with { Status = String(payload, "status"), PaymentStatus = String(payload, "payment_status"), Delivery = DeliveryProgress(payload) };
        // Historical orders have no tip/table fields, and stay readable without mutation.
        if (payload["lines"] is not JsonArray storedLines) throw Unavailable();
        var lines = storedLines.OfType<JsonObject>().Select(line => new RestaurantOrderLine(String(line, "item_id"), String(line, "name"),
            Integer(line, "quantity", 1, 20, 1), Integer(line, "unit_cents", 0, 1000000, 0))).ToList();
        var quote = new RestaurantQuote(lines, Integer(payload, "subtotal_cents", 0, int.MaxValue, 0), Integer(payload, "tax_cents", 0, int.MaxValue, 0),
            Integer(payload, "delivery_fee_cents", 0, 5000, 0), Integer(payload, "tip_cents", 0, 50000, 0), Integer(payload, "total_cents", 0, int.MaxValue, 0),
            "USD", String(payload, "fulfillment"), String(payload, "payment_method", "staff"), payload["table_label"]?.GetValue<string>(), "", false, null);
        return new(String(payload, "id"), String(payload, "number"), String(payload, "status"), String(payload, "payment_status", "unpaid"), quote, String(payload, "created_at"), DeliveryProgress(payload));
    }

    private static async Task EnforceRateAsync(DbConnection db, DbTransaction tx, string tenant, CancellationToken ct)
    {
        var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        await using (var clean = Command(db, tx, "DELETE FROM bartide_enhanced_limits WHERE expires_at<@now", ("@now", now))) await clean.ExecuteNonQueryAsync(ct);
        // All clients share one allowance for this venue/location and minute.
        // The containing order transaction rolls this increment back on failure.
        var key = "dotnet-order-location:" + tenant + ":" + now / 60000;
        await using var count = Command(db, tx, """
            INSERT INTO bartide_enhanced_limits(id,count,expires_at) VALUES(@id,1,@expiry)
            ON CONFLICT(id) DO UPDATE SET count=bartide_enhanced_limits.count+1 WHERE bartide_enhanced_limits.count<23
            """, ("@id", key), ("@expiry", now + 120000));
        if (await count.ExecuteNonQueryAsync(ct) != 1) throw new OrderingException("This location has reached its limit of 23 new orders this minute. Please try again next minute.", 429, "rate_limited");
    }

    private static DbCommand Command(DbConnection db, DbTransaction tx, string sql, params (string Name, object Value)[] parameters)
    {
        var command = db.CreateCommand(); command.Transaction = tx; command.CommandText = sql;
        foreach (var parameter in parameters) command.Parameters.AddWithValue(parameter.Name, parameter.Value);
        return command;
    }
    private static JsonObject Parse(string json) => JsonNode.Parse(json, documentOptions: new() { MaxDepth = 32 }) as JsonObject ?? throw Unavailable();
    private static string String(JsonObject value, string key, string fallback = "") => value[key] is null ? fallback : value[key]!.GetValue<string>();
    private static bool Boolean(JsonObject value, string key, bool fallback) => value[key] is null ? fallback : value[key]!.GetValue<bool>();
    private static int Integer(JsonObject value, string key, int min, int max, int fallback)
    {
        var number = value[key] is null ? fallback : value[key]!.GetValue<int>();
        return number >= min && number <= max ? number : throw Unavailable();
    }
    private static List<string> Strings(JsonObject value, string key, int max)
    {
        if (value[key] is null) return [];
        if (value[key] is not JsonArray array || array.Count > max) throw Unavailable();
        return array.Select(node => node?.GetValue<string>() ?? throw Unavailable()).ToList();
    }
    private static bool ValidTrackingKey(string? key) => key is not null && (TokenPattern.IsMatch(key)
        || key.Length == 72 && Guid.TryParseExact(key[..36], "D", out _) && Guid.TryParseExact(key[36..], "D", out _));

    private static string Text(string? value, string name, int max, bool optional = false, bool multiline = false)
    {
        if (value is null) value = "";
        if (value.Length > max || value.Any(character => char.IsControl(character) && !(multiline && character is '\r' or '\n' or '\t')) || !optional && string.IsNullOrWhiteSpace(value))
            throw new OrderingException($"{name}: enter {(optional ? "up to" : "1 to")} {max} characters.");
        return value.Trim();
    }
    private static OrderingException Unavailable() => new("This restaurant's ordering settings need review. Please contact the restaurant.", 503, "invalid_configuration");
    private sealed record Venue(string Id, string Slug, string Name, int MenuVersion, string? OwnerId, JsonObject Config, int ConfigVersion,
        IReadOnlyList<RestaurantCategory> Categories, IReadOnlyList<RestaurantMenuItem> Items, IReadOnlyList<RestaurantTable> Tables);
}
