using Microsoft.AspNetCore.Components;
using TideCasa.Blazor.Services;

namespace TideCasa.Blazor.Features.Authentication;

public class AuthPageBase : ComponentBase
{
    [Inject] protected NavigationManager AuthNavigation { get; set; } = default!;
    [SupplyParameterFromQuery(Name = "return_to")] public string? ReturnTo { get; set; }
    [SupplyParameterFromQuery(Name = "notice")] public string? NoticeCode { get; set; }
    protected bool CustomerEntry => CustomerExperience.IsEntry(AuthNavigation.Uri, ReturnTo);
    protected string AccountBrand => CustomerEntry ? "BarTide" : "Tide Casa";
    protected string ReturnPath => CustomerExperience.ReturnPath(ReturnTo, CustomerEntry);
    protected string AuthLink(string page) => CustomerExperience.AuthPage(page, CustomerEntry) + "?return_to=" + Uri.EscapeDataString(ReturnPath);
    protected string? NoticeText => AuthFlow.Notice(NoticeCode);
}
