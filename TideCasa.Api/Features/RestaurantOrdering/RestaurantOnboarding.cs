using System.Data.Common;
using System.Text.Json;
using System.Text.Json.Nodes;
using TideCasa.Api.Features.ClientOnboarding;
using TideCasa.Contracts;

namespace TideCasa.Api.Features.RestaurantOrdering;

public sealed partial class RestaurantOrderingStore
{
    // Internal callers have already checked tenant access in their containing transaction.
    internal static async Task<RestaurantMenuEditor> ReadOnboardingMenuAsync(DbConnection db, DbTransaction tx, string id, CancellationToken ct)
    {
        var venue = await ReadVenueAsync(db, tx, id, true, ct, allowPreparation: true);
        var menu = await RawMenuAsync(db, tx, id, ct); var p = menu["venue"] as JsonObject ?? throw Unavailable();
        return new(id, venue.Slug, venue.MenuVersion, new(venue.Name, String(p, "area"), String(p, "tagline"), String(p, "hours_text"),
            String(p, "website_url"), String(p, "service_note")), venue.Categories, venue.Items);
    }

    internal static RestaurantQuote OnboardingQuote(OnboardingRelease release, RestaurantQuoteRequest request)
    {
        if (request is null) throw new OnboardingException("Choose an item and service type for the practice check.");
        if (!release.Service.Ordering || release.Service.TaxBasisPoints is null) throw new OnboardingException("Save ordering choices and confirm tax before a practice check.", 409, "setup_incomplete");
        if (request.PaymentMethod != "staff" || !release.Service.PayStaff) throw new OnboardingException("Practice currently checks the pay-staff flow. It does not verify card processing.");
        var config = new JsonObject(); ClientOnboardingStore.ApplyService(config, release.Service, release.Business.Phone);
        // Only an in-memory copy is opened; this function has no database or payment-provider collaborator.
        config["enabled"] = true; config["accepting_orders"] = true;
        var tables = config["checkout"]!["tables"]!.Deserialize<List<RestaurantTable>>(Json)!;
        var venue = new Venue(release.TenantId, release.Slug, release.Business.Name, 0, null, config, 0, release.Categories, release.Items, tables, "draft", "bartide");
        return Quote(venue, request) with { CanSubmit = false };
    }
}
