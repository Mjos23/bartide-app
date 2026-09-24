namespace TideCasa.Contracts;

public sealed record DeliveryDriverProfile(string Id, string Name, bool AccountLinked, string Availability,
    int Capacity, IReadOnlyList<string> DeliveryZips, int ActiveOrders, bool AvailableNow, int Version,
    string? LastAssignedAt);
public sealed record DeliveryAssignmentPlan(string Mode, string? DispatchAt = null, string? PreferredDriverId = null);
public sealed record DeliveryDispatchWorkspace(string TenantId, string Role, string? MemberId, bool AutomaticAssignment,
    int Version, bool WorkflowEnabled, IReadOnlyList<DeliveryDriverProfile> Drivers, IReadOnlyList<TeamShift> Shifts,
    string ServerTime);
public sealed record SaveDeliveryDispatchSettings(int ExpectedVersion, bool AutomaticAssignment);
public sealed record SaveDeliveryDriverProfile(int ExpectedVersion, string Availability, int Capacity, IReadOnlyList<string> DeliveryZips);
public sealed record SaveDeliveryAssignmentPlan(int ExpectedVersion, string Mode, string? DispatchAt = null, string? PreferredDriverId = null);
public sealed record DeliveryDispatchResult(int Assigned);
