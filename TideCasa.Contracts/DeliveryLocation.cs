namespace TideCasa.Contracts;

public sealed record DeliveryLocationPoint(double Latitude, double Longitude, double AccuracyMeters, string CapturedAt);
public sealed record DeliveryLocationView(string OrderId, string DriverName, bool Simulated, string State,
    DeliveryLocationPoint? Point, string? ReceivedAt, string ServerTime);
public sealed record DeliveryLocationBoard(bool Enabled, bool Simulated, int Version, IReadOnlyList<DeliveryLocationView> Locations);
public sealed record DeliveryLocationSettings(int ExpectedVersion, bool Enabled);
public sealed record DeliveryLocationStart(bool Consent);
public sealed record DeliveryLocationSession(string Session, bool Simulated, DeliveryLocationView Location);
public sealed record DeliveryLocationUpdate(string Session, long Sequence, DeliveryLocationPoint? Point = null);
public sealed record DeliveryLocationStop(string Session);
