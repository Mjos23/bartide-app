using System.Globalization;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Http.Features;
using TideCasa.Blazor.Features.Authentication;
using TideCasa.Blazor.Services;
using TideCasa.Contracts;
namespace TideCasa.Blazor.Features.Rewards;

public static partial class RewardsFlow
{
    public static void MapRewardForms(this WebApplication app)
    {
        app.MapPost("/rewards/forms/{scope}/{id}/{action}", SaveAsync).RequireAuthorization();
        MapOrderRewards(app);
    }
    private static async Task<IResult> SaveAsync(string scope, string id, string action, HttpContext context, IAntiforgery antiforgery, RewardsClient client)
    {
        context.Response.Headers.CacheControl = "no-store";
        context.Response.Headers["Referrer-Policy"] = "same-origin";
        if (scope is not ("owner" or "customer") || !Identifier().IsMatch(id)) return Results.NotFound();
        if (context.Items.ContainsKey(AuthFlow.UnavailableItem)) return AuthFlow.ServiceUnavailable();
        if (context.Items[AuthFlow.TokenItem] is not string token) return Results.Unauthorized();
        var returnPath = scope == "owner" ? "/workspace/" + id + "/rewards" : "/rewards/" + id;
        IResult Notice(string code) => Results.LocalRedirect(returnPath + "?notice=" + Uri.EscapeDataString(code));
        if (!SameOrigin(context.Request) || !context.Request.HasFormContentType) return Results.BadRequest();
        if (context.Features.Get<IHttpMaxRequestBodySizeFeature>() is { IsReadOnly: false } body) body.MaxRequestBodySize = 64 * 1024;
        try
        {
            await antiforgery.ValidateRequestAsync(context);
            var form = await context.Request.ReadFormAsync(context.RequestAborted);
            if (form.Count > 18 || form.Files.Count > 0 || form.Any(pair => pair.Value.Count != 1)) return Results.BadRequest();
            string Text(string key) => form[key].ToString().Trim();
            string Entry(string key) => Identifier().IsMatch(Text(key)) ? Text(key) : throw new FormatException();
            int Number(string key) => int.Parse(Text(key), NumberStyles.Integer, CultureInfo.InvariantCulture);
            var requestId = Text("request_id"); if (!Guid.TryParseExact(requestId, "D", out _)) return Notice("invalid");
            object request; string suffix;
            if (scope == "customer")
            {
                (suffix, request) = action switch
                {
                    "join" => ("/join", (object)new JoinRewardsRequest(requestId, Text("name"))),
                    "request" => ("/redemptions/" + Entry("reward_id") + "/request", (object)new RequestRewardRedemptionRequest(requestId)),
                    "points" => ("/points-redemptions", (object)new RedeemPointsRewardRequest(requestId, Entry("rule_id"))),
                    _ => throw new FormatException()
                };
            }
            else
            {
                (suffix, request) = action switch
                {
                    "rule" => ("/rules" + (Text("rule_id").Length == 0 ? "" : "/" + Entry("rule_id")), (object)new SaveRewardRuleRequest(requestId, Number("version"), Text("title"), Text("reward"), Text("kind"), Number("threshold"), Text("item_id").Length == 0 ? null : Entry("item_id"), Text("timezone"), Text("active") == "true")),
                    "qualify" => ("/qualifications", (object)new RecordRewardQualificationRequest(requestId, Entry("member_id"), Entry("rule_id"), Text("source"), Text("reference"), Number("units"), Text("item_id").Length == 0 ? null : Entry("item_id"), Text("note"))),
                    "void" => ("/qualifications/" + Entry("qualification_id") + "/void", (object)new VoidRewardQualificationRequest(requestId, Text("reason"))),
                    "points" => ("/points", (object)new AwardCustomerPointsRequest(requestId, Entry("member_id"), Number("points"), Text("reference"), Text("reason"))),
                    "resolve" => ("/redemptions/" + Entry("reward_id") + "/resolve", (object)new ResolveRewardRedemptionRequest(requestId, Text("resolution"), Text("note"))),
                    "employee" => ("/employee-points", (object)new ChangeEmployeePointsRequest(requestId, Entry("member_id"), Number("delta"), Text("reference"), Text("reason"))),
                    _ => throw new FormatException()
                };
                if (action is "qualify" or "points" or "employee" or "resolve" or "void" && Text("confirmed") != "true") return Notice("confirm");
            }
            var result = await client.SendAsync<RewardChangeResult>((scope == "owner" ? RewardsClient.Owner(id) : RewardsClient.Customer(id)) + suffix, token, request, context.RequestAborted);
            return Notice(result.Succeeded ? "saved" : result.Uncertain ? "unconfirmed" : result.Message ?? "invalid");
        }
        catch (Exception e) when (e is AntiforgeryValidationException or InvalidDataException or BadHttpRequestException) { return Notice("expired"); }
        catch (Exception e) when (e is FormatException or OverflowException) { return Notice("invalid"); }
    }
    public static string? Notice(string? value) => value switch
    {
        null or "" => null, "saved" => "Your change is saved.", "confirm" => "Confirm the details before recording this change.",
        "invalid" => "Check the form and try again.", "expired" => "This form expired. Refresh the page and try again.",
        "unconfirmed" => "We couldn’t confirm the change. Refresh and check the current balance before trying again.",
        _ => value.Length <= 300 ? value : "Check the form and try again."
    };
    public static string State(string state) => state switch { "available" => "Ready to use", "requested" => "Requested — show staff", "fulfilled" => "Used", "void" => "No longer eligible", "cancelled" => "Request cancelled", _ => state };
    private static bool SameOrigin(HttpRequest r)
    {
        var origin = r.Headers.Origin.ToString(); var source = string.IsNullOrEmpty(origin) ? r.Headers.Referer.ToString() : origin;
        return Uri.TryCreate(source, UriKind.Absolute, out var actual) && Uri.TryCreate(r.Scheme + "://" + r.Host, UriKind.Absolute, out var expected)
            && actual.Scheme == expected.Scheme && actual.IdnHost == expected.IdnHost && actual.Port == expected.Port && actual.UserInfo.Length == 0
            && (origin.Length == 0 || actual.AbsolutePath == "/") && r.Headers["Sec-Fetch-Site"] != "cross-site";
    }
    [GeneratedRegex("^[A-Za-z0-9_-]{1,128}$", RegexOptions.CultureInvariant)] private static partial Regex Identifier();
}
