using System.Text.Json.Serialization;

namespace TideCasa.Contracts;

public sealed record RestaurantCategory(string Id, string Name);
public sealed record RestaurantMenuItem(string Id, string CategoryId, string Name, string Description, int? PriceCents, string? PriceLabel, bool Available, string? PhotoId = null);
public sealed record RestaurantTable(string Id, string Label, string Token, bool Enabled);
public sealed record RestaurantCheckoutOptions(bool AcceptingOrders, bool DineInEnabled, bool PickupEnabled, bool DeliveryEnabled,
    bool PayStaffEnabled, bool PhonePaymentAvailable, bool TipsEnabled, int? TaxBasisPoints,
    int DeliveryFeeCents, int DeliveryMinimumCents, IReadOnlyList<string> DeliveryZips, string PickupInstructions, string PaymentInstructions);
public sealed record RestaurantMenu(string Slug, string Name, int MenuVersion, string Currency,
    IReadOnlyList<RestaurantCategory> Categories, IReadOnlyList<RestaurantMenuItem> Items, RestaurantCheckoutOptions Checkout, RestaurantTable? Table);
public sealed record OrderItemSelection(string ItemId, int Quantity);
public sealed record RestaurantQuoteRequest(IReadOnlyList<OrderItemSelection> Items, string Fulfillment, string PaymentMethod,
    string? TableToken = null, string? DeliveryZip = null, int TipPercent = 0, int? CustomTipCents = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? TableLabel = null);
public sealed record RestaurantOrderLine(string ItemId, string Name, int Quantity, int UnitCents);
public sealed record RestaurantQuote(IReadOnlyList<RestaurantOrderLine> Lines, int SubtotalCents, int TaxCents, int DeliveryFeeCents,
    int TipCents, int TotalCents, string Currency, string Fulfillment, string PaymentMethod, string? TableLabel,
    string Fingerprint, bool CanSubmit, string? UnavailableReason);
public sealed record RestaurantOrderRequest(string RequestKey, string TrackingKey, RestaurantQuoteRequest Order,
    string QuoteFingerprint, string CustomerName, string Phone, string? Address = null, string? Note = null);
public sealed record RestaurantOrderReceipt(string OrderId, string Number, string Status, string PaymentStatus,
    RestaurantQuote Quote, string CreatedAt);
public sealed record RestaurantTrackingRequest(string OrderId, string TrackingKey);
public sealed record RestaurantManagedOrder(RestaurantOrderReceipt Receipt, string CustomerName, string Phone, string Address,
    string Zip, string Note, int Version);
public sealed record RestaurantOrderingWorkspace(string TenantId, string Slug, string Name, int ConfigVersion,
    bool CanManage, IReadOnlyList<RestaurantTable> Tables, IReadOnlyList<RestaurantManagedOrder> Orders);
public sealed record CreateRestaurantTableRequest(int ExpectedVersion, string Label);
public sealed record SetRestaurantTableStateRequest(int ExpectedVersion, bool Enabled);
