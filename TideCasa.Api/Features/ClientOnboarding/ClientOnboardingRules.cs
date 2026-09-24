using System.Data.Common;
using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using TideCasa.Api.Infrastructure;
using TideCasa.Contracts;

namespace TideCasa.Api.Features.ClientOnboarding;

public sealed partial class ClientOnboardingStore
{
    private static Answers Defaults(string name, JsonObject menu)
    {
        var venue = menu["venue"] as JsonObject ?? new();
        return new(new(name, "", "", "", "", "", "", "US", "America/New_York", Text(venue, "website_url"),
            Enumerable.Range(0, 7).Select(day => new OnboardingHours(day, true, "", "")).ToArray()),
            new("coastal", "#0F766E", Text(venue, "tagline"), "text", null, null),
            new(false, false, false, false, true, false, true, null, [], [], 0, 0, 5, "", ""),
            new(true, "", true, true, true));
    }

    private static string Field(string? value, string name, int max, bool required = false, bool multiline = false)
    {
        value = value?.Trim() ?? "";
        if (value.Length > max || required && value.Length == 0 || value.Any(c => char.IsControl(c) && !(multiline && c is '\r' or '\n' or '\t')))
            throw new OnboardingException($"{name}: enter {(required ? "1 to" : "up to")} {max} characters.");
        return value;
    }
    private static bool Email(string value) => Regex.IsMatch(value, "^[^\\s@]+@[^\\s@]+\\.[^\\s@]+$");
    private static string? Photo(string? value) => string.IsNullOrWhiteSpace(value) ? null : Guid.TryParseExact(value, "D", out _) ? value.ToLowerInvariant() : throw new OnboardingException("Choose a photo from your file library.");

    private static OnboardingBusiness Validate(OnboardingBusiness p)
    {
        if (p is null) throw new OnboardingException("Enter your business details.");
        var name = Field(p.Name, "Business name", 120, true); var email = Field(p.PublicEmail, "Public email", 254);
        var phone = Field(p.Phone, "Public phone", 30); var website = Field(p.Website, "Website", 2048);
        if (email.Length > 0 && !Email(email)) throw new OnboardingException("Enter a valid public contact email.");
        if (phone.Length > 0 && (phone.Count(char.IsAsciiDigit) is < 7 or > 15 || phone.Any(c => !char.IsAsciiDigit(c) && !"+()- .".Contains(c)))) throw new OnboardingException("Enter a valid public phone number.");
        if (website.Length > 0 && (!Uri.TryCreate(website, UriKind.Absolute, out var url) || url.Scheme != "https" || url.UserInfo.Length > 0)) throw new OnboardingException("Use an HTTPS website address.");
        if (p.Country != "US") throw new OnboardingException("This setup currently supports US businesses.");
        var zone = Field(p.TimeZone, "Time zone", 100, true);
        if (!TimeZoneInfo.TryFindSystemTimeZoneById(zone, out _)) throw new OnboardingException("Choose a supported time zone.");
        var region = Field(p.Region, "State", 2).ToUpperInvariant(); var zip = Field(p.PostalCode, "ZIP code", 10);
        if (region.Length > 0 && !Regex.IsMatch(region, "^[A-Z]{2}$") || zip.Length > 0 && !Regex.IsMatch(zip, "^[0-9]{5}(-[0-9]{4})?$")) throw new OnboardingException("Use a two-letter state and a valid ZIP code.");
        if (p.Hours is null || p.Hours.Count != 7 || p.Hours.Any(h => h is null || h.Day is < 0 or > 6) || p.Hours.Select(h => h.Day).Distinct().Count() != 7)
            throw new OnboardingException("Set hours or closed for all seven days.");
        var hours = p.Hours.OrderBy(h => h.Day).Select(h =>
        {
            if (h.Closed) return new OnboardingHours(h.Day, true, "", "");
            if (!TimeOnly.TryParseExact(h.Opens, "HH:mm", CultureInfo.InvariantCulture, DateTimeStyles.None, out var open)
                || !TimeOnly.TryParseExact(h.Closes, "HH:mm", CultureInfo.InvariantCulture, DateTimeStyles.None, out var close)
                || (!h.Overnight && close <= open) || h.Overnight && close > open)
                throw new OnboardingException("Enter opening and closing times; mark overnight when closing the next day.");
            return h;
        }).ToArray();
        return new(name, email, phone, Field(p.Address, "Street address", 200), Field(p.City, "City", 100), region, zip, "US", zone, website, hours);
    }

