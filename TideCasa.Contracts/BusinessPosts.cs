namespace TideCasa.Contracts;

public sealed record BusinessPost(string Id, string Title, string Body, string State, int Version, string CreatedAt, string? PublishedAt);
public sealed record BusinessPostsFeed(string TenantId, string Slug, string Name, IReadOnlyList<BusinessPost> Posts, bool PushAvailable, string? VapidPublicKey);
public sealed record BusinessPostWorkspace(BusinessPostsFeed Feed, int SubscriberCount, bool CanPublish);
public sealed record CreateBusinessPostRequest(string RequestKey, string Title, string Body);
public sealed record TransitionBusinessPostRequest(string RequestKey, int ExpectedVersion);
public sealed record PushSubscriptionRequest(string Endpoint, string P256dh, string Auth, bool Consent);
public sealed record PushSubscriptionInfo(string Id, string EndpointHash, bool Active, string CreatedAt);
public sealed record MyPushSubscriptions(IReadOnlyList<PushSubscriptionInfo> Subscriptions);
