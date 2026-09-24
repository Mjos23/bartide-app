using System.Globalization;
using Microsoft.AspNetCore.Antiforgery;
using TideCasa.Blazor.Services;
using TideCasa.Contracts;

namespace TideCasa.Blazor.Features.RestaurantManagement;

public static partial class RestaurantManagementFlow
{
    public static void MapDriverNetworkForms(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapPost("/driver/manage/{operation}", SaveDriverAccountAsync).RequireAuthorization();
        endpoints.MapPost("/driver/manage/hires/{hireId}", RespondDriverOfferAsync).RequireAuthorization();
        endpoints.MapPost("/driver-network/{tenantId}/{operation}", ManageDriverNetworkAsync).RequireAuthorization();
        endpoints.MapPost("/driver-payments/{tenantId}/{paymentId}/{operation}", ManageDriverPaymentAsync).RequireAuthorization();
    }

    private static async Task<IResult> SaveDriverAccountAsync(string operation, HttpContext context, IAntiforgery antiforgery, RestaurantManagementClient api)
    {
        var read = await ReadAsync(context, antiforgery);
        if (read.Failure is { } failure) return failure;
        var page = operation == "payout" ? "/driver/earnings" : "/driver";
        try
        {
            var form = read.Form!;
            if (operation == "payout")
            {
                var request = Text(form, "request_key", 36, true);
                if (!Guid.TryParseExact(request, "D", out _) || !Checkbox(form, "confirm_us")) return NetworkRedirect(page, "confirm-payout");
                var result = await api.StartDriverPayoutAsync(new(request, true), read.Token!, context.RequestAborted);
                if (result.Succeeded && DriverWorkFormat.HostedUrl(result.Value?.Url, "connect.stripe.com") is { } safe) return Results.Redirect(safe);
                return NetworkResponse(page, result, "unavailable");
            }
            if (operation != "profile") return Results.NotFound();
            if (!int.TryParse(form["capacity"], NumberStyles.None, CultureInfo.InvariantCulture, out var capacity) || capacity is < 1 or > 10) throw new FormFailure("invalid");
            var zips = Text(form, "zips", 500, multiline: true).Split([',', ' ', '\r', '\n', '\t'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Distinct(StringComparer.Ordinal).ToArray();
            if (zips.Length is < 1 or > 50 || zips.Any(zip => !Zip().IsMatch(zip))) throw new FormFailure("invalid");
            return NetworkResponse(page, await api.SaveNetworkDriverAsync(new(Version(form), Text(form, "name", 80, true),
                Text(form, "bio", 1000, multiline: true), zips, Checkbox(form, "listed"), capacity), read.Token!, context.RequestAborted));
        }
        catch (FormFailure) { return NetworkRedirect(page, "invalid"); }
    }

    private static async Task<IResult> RespondDriverOfferAsync(string hireId, HttpContext context, IAntiforgery antiforgery, RestaurantManagementClient api)
    {
        if (!Identifier().IsMatch(hireId)) return Results.NotFound();
        var read = await ReadAsync(context, antiforgery);
        if (read.Failure is { } failure) return failure;
        try
        {
            var response = Text(read.Form!, "response", 10, true);
            if (response is not ("accept" or "decline")) throw new FormFailure("invalid");
            return NetworkResponse("/driver", await api.RespondDriverHireAsync(hireId, new(Version(read.Form!), response == "accept"), read.Token!, context.RequestAborted));
        }
        catch (FormFailure) { return NetworkRedirect("/driver", "invalid"); }
    }

    private static async Task<IResult> ManageDriverNetworkAsync(string tenantId, string operation, HttpContext context, IAntiforgery antiforgery, RestaurantManagementClient api)
    {
        if (!Identifier().IsMatch(tenantId)) return Results.NotFound();
        var read = await ReadAsync(context, antiforgery);
        if (read.Failure is { } failure) return failure;
        var page = NetworkPage(tenantId, "driver-network");
        try
        {
            var form = read.Form!;
            if (operation == "offer")
            {
                var driver = Text(form, "driver", 128, true);
                if (!Identifier().IsMatch(driver)) throw new FormFailure("invalid");
                return NetworkResponse(page, await api.OfferDriverHireAsync(tenantId, new(driver, DriverPay(form, "pay"), Text(form, "notes", 500, multiline: true), Version(form)), read.Token!, context.RequestAborted));
            }
            if (operation == "end")
            {
                var hire = Text(form, "hire", 128, true);
                if (!Identifier().IsMatch(hire) || !Checkbox(form, "confirm_end")) return NetworkRedirect(page, "confirm-end");
                return NetworkResponse(page, await api.EndDriverHireAsync(tenantId, hire, new(Version(form)), read.Token!, context.RequestAborted));
            }
            return Results.NotFound();
        }
        catch (FormFailure) { return NetworkRedirect(page, "invalid"); }
    }

    private static async Task<IResult> ManageDriverPaymentAsync(string tenantId, string paymentId, string operation, HttpContext context, IAntiforgery antiforgery, RestaurantManagementClient api)
    {
        if (!Identifier().IsMatch(tenantId) || !Identifier().IsMatch(paymentId)) return Results.NotFound();
        var read = await ReadAsync(context, antiforgery);
        if (read.Failure is { } failure) return failure;
        var page = NetworkPage(tenantId, "driver-payments");
        try
        {
            var form = read.Form!;
            var pageText = form["page"].ToString();
            var returnPage = 0;
            if (pageText.Length > 0 && (!int.TryParse(pageText, NumberStyles.None, CultureInfo.InvariantCulture, out returnPage) || returnPage is < 0 or > 100000)) throw new FormFailure("invalid");
            page += "?page=" + returnPage.ToString(CultureInfo.InvariantCulture);
            if (operation == "review") return Results.LocalRedirect(page + "&payment=" + Uri.EscapeDataString(paymentId) + "&pay=" + DriverPay(form, "pay").ToString(CultureInfo.InvariantCulture));
            if (operation == "refresh") return NetworkResponse(page, await api.RefreshDriverPaymentAsync(tenantId, paymentId, read.Token!, context.RequestAborted), "refreshed");
            if (operation != "approve") return Results.NotFound();
            if (!Checkbox(form, "confirm_payment")) return NetworkRedirect(page, "confirm-payment");
            if (!int.TryParse(form["pay_cents"], NumberStyles.None, CultureInfo.InvariantCulture, out var pay) || pay is < 50 or > 100000) throw new FormFailure("invalid");
            var result = await api.ApproveDriverPaymentAsync(tenantId, paymentId, new(Version(form), pay, true, returnPage), read.Token!, context.RequestAborted);
            if (result.Succeeded && DriverWorkFormat.HostedUrl(result.Value?.Url, "checkout.stripe.com") is { } safe) return Results.Redirect(safe);
            return NetworkResponse(page, result, "refreshed");
        }
        catch (FormFailure) { return NetworkRedirect(page, "invalid"); }
    }

    private static int DriverPay(IFormCollection form, string field)
    {
        var amount = Amount(form, field, 100000);
        return amount is >= 50 ? amount.Value : throw new FormFailure("invalid");
    }
    private static string NetworkPage(string tenant, string page) => "/workspace/" + Uri.EscapeDataString(tenant) + "/" + page;
    private static IResult NetworkRedirect(string page, string notice) => Results.LocalRedirect(page + (page.Contains('?') ? "&" : "?") + "notice=" + notice);
    private static IResult NetworkResponse<T>(string page, RestaurantApiResult<T> result, string success = "saved") => NetworkRedirect(page,
        result.Succeeded ? success : (int)result.Status is 401 or 403 ? "denied"
        : result.Code == "driver_payments_disabled" ? "disabled" : result.Code == "driver_payment_setup" ? "driver-setup"
        : result.Code == "payment_review" ? "payment-review" : result.Code == "payment_amount" ? "payment-amount"
        : result.Uncertain ? "unconfirmed" : (int)result.Status == 409 ? "changed" : "invalid");
    public static string? DriverNetworkNotice(string? code) => code switch
    {
        "saved" => "Your change is saved.", "refreshed" => "The payment status has been refreshed. Review the latest details below.",
        "changed" => "These details changed or an active delivery prevents this action. Review the latest information before trying again.",
        "denied" => "Your account does not have permission for this action. Only the business owner can approve driver payments.",
        "unconfirmed" => "We could not confirm the change. Refresh and review the current status before trying again.",
        "unavailable" => "Payout setup could not be opened. Refresh your payout status and try again shortly.",
        "disabled" => "Driver payments are not enabled yet. No payment was started.",
        "driver-setup" => "The driver must finish their Stripe payout setup before this payment can be approved.",
        "payment-review" => "This payment needs review. Do not create another payment for the same delivery.",
        "payment-amount" => "The agreed or previously approved driver pay cannot be changed. Refresh to review the saved amount.",
        "confirm-payment" => "Review the exact driver pay, fee and total, then check the approval box to continue.",
        "confirm-payout" => "Confirm that you are setting up payouts as a US individual before continuing.",
        "confirm-end" => "Check the confirmation before ending this hiring agreement.",
        "invalid" => "The change was not saved. Check the fields, use five-digit ZIP codes and a delivery capacity of 1–10. Driver pay must be $0.50–$1,000.00 with at most two decimal places.", _ => null
    };
}