    private static OnboardingBrand Validate(OnboardingBrand p)
    {
        if (p is null || p.Style is not ("coastal" or "classic" or "night") || p.Accent is null || !Regex.IsMatch(p.Accent, "^#[a-fA-F0-9]{6}$")
            || p.LogoChoice is not ("text" or "photo")) throw new OnboardingException("Choose a style, an accent color and a logo option.");
        var logo = Photo(p.LogoPhotoId); var cover = Photo(p.CoverPhotoId);
        if (p.LogoChoice == "text") logo = null;
        return new(p.Style, p.Accent.ToUpperInvariant(), Field(p.Introduction, "Introduction", 250, multiline: true), p.LogoChoice, logo, cover);
    }

    private static OnboardingService Validate(OnboardingService p)
    {
        if (p is null || p.TaxBasisPoints is < 0 or > 2500 || p.DeliveryFeeCents is < 0 or > 5000 || p.DeliveryMinimumCents is < 0 or > 100000
            || p.DeliveryCapacity is < 1 or > 30 || p.TableLabels is null || p.TableLabels.Count > 100 || p.DeliveryZips is null || p.DeliveryZips.Count > 50)
            throw new OnboardingException("Check the tax, delivery amounts, capacity and table list.");
        var tables = p.TableLabels.Select(t => Field(t, "Table label", 40, true)).ToArray();
        var zips = p.DeliveryZips.Select(z => Field(z, "Delivery ZIP", 5, true)).ToArray();
        if (tables.Distinct(StringComparer.OrdinalIgnoreCase).Count() != tables.Length || zips.Distinct().Count() != zips.Length || zips.Any(z => !Regex.IsMatch(z, "^[0-9]{5}$")))
            throw new OnboardingException("Use unique table names and five-digit delivery ZIP codes.");
        return p with { TableLabels = tables, DeliveryZips = zips, PickupInstructions = Field(p.PickupInstructions, "Pickup instructions", 500, multiline: true), PaymentInstructions = Field(p.PaymentInstructions, "Payment instructions", 500, multiline: true) };
    }
    private static OnboardingTeam Validate(OnboardingTeam p) => p is null ? throw new OnboardingException("Choose who will handle orders.")
        : p with { OrderContact = Field(p.OrderContact, "Order contact", 254) };
    internal static string HoursText(IReadOnlyList<OnboardingHours> hours) => string.Join("; ", hours.OrderBy(h => h.Day)
        .Select(h => CultureInfo.InvariantCulture.DateTimeFormat.AbbreviatedDayNames[h.Day] + ": " + (h.Closed ? "Closed" : h.Opens + "–" + h.Closes + (h.Overnight ? " next day" : ""))));

