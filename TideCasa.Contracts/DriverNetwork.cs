namespace TideCasa.Contracts;

public sealed record NetworkDriverProfile(string Id, string Name, string Bio, IReadOnlyList<string> DeliveryZips,
    bool Listed, int Capacity, int Version);
public sealed record NetworkDriverHire(string Id, string DriverId, string DriverName, string TenantId, string TenantName,
    string Status, int PayPerDeliveryCents, string? MemberId, int Version, string Notes);
public sealed record DriverNetworkAccount(NetworkDriverProfile? Profile, IReadOnlyList<NetworkDriverHire> Hires);
public sealed record ClientDriverNetworkWorkspace(string TenantId, string Name, string Role,
    IReadOnlyList<NetworkDriverProfile> Drivers, IReadOnlyList<NetworkDriverHire> Hires);
public sealed record SaveNetworkDriverProfile(int ExpectedVersion, string Name, string Bio,
    IReadOnlyList<string> DeliveryZips, bool Listed, int Capacity = 1);
public sealed record OfferNetworkDriverHire(string DriverId, int PayPerDeliveryCents, string Notes, int ExpectedVersion = 0);
public sealed record RespondNetworkDriverHire(int ExpectedVersion, bool Accept);
public sealed record EndNetworkDriverHire(int ExpectedVersion);
