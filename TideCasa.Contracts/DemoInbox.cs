namespace TideCasa.Contracts;
public sealed record DemoInquiry(DemoRequest Request, string Status, int Version, string Note, string NotificationState, string CreatedAt);
public sealed record DemoInbox(IReadOnlyList<DemoInquiry> Inquiries, bool NotificationsEnabled);
public sealed record UpdateDemoInquiryRequest(int ExpectedVersion, string Status, string Note);