    internal static void ApplyService(JsonObject config, OnboardingService p, string phone)
    {
        config["enabled"] = false; config["accepting_orders"] = false;
        config["tax_basis_points"] = p.TaxBasisPoints; config["delivery_enabled"] = p.Ordering && p.Delivery;
        config["delivery_fee_cents"] = p.DeliveryFeeCents; config["delivery_minimum_cents"] = p.DeliveryMinimumCents;
        config["delivery_capacity"] = p.DeliveryCapacity; config["delivery_zips"] = JsonSerializer.SerializeToNode(p.DeliveryZips, Json);
        config["pickup_instructions"] = p.PickupInstructions; config["payment_instructions"] = p.PaymentInstructions;
        config["contact_phone"] = phone;
        var checkout = config["checkout"] as JsonObject;
        if (checkout is null) { checkout = new(); config["checkout"] = checkout; }
        var old = checkout["tables"]?.Deserialize<List<RestaurantTable>>(Json) ?? [];
        var labels = new HashSet<string>(p.TableLabels, StringComparer.OrdinalIgnoreCase);
        var tables = old.Select(t => t with { Enabled = p.Ordering && p.DineIn && labels.Contains(t.Label) }).ToList();
        foreach (var label in p.TableLabels)
            if (!tables.Any(t => t.Label.Equals(label, StringComparison.OrdinalIgnoreCase)))
                tables.Add(new(Guid.NewGuid().ToString("D"), label, Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(32)), p.Ordering && p.DineIn));
        if (tables.Count > 100) throw new OnboardingException("This workspace supports 100 table records. Reuse existing table labels.");
        checkout["tables"] = JsonSerializer.SerializeToNode(tables, Json); checkout["dine_in_enabled"] = p.Ordering && p.DineIn;
        checkout["pickup_enabled"] = p.Ordering && p.Pickup; checkout["pay_staff_enabled"] = p.PayStaff; checkout["tips_enabled"] = p.Tips;
    }

    private static OnboardingService ProjectService(JsonObject c, OnboardingService saved)
    {
        if (c["checkout"] is not JsonObject checkout) return saved;
        bool Flag(JsonObject node, string key, bool fallback) => node[key]?.GetValue<bool>() ?? fallback;
        int Number(string key, int fallback) => c[key]?.GetValue<int>() ?? fallback;
        var tables = checkout["tables"]?.Deserialize<List<RestaurantTable>>(Json);
        var dineIn = Flag(checkout, "dine_in_enabled", saved.DineIn);
        return saved with { DineIn = dineIn, Pickup = Flag(checkout, "pickup_enabled", saved.Pickup),
            Delivery = Flag(c, "delivery_enabled", saved.Delivery), PayStaff = Flag(checkout, "pay_staff_enabled", saved.PayStaff), Tips = Flag(checkout, "tips_enabled", saved.Tips),
            TaxBasisPoints = c["tax_basis_points"]?.GetValue<int>(), TableLabels = tables is null || !saved.Ordering || !dineIn ? saved.TableLabels : tables.Where(t => t.Enabled).Select(t => t.Label).ToArray(),
            DeliveryZips = c["delivery_zips"]?.Deserialize<List<string>>(Json) ?? saved.DeliveryZips, DeliveryFeeCents = Number("delivery_fee_cents", saved.DeliveryFeeCents),
            DeliveryMinimumCents = Number("delivery_minimum_cents", saved.DeliveryMinimumCents), DeliveryCapacity = Number("delivery_capacity", saved.DeliveryCapacity),
            PickupInstructions = c["pickup_instructions"]?.GetValue<string>() ?? saved.PickupInstructions, PaymentInstructions = c["payment_instructions"]?.GetValue<string>() ?? saved.PaymentInstructions };
    }

    private static async Task<List<OnboardingCheck>> Checks(DbConnection db, DbTransaction tx, State s, CancellationToken ct)
    {
        var checks = new List<OnboardingCheck>(); var a = s.Answers; var b = a.Business; var service = a.Service;
        void Check(string code, string section, bool ready, string done, string todo, string? path = null) => checks.Add(new(code, section, ready ? "ready" : "needs_attention", ready ? done : todo, path ?? Setup(s.Id, section)));
        Check("business_contact", "business", b.Name.Length > 0 && Email(b.PublicEmail) && b.Phone.Count(char.IsAsciiDigit) >= 7, "Public business contact details are complete.", "Add your public email and phone number.");
        Check("business_address", "business", b.Address.Length > 0 && b.City.Length > 0 && b.Region.Length == 2 && b.PostalCode.Length >= 5 && b.Country == "US", "Your venue address is complete.", "Add your street address, city, state and ZIP code.");
        Check("business_hours", "business", b.Hours.Count == 7 && b.Hours.Any(h => !h.Closed) && s.Editor.Profile.Hours == HoursText(b.Hours), "Weekly hours and time zone are saved.", "Confirm and save your weekly opening hours here, including closed days, so every guest page matches.");
        Check("brand_style", "brand", a.Brand.Introduction.Length > 0, "Your style and introduction are saved.", "Add a short introduction for your guests.");
        var assets = new[] { a.Brand.LogoPhotoId, a.Brand.CoverPhotoId }.Concat(s.Editor.Items.Select(i => i.PhotoId)).Where(p => p is not null).Distinct().ToArray();
        var assetsReady = a.Brand.LogoChoice == "text" || a.Brand.LogoPhotoId is not null;
        foreach (var asset in assets)
            assetsReady &= await Count(db, tx, "SELECT COUNT(*) FROM bartide_photos WHERE id=@photo AND tenant_id=@id AND status='ready'", ct, ("@photo", asset), ("@id", s.Id)) == 1;
        Check("brand_assets", "brand", assetsReady, "Selected photos are ready; your logo choice is confirmed.", "Choose a ready logo/photo or use a text logo.", "/workspace/" + s.Id + "/media");
        Check("menu_items", "brand", s.Editor.Items.Count > 0 && (!service.Ordering || s.Editor.Items.Any(i => i.Available && i.PriceCents is not null)),
            "Your menu is ready to review.", service.Ordering ? "Add at least one available menu item with an exact price." : "Add your menu items; uploading a source file alone does not create the menu.", "/workspace/" + s.Id + "/menu");
        Check("service_modes", "service", !service.Ordering || service.DineIn || service.Pickup || service.Delivery, "Your service choices are saved.", "Choose table ordering, pickup or delivery, or select menu-only.");
        Check("service_tax", "service", !service.Ordering || service.TaxBasisPoints is not null, "Tax is confirmed for the selected launch scope.", "Confirm the tax rate before enabling ordering.");
        // The current production merchant implementation is sandbox-only. Do not infer live readiness from any test capability.
        Check("service_payment", "service", !service.Ordering || service.PayStaff || service.RequestCards, "A payment preference is selected.", "Select paying staff or request online card payments.");
        Check("service_tables", "service", !service.Ordering || !service.DineIn || service.TableLabels.Count > 0, "Table setup matches your service choices.", "Add your table names or numbers.");
        Check("service_delivery", "service", !service.Ordering || !service.Delivery || service.DeliveryZips.Count > 0, "Delivery setup matches your service choices.", "Add the ZIP codes you deliver to.");
        var contactReady = a.Team.OwnerHandlesOrders || (a.Team.OrderContact.Length > 0 && await Count(db, tx, db.Sql(
            "SELECT COUNT(*) FROM bartide_enhanced_members WHERE tenant_id=@id AND active=1 AND role IN('manager','bartender','server') AND email=@email COLLATE NOCASE",
            "SELECT COUNT(*) FROM bartide_enhanced_members WHERE tenant_id=@id AND active=1 AND role IN('manager','bartender','server') AND lower(email)=lower(@email)"), ct, ("@id", s.Id), ("@email", a.Team.OrderContact)) == 1);
        Check("team_contact", "team", !service.Ordering || contactReady, "Order responsibility is confirmed.", "Choose yourself or enter an active order-handling team member's sign-in email.", Setup(s.Id, "team"));
        var paid = await SetupPaid(db, tx, s.Id, ct);
        checks.Add(new("plan_payment", "payments", paid ? "ready" : "waiting", paid ? "Your BarTide setup payment is recorded." : "Your setup payment needs verification or refund review. Check your existing purchase before paying again; verified payment starts the scheduled build period.", "/workspace/" + s.Id + "/billing"));
        checks.Add(new("merchant_payments", "payments", service.Ordering && service.RequestCards ? "waiting" : "optional",
            service.Ordering && service.RequestCards ? "Online card payments need BarTide's live payment verification before launch. Test status cannot enable real payments." : "Guest card payments are deferred for your selected launch scope.", "/workspace/" + s.Id + "/payments"));
        return checks;
    }
    private static bool CoreReady(IEnumerable<OnboardingCheck> checks) => checks.Where(c => c.Section is "business" or "brand" or "service" or "team").All(c => c.State == "ready");

    /// <summary>Called within the publication transaction, so stale content/approval cannot slip through a concurrent edit.</summary>
    internal static async Task<string?> LaunchBlockAsync(DbConnection db, DbTransaction tx, string id, CancellationToken ct, bool publish = false)
    {
        var raw = await Scalar(db, tx, "SELECT settings_json FROM bartide_enhanced_configs WHERE tenant_id=@id", ct, ("@id", id)) as string;
        if (raw is null || Parse(raw)["onboarding"] is null) return null; // Preserve established legacy launch contracts.
        var s = await ReadState(db, tx, id, false, ct); var release = Release(s);
        if (!CoreReady(await Checks(db, tx, s, ct))) return "Complete the client's required business, menu, service and team details.";
        if (release is null || release.Fingerprint != await Fingerprint(db, tx, s, ct)
            || Text(s.Workflow, "approvedRelease") != release.Id || Text(s.Workflow, "approvedBy") != s.Owner)
            return "The business owner must approve the current private preview before launch.";
        if (s.Answers.Service.Ordering && s.Answers.Service.RequestCards)
            return "Live restaurant card payments have not been verified. Keep this launch waiting, or have the owner approve a different payment scope.";
        if (s.Answers.Service.Ordering && !s.Answers.Service.PayStaff) return "The approved ordering scope needs an available payment method.";
        if (!await SetupPaid(db, tx, id, ct))
            return "Verify the BarTide setup payment and resolve any pending or completed setup refunds before launch.";
        if (publish)
        {
            s.Config["enabled"] = true; s.Config["accepting_orders"] = s.Answers.Service.Ordering;
            s.Workflow["publishedRelease"] = release.Id; s.Workflow["publishedAt"] = Now();
            await WriteConfig(db, tx, s, ct);
        }
        return null;
    }

    private static async Task<bool> SetupPaid(DbConnection db, DbTransaction tx, string id, CancellationToken ct) =>
        await Count(db, tx, """
            SELECT COUNT(*) FROM tide_service_orders o JOIN tide_service_invoices i ON i.id=o.initial_invoice_id AND i.order_id=o.id
            WHERE o.tenant_id=@id AND o.environment='live' AND o.status='paid' AND i.kind='initial' AND i.status='paid'
            AND i.paid_at IS NOT NULL AND i.amount_cents=o.total_cents AND i.refunded_cents=0 AND i.refund_pending_cents=0
            """, ct, ("@id", id)) > 0;
}
