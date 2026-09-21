namespace TideCasa.Contracts;

public sealed record RegisterWorkspaceRequest(string RequestId, string BusinessName, string ContactName,
    string Area, string Plan, bool Acknowledged, string? Phone = null, string? Website = null, string? Notes = null);
public sealed record WorkspaceRegistration(string TenantId, string Slug, bool Created);
