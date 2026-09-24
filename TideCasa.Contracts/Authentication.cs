using System.ComponentModel.DataAnnotations;

namespace TideCasa.Contracts;

public sealed record SignInRequest(
    [property: Required, EmailAddress, StringLength(254)] string Email,
    [property: Required, StringLength(128, MinimumLength = 1)] string Password);
public sealed record SignUpRequest(
    [property: Required, EmailAddress, StringLength(254)] string Email,
    [property: Required, StringLength(128, MinimumLength = 12)] string Password);
public sealed record EmailRequest([property: Required, EmailAddress, StringLength(254)] string Email);
public sealed record VerifyEmailRequest(
    [property: Required, EmailAddress, StringLength(254)] string Email,
    [property: Required, RegularExpression("^[0-9]{6,10}$")] string Code);
public sealed record ResetPasswordRequest(
    [property: Required, EmailAddress, StringLength(254)] string Email,
    [property: Required, RegularExpression("^[0-9]{6,10}$")] string Code,
    [property: Required, StringLength(128, MinimumLength = 12)] string Password);

public sealed record AuthUser(string UserId, string Email, string DisplayName, string? FullName, bool IsPlatformOwner = false);
public sealed record AuthSession(string AccessToken, string TokenType, int ExpiresIn, AuthUser User);
public sealed record AuthNotice(string Message);
public sealed record WorkspaceAccess(string TenantId, string Slug, string Name, string Status,
    bool CanPrepare, bool CanEdit, string? StaffRole, string? StaffMemberId, string? LearnerId, bool IsOwner = false, string Vertical = "bartide");
public sealed record AccountOverview(AuthUser User, IReadOnlyList<WorkspaceAccess> Workspaces);
