namespace TideCasa.Contracts;

public sealed record LaunchReviewProject(string Id, string Name, string Vertical, string Status, int Version,
    string? EnrolledAt, string? BuildReadyAt, int FinishedItemCount, bool CanLaunch, string? LaunchBlockReason);
public sealed record LaunchReviewWorkspace(IReadOnlyList<LaunchReviewProject> Projects, string? NextCursor);
public sealed record LaunchReviewTransitionRequest(int ExpectedVersion, string Status, bool ReviewedWithCustomer = false);
