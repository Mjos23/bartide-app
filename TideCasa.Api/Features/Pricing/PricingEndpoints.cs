using TideCasa.Contracts;
using TideCasa.Api.Features.ServiceBilling;

namespace TideCasa.Api.Features.Pricing;

public static class PricingEndpoints
{
    public static void MapPricing(this WebApplication app)
    {
        app.MapPost("/api/v1/pricing/quote", (QuoteRequest request) =>
        {
            // Never accept a client-provided discount or price. Referral lookup must be ported before enabling it.
            if (!string.IsNullOrWhiteSpace(request.ReferralCode))
                return Results.Problem(statusCode: 503, title: "Referral validation is not connected yet. Your code has not been applied.");
            var stores = request.AppStores ? 30000 : 0;
            return Results.Ok(new PackageQuote(60000, stores, 0, 0, 60000 + stores, ServiceBillingProvider.MonthlyCents, "usd", ServiceBillingProvider.TermsVersion));
        }).WithName("QuotePackage").WithTags("Pricing").Produces<PackageQuote>().ProducesProblem(503);
    }
}
