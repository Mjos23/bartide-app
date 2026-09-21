using Microsoft.AspNetCore.Components;

namespace TideCasa.Blazor.Features.Authentication;

public class AuthPageBase : ComponentBase
{
    [SupplyParameterFromQuery(Name = "return_to")] public string? ReturnTo { get; set; }
    [SupplyParameterFromQuery(Name = "notice")] public string? NoticeCode { get; set; }
    protected string ReturnPath => AuthFlow.SafeReturnPath(ReturnTo);
    protected string AuthLink(string page) => page + "?return_to=" + Uri.EscapeDataString(ReturnPath);
    protected string? NoticeText => AuthFlow.Notice(NoticeCode);
}
