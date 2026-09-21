namespace TideCasa.Contracts;

public sealed record SalesLead(string Id, string Name, string Business, string Email, string Phone, string City,
    string Vertical, string Stage, string Source, string ReferralCode, string? SubmittedBy, string NextAction,
    string FollowUpDate, string Assignee, string PrivateNote, string? WorkspaceId, int Version,
    string CreatedAt, string UpdatedAt, int DemoRequestCount);
public sealed record SalesPipelineSummary(int ToPitch, int Pitched, int FollowUp, int Unplanned);
public sealed record SalesPipelinePage(IReadOnlyList<SalesLead> Leads, int Total, int Page, int PageSize,
    SalesPipelineSummary? Summary = null);
public sealed record SalesLeadActivity(string Id, string Kind, string Summary, string Actor, string CreatedAt, string PrivateNote);
public sealed record SalesDemoRequest(string Id, string Status, string PreferredTimes, string TimeZone, string Goals, string CreatedAt,
    string Phone = "", bool CanText = false);
public sealed record SalesWorkspaceOption(string Id, string Name);
public sealed record SalesLeadDetail(SalesLead Lead, IReadOnlyList<SalesLeadActivity> History, int HistoryCount,
    IReadOnlyList<SalesDemoRequest> DemoRequests, IReadOnlyList<SalesWorkspaceOption> Workspaces, bool WorkspaceChoicesLimited,
    bool ExistingProspect = false);
public sealed record CreateSalesLeadRequest(string RequestKey, string Name, string Business, string Email,
    string Phone, string City, string Vertical, string Source, string ReferralCode, string PrivateNote);
public sealed record UpdateSalesLeadRequest(string RequestKey, int ExpectedVersion, string Stage,
    string NextAction, string FollowUpDate, string Assignee, string PrivateNote, string? WorkspaceId);
