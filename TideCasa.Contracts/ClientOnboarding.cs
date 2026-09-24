namespace TideCasa.Contracts;

public sealed record OnboardingHours(int Day, bool Closed, string Opens, string Closes, bool Overnight = false);
public sealed record OnboardingBusiness(string Name, string PublicEmail, string Phone, string Address,
    string City, string Region, string PostalCode, string Country, string TimeZone, string Website,
    IReadOnlyList<OnboardingHours> Hours);
public sealed record OnboardingBrand(string Style, string Accent, string Introduction, string LogoChoice,
    string? LogoPhotoId, string? CoverPhotoId);
public sealed record OnboardingService(bool Ordering, bool DineIn, bool Pickup, bool Delivery,
    bool PayStaff, bool RequestCards, bool Tips, int? TaxBasisPoints, IReadOnlyList<string> TableLabels,
    IReadOnlyList<string> DeliveryZips, int DeliveryFeeCents, int DeliveryMinimumCents, int DeliveryCapacity,
    string PickupInstructions, string PaymentInstructions);
public sealed record OnboardingTeam(bool OwnerHandlesOrders, string OrderContact, bool EventsLater,
    bool RewardsLater, bool TrainingLater);
public sealed record OnboardingCheck(string Code, string Section, string State, string Message, string ActionPath);
public sealed record OnboardingSaveBusiness(int ExpectedRevision, OnboardingBusiness Business);
public sealed record OnboardingSaveBrand(int ExpectedRevision, OnboardingBrand Brand);
public sealed record OnboardingSaveService(int ExpectedRevision, OnboardingService Service);
public sealed record OnboardingSaveTeam(int ExpectedRevision, OnboardingTeam Team);
public sealed record OnboardingBuildRequest(int ExpectedRevision);
public sealed record OnboardingApproveRequest(string ReleaseId, string Fingerprint, bool Confirmed);
public sealed record OnboardingWorkspace(string TenantId, string Slug, string Name, string Status, int Revision,
    bool IsOwner, string? UpdatedAt, OnboardingBusiness Business, OnboardingBrand Brand, OnboardingService Service,
    OnboardingTeam Team, IReadOnlyList<OnboardingCheck> Checks, int MenuItemCount, string? ReleaseId,
    bool ApprovalCurrent, string? ApprovedAt, bool CanApprove, string? BuildReadyAt);
public sealed record OnboardingRelease(string Id, string TenantId, string Slug, string Fingerprint,
    string TemplateVersion, string CreatedAt, bool Current, bool Approved, bool CanApprove,
    OnboardingBusiness Business, OnboardingBrand Brand, OnboardingService Service,
    IReadOnlyList<RestaurantCategory> Categories, IReadOnlyList<RestaurantMenuItem> Items,
    IReadOnlyList<OnboardingCheck> Checks);
public sealed record OnboardingPracticeRequest(string ReleaseId, string Fingerprint, RestaurantQuoteRequest Order);
public sealed record OnboardingPracticeResult(RestaurantQuote Quote, string Message);
public sealed record OnboardingPublicSite(string TenantId, string Slug, OnboardingBusiness Business,
    OnboardingBrand Brand, IReadOnlyList<RestaurantCategory> Categories, IReadOnlyList<RestaurantMenuItem> Items,
    bool OrderingAvailable, string? HoursText = null);
