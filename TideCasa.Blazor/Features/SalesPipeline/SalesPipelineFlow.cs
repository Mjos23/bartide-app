using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Http.Features;
using TideCasa.Blazor.Features.Authentication;
using TideCasa.Blazor.Services;
using TideCasa.Contracts;

namespace TideCasa.Blazor.Features.SalesPipeline;

public static class SalesPipelineFlow
{
    public static readonly IReadOnlyDictionary<string, string> Stages = new Dictionary<string, string>
    {
        ["new"] = "Needs first contact", ["contacted"] = "Contact started", ["pitched"] = "Pitched", ["demo-scheduled"] = "Demo agreed",
        ["demo-completed"] = "Demo completed", ["proposal"] = "Proposal", ["enrolled"] = "Agreement recorded", ["closed"] = "Closed"
    };
    public static string Stage(string value) => Stages.GetValueOrDefault(value, "Imported: " + value);
    public static string Detail(string id) => "/owner/sales/" + Uri.EscapeDataString(id);
    public static string? Notice(string? value) => value switch
    {
        "saved" => "Your sales record is saved.", "created" => "The prospect is saved. Add its next action below.",
        "existing" => "This prospect is already saved. The original details, source and notes were retained. Review them before adding your next step.",
        "changed" => "This record changed or already exists. Review the latest details before saving again.",
        "invalid" => "The record was not saved. Check the fields and try again.",
        "expired" => "The form expired. Refresh before saving again.",
        "unconfirmed" => "We could not confirm the save. Check the latest record before trying again.",
        _ => ValidationNotices.GetValueOrDefault(value ?? "")
    };
    // Only fixed, reviewed codes enter redirect URLs; never echo provider text or submitted fields.
    private static readonly IReadOnlyDictionary<string, string> ValidationNotices = new Dictionary<string, string>
    {
        ["referral"] = "Use an existing sales engineer’s referral code or leave it blank.",
        ["email"] = "Enter a valid email address or leave it blank.",
        ["date"] = "Choose a valid follow-up date.",
        ["workspace"] = "Choose an existing client workspace or leave it blank.",
        ["stage"] = "Choose a listed sales stage.",
        ["vertical"] = "Choose a listed business app.",
        ["required"] = "Complete the required prospect details.",
        ["length"] = "Check the length and characters in the prospect details.",
        ["refresh"] = "Refresh the form before saving."
    };
    private static string FailureNotice(SalesResult<SalesLeadDetail> result) => (int)result.Status switch
    {
        400 => ValidationNotices.FirstOrDefault(pair => pair.Value == result.ProblemTitle).Key ?? "invalid",
        409 => "changed",
        _ => "unconfirmed"
    };
    public static string When(string value) => DateTimeOffset.TryParse(value, out var at) ? at.ToString("MMM d, yyyy · HH:mm 'UTC'") : value;
    public static void MapSalesPipelineForms(this WebApplication app)
    {
        app.MapPost("/owner/sales/create", (HttpContext c, IAntiforgery csrf, SalesPipelineClient api) => Save(c, csrf, api, null)).RequireAuthorization();
        app.MapPost("/owner/sales/{id}/save", (string id, HttpContext c, IAntiforgery csrf, SalesPipelineClient api) => Save(c, csrf, api, id)).RequireAuthorization();
    }
    private static async Task<IResult> Save(HttpContext context, IAntiforgery csrf, SalesPipelineClient api, string? id)
    {
        if (context.Items.ContainsKey(AuthFlow.UnavailableItem)) return AuthFlow.ServiceUnavailable();
        if ((context.Items[AuthFlow.AccountItem] as AccountOverview)?.User.IsPlatformOwner != true) return Results.Forbid();
        if (context.Items[AuthFlow.TokenItem] is not string token || id is { Length: > 200 }) return Results.BadRequest();
        var origin = context.Request.Headers.Origin.ToString();
        var source = origin.Length == 0 ? context.Request.Headers.Referer.ToString() : origin;
        if (!context.Request.HasFormContentType || !Uri.TryCreate(source, UriKind.Absolute, out var actual)
            || !Uri.TryCreate(context.Request.Scheme + "://" + context.Request.Host, UriKind.Absolute, out var expected)
            || actual.Scheme != expected.Scheme || actual.IdnHost != expected.IdnHost || actual.Port != expected.Port || actual.UserInfo.Length != 0
            || origin.Length > 0 && actual.AbsolutePath != "/" || context.Request.Headers["Sec-Fetch-Site"] == "cross-site") return Results.BadRequest();
        if (context.Features.Get<IHttpMaxRequestBodySizeFeature>() is { IsReadOnly: false } size) size.MaxRequestBodySize = 64 * 1024;
        var returnPath = id is null ? "/owner/sales" : Detail(id);
        try
        {
            await csrf.ValidateRequestAsync(context);
            var form = await context.Request.ReadFormAsync(context.RequestAborted);
            if (form.Files.Count > 0 || form.Count > 12 || form.Any(x => x.Value.Count != 1 || x.Value.ToString().Length > 4000)) return Results.BadRequest();
            if (!Guid.TryParseExact(form["requestKey"], "D", out _)) return Results.BadRequest();
            object request;
            if (id is null)
                request = new CreateSalesLeadRequest(form["requestKey"].ToString(), form["name"].ToString(), form["business"].ToString(),
                    form["email"].ToString(), form["phone"].ToString(), form["city"].ToString(), form["vertical"].ToString(),
                    form["source"].ToString(), form["referralCode"].ToString(), form["privateNote"].ToString());
            else
            {
                if (!int.TryParse(form["version"], out var version) || version < 0) return Results.BadRequest();
                request = new UpdateSalesLeadRequest(form["requestKey"].ToString(), version, form["stage"].ToString(),
                    form["nextAction"].ToString(), form["followUpDate"].ToString(), form["assignee"].ToString(),
                    form["privateNote"].ToString(), string.IsNullOrWhiteSpace(form["workspaceId"]) ? null : form["workspaceId"].ToString());
            }
            var result = await api.SendAsync<SalesLeadDetail>(id is null ? "" : "/" + Uri.EscapeDataString(id), HttpMethod.Post, request, token, context.RequestAborted);
            if (result.Succeeded && result.Value is not null)
                return Results.LocalRedirect(Detail(result.Value.Lead.Id) + "?notice=" + (id is null ? result.Value.ExistingProspect ? "existing" : "created" : "saved"));
            return Results.LocalRedirect(returnPath + "?notice=" + FailureNotice(result));
        }
        catch (Exception e) when (e is AntiforgeryValidationException or InvalidDataException or BadHttpRequestException)
        { return Results.LocalRedirect(returnPath + "?notice=expired"); }
    }
}
