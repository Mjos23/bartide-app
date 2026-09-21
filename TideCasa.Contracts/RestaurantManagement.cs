namespace TideCasa.Contracts;

public sealed record RestaurantProfile(string Name, string Area, string Tagline, string Hours, string Website, string ServiceNote);
public sealed record RestaurantPhotoOption(string Id, string CreatedAt);
public sealed record RestaurantMenuEditor(string TenantId, string Slug, int Version, RestaurantProfile Profile,
    IReadOnlyList<RestaurantCategory> Categories, IReadOnlyList<RestaurantMenuItem> Items, IReadOnlyList<RestaurantPhotoOption>? Photos = null);
public sealed record SaveRestaurantProfileRequest(int ExpectedVersion, RestaurantProfile Profile);
public sealed record SaveRestaurantCategoryRequest(int ExpectedVersion, RestaurantCategory Category);
public sealed record SaveRestaurantItemRequest(int ExpectedVersion, RestaurantMenuItem Item, string? PhotoId = null);
public sealed record RemoveRestaurantEntryRequest(int ExpectedVersion);
public sealed record RestaurantDriver(string Id, string Name);
public sealed record RestaurantOperationOrder(RestaurantManagedOrder Order, string? DriverId, IReadOnlyList<string> AllowedActions);
public sealed record RestaurantOperationsWorkspace(string TenantId, string Name, string Role,
    IReadOnlyList<RestaurantDriver> Drivers, IReadOnlyList<RestaurantOperationOrder> Orders);
public sealed record ChangeRestaurantOrderRequest(int ExpectedVersion, string Action, string? DriverId = null, bool PaymentCollected = false);
public sealed record RestaurantOrderingSettings(int Version, bool AcceptingOrders, bool PickupEnabled, bool DeliveryEnabled,
    bool PayStaffEnabled, bool TipsEnabled, int? TaxBasisPoints, int DeliveryFeeCents, int DeliveryMinimumCents,
    int DeliveryCapacity, IReadOnlyList<string> DeliveryZips, string PickupInstructions, string PaymentInstructions);
public sealed record SaveRestaurantOrderingSettingsRequest(int ExpectedVersion, bool AcceptingOrders, bool PickupEnabled,
    bool DeliveryEnabled, bool PayStaffEnabled, bool TipsEnabled, int? TaxBasisPoints, int DeliveryFeeCents,
    int DeliveryMinimumCents, int DeliveryCapacity, IReadOnlyList<string> DeliveryZips, string PickupInstructions, string PaymentInstructions);
