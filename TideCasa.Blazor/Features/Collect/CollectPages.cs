using System.Globalization;
using System.Text;
using System.Text.Encodings.Web;

namespace TideCasa.Blazor.Features.Collect;

/// <summary>Server-rendered Collect pages. Authorization remains in the route and domain layers.</summary>
public static class CollectPages
{
    static string H(object? value) => HtmlEncoder.Default.Encode(Convert.ToString(value, CultureInfo.InvariantCulture) ?? "");
    static string Money(decimal cents) => (cents / 100m).ToString("C2", CultureInfo.GetCultureInfo("en-US"));
    static string Amount(long cents) => (cents / 100m).ToString("0.00", CultureInfo.InvariantCulture);
    static string Date(DateTimeOffset value) => value.ToString("MMM d, yyyy", CultureInfo.InvariantCulture);
    static string Day(DateOnly value) => value.ToString("MMM d, yyyy", CultureInfo.InvariantCulture);
    static string Hidden(string name, object? value) => $"<input type=\"hidden\" name=\"{H(name)}\" value=\"{H(value)}\">";
    static string Button(string label, string style = "") => $"<button class=\"button {H(style)}\" type=\"submit\">{H(label)}</button>";
    static string Link(string path, string label, string style = "") => $"<a class=\"button {H(style)}\" href=\"{H(path)}\">{H(label)}</a>";
    static string Tag(string text, string style = "") => $"<span class=\"tag {H(style)}\">{H(Status(text))}</span>";
    static string Status(string text) => text switch { "document-reviewed" => "Creditor reviewed", "uploaded" => "Submitted", "clarification-needed" => "Needs clarification", _ => text.Replace('-', ' ') };
    static string Input(string name, string label, string value = "", string type = "text", string extra = "") => $"<label class=\"field\"><span>{H(label)}</span><input name=\"{H(name)}\" type=\"{H(type)}\" value=\"{H(value)}\" {extra}></label>";
    static string Select(string name, string label, string selected, params (string Value, string Text)[] options) => $"<label class=\"field\"><span>{H(label)}</span><select name=\"{H(name)}\">{string.Concat(options.Select(x => $"<option value=\"{H(x.Value)}\"{(x.Value == selected ? " selected" : "")}>{H(x.Text)}</option>"))}</select></label>";
    static string StartForm(CollectView v, string action, CollectCase? item = null) => $"<form method=\"post\" action=\"/collect/action/{H(action)}\">{Hidden("__RequestVerificationToken", v.Csrf)}{Hidden("revision", v.Client.Revision)}{(item == null ? "" : Hidden("case_id", item.Id))}";
    static string PreviewForm(string csrf, string role, string label, string style = "") => $"<form method=\"post\" action=\"/collect/start\">{Hidden("__RequestVerificationToken", csrf)}{Hidden("role", role)}{Button(label, style)}</form>";
    static string Heading(string eyebrow, string title, string text) => $"<header class=\"page-heading\"><p class=\"eyebrow\">{H(eyebrow)}</p><h1>{H(title)}</h1><p class=\"lede\">{H(text)}</p></header>";
    static string Panel(string title, string content, string text = "") => $"<section class=\"panel\"><div class=\"panel-heading\"><h2>{H(title)}</h2>{(text.Length > 0 ? $"<p>{H(text)}</p>" : "")}</div>{content}</section>";
    static string Metric(string label, string value, string note) => $"<div class=\"metric\"><p>{H(label)}</p><strong>{H(value)}</strong><span>{H(note)}</span></div>";

    public static string Home(string csrf)
    {
        var content = $$"""
        <section class="front-door" aria-labelledby="entrance-heading">
          <header class="entrance-heading"><p class="eyebrow">Two sides. One direct conversation.</p><h1 id="entrance-heading">Meet in the middle.</h1><p>Choose your door. We’ll help you start the conversation.</p><nav class="door-shortcuts" aria-label="Choose your entrance"><a href="#creditor-door">I’m a creditor ↓</a><a href="#debtor-door">I’m a debtor ↓</a></nav></header>
          <div class="door-pair">
            <section class="entrance-door creditor-door" id="creditor-door" aria-labelledby="creditor-door-heading">
              <div class="door-topline"><span class="door-number">01</span><p class="door-label">For creditors</p><span class="door-symbol" aria-hidden="true">↗</span></div>
              <div class="door-message"><p class="door-kicker">Your customers. Your assessment.</p><h2 id="creditor-door-heading">Stop relying on<br>a middleman.<br><em>See for yourself.</em></h2><p>Give customers considering Beyond Finance a direct route to your team. Review the documents they choose to share, ask questions, and make your own assessment.</p></div>
              <div class="door-bottom"><a class="button door-button" href="/client">Enter the creditor office <span aria-hidden="true">↗</span></a><p>$1,500 build + $199/month</p><a class="door-detail" href="/collect/client">Your own branded office. Your own decisions.</a></div>
              <span class="door-handle" aria-hidden="true"></span>
            </section>
            <section class="entrance-door debtor-door" id="debtor-door" aria-labelledby="debtor-door-heading">
              <div class="door-topline"><span class="door-number">02</span><p class="door-label">For debtors</p><span class="door-symbol" aria-hidden="true">↗</span></div>
              <div class="door-message"><p class="door-kicker">Your finances. Your voice.</p><h2 id="debtor-door-heading">Debt relief<br>shouldn’t leave you<br><em>deeper in debt.</em></h2><p>Keep a direct line to your creditor. Share your financial picture on your terms and discuss a proposal yourself—with no Collect fee for debtor access.</p></div>
              <div class="door-bottom"><a class="button door-button" href="/collect/signup">Enter the debtor hub <span aria-hidden="true">↗</span></a><p>Free to join. Free to use.</p><a class="door-detail" href="/collect/directory">Already know your creditor? Find their office →</a></div>
              <span class="door-handle" aria-hidden="true"></span>
            </section>
          </div>
          <div class="door-threshold"><span>Independent signup or an invitation. Both lead here.</span><a href="#compare-fees">Compare the fees ↓</a></div>
          <p class="entrance-preview-note">You’re viewing a fictional preview. Real financial intake, creditor activation and payments are not open.</p>
        </section>
        <section class="home-hero fee-section" id="compare-fees" aria-labelledby="fee-story-heading"><div class="hero-copy"><p class="eyebrow">Before you choose a program</p><h2 id="fee-story-heading">Know the cost<br>of the middleman.</h2><p class="hero-description">Considering Beyond Finance? Compare its published program fees with Collect’s $0 debtor access. Put the fees in perspective before you decide.</p><div class="settlement-context"><h3>Understand what you’re signing up for.</h3><p>Debt settlement and a consolidation loan are different. The CFPB warns that settlement programs may ask you to stop paying creditors, which can add fees and interest, damage your credit, and expose you to lawsuits. An agreement isn’t guaranteed.</p><a href="https://www.consumerfinance.gov/ask-cfpb/what-is-a-debt-relief-program-and-how-do-i-know-if-i-should-use-one-en-1457/" rel="noopener noreferrer">Read the CFPB’s explanation ↗</a></div><div class="preview-link">{{PreviewForm(csrf, "debtor", "Explore the debtor preview →", "text-button")}}<span>Fictional data only</span></div></div><section class="fee-calculator" aria-labelledby="fee-heading"><div class="section-line"><p class="eyebrow">Keep more of your money</p><span class="tag">Fee comparison</span></div><h2 id="fee-heading">What could program fees cost?</h2><p class="calculator-intro">Change the figures to compare fees. Your entries stay in this browser.</p><div class="calculator-fields"><label class="field"><span>Debt enrolled in a program ($)</span><input id="fee-debt" type="number" min="0" max="10000000" step="100" value="20000" inputmode="decimal"></label><label class="field"><span>Program fee rate (%)</span><input id="fee-rate" type="number" min="0" max="100" step="0.1" value="25" inputmode="decimal"></label></div><div class="fee-results" aria-live="polite" aria-atomic="true"><div class="fee-line"><span>Estimated program fee</span><output id="fee-program" for="fee-debt fee-rate">$5,000</output></div><div class="fee-line collect-fee"><span>Collect debtor access</span><strong>$0</strong></div><div class="fee-difference"><span>Difference in fees</span><output id="fee-difference" for="fee-debt fee-rate">$5,000</output></div></div><p id="fee-validation" class="small calculator-validation" role="status"></p><p class="calculator-source">Beyond Finance reports typical fees of 15–25% of enrolled debt, varying by state and debt. A fee is earned after an accepted settlement and a payment toward it. <a href="https://www.beyondfinance.com/program/" rel="noopener noreferrer">Published program details ↗</a> · checked September 26, 2026.</p><noscript><p class="small">JavaScript is off. The example above uses $20,000 × 25% = $5,000. Enable JavaScript to update the comparison.</p></noscript></section></section>
        <p class="comparison-disclosure">Beyond Finance provides a managed negotiation service. Collect gives you tools to negotiate directly; creditors decide whether to agree. This compares platform/program fees, not total repayment or guaranteed savings. Collect’s free access does not remove your debt or any agreed creditor charges.</p>
        <section class="value-strip" aria-label="How Collect works"><div><span class="step-number">01</span><h2>Start with your picture</h2><p>Create an account on your own terms. Your personal profile stays private until you choose to share.</p></div><div><span class="step-number">02</span><h2>Find the right office</h2><p>Search for a participating creditor or follow an invitation to their dedicated office.</p></div><div><span class="step-number">03</span><h2>Make a thoughtful proposal</h2><p>Review the account, ask questions and discuss clear terms in one place.</p></div></section>
        <section class="office-promo"><div><p class="eyebrow">For creditors &amp; collection teams</p><h2>Your office.<br>A more informed conversation.</h2><p>Give customers a dedicated place to reach you, and receive inquiries from people who have already taken the first step.</p><p class="client-price"><strong>$1,500 build + $199/month</strong><span>$1,699 initially, including your first month.</span></p>{{Link("/client", "Explore the client workspace", "secondary")}}</div><div class="office-promo-list"><div><span>01</span><p><strong>An office with your name on it</strong>A branded destination you can share directly.</p></div><div><span>02</span><p><strong>Financial context, with permission</strong>Evaluate the customer’s submitted documents, including a handwritten budget. Your team makes the assessment.</p></div><div><span>03</span><p><strong>Agreements that stay clear</strong>Versioned proposals, defined authority and a transparent payment schedule.</p></div></div></section>
        <section class="notice home-notice"><strong>We’re opening thoughtfully.</strong><p>Explore a working preview with fictional creditors and accounts. Real financial-document intake and live payments are not open. Registration does not open a live debt negotiation account.</p></section>
        """;
        return Shell("A clearer way forward", content, null, "home");
    }

    public static string Workspace(CollectView view, string section)
    {
        var content = section switch
        {
            "case" => CasePage(view), "profile" => ProfilePage(view), "directory" => DirectoryPage(view),
            "office" => OfficePage(view), "client" => ClientPage(view), "inquiries" => InquiriesPage(view),
            "analytics" => AnalyticsPage(view), "readiness" => ReadinessPage(view), _ => Overview(view)
        };
        return Shell(section == "case" ? "Account details" : Title(section), content, view, section);
    }

    static string Title(string section) => section switch { "overview" => "Your workspace", "profile" => "Your private profile", "directory" => "Find a creditor", "client" => "Client office", "inquiries" => "Inquiries", "analytics" => "Financial comparison", "readiness" => "Pilot status", "office" => "Creditor office", _ => "Your workspace" };
    static IEnumerable<CollectCase> Visible(CollectView v) => v.Client.Cases.Where(x => CollectRules.Staff(v.Client, v.Actor) || x.DebtorId == v.Actor.Id);
    static bool Owner(CollectView v) => v.Actor.Id == v.Personal.OwnerId;

    static string Overview(CollectView v)
    {
        var staff = CollectRules.Staff(v.Client, v.Actor); var cases = Visible(v).ToArray();
        if (!v.Actor.Preview) return Heading("Your account", "You’re here. Your next step is yours.", "Your account is ready. The financial workspace is in a limited preview while participating creditors and launch safeguards are prepared.") +
            "<div class=\"grid two\">" + Panel("Find a creditor", "<p>Explore the fictional directory to see how independent discovery works. Searching does not disclose your identity or financial profile.</p>" + Link("/collect/directory", "Browse directory", "secondary")) + Panel("Explore the experience", "<p>Try the complete journey with a separate fictional profile. Do not enter real debts or personal financial information.</p>" + PreviewForm(v.Csrf, "debtor", "Open fictional preview")) + "</div>";
        var b = new StringBuilder(Heading(staff ? v.Client.Name : "Your workspace", staff ? "Room for a better conversation." : "Let’s find a way forward.", staff ? "Review financial context, respond to inquiries and keep each agreement clear." : "Your profile is yours. Choose a creditor, review your account and start a conversation when you’re ready."));
        b.Append("<div class=\"metrics\">").Append(Metric(staff ? "Accounts in your office" : "Your accounts", cases.Length.ToString(), "Fictional preview"))
            .Append(Metric("Open proposals", cases.Sum(x => x.Offers.Count(o => o.State == "open")).ToString(), "Awaiting the other party"))
            .Append(Metric("Agreements", cases.Count(x => x.Agreement != null).ToString(), "Terms recorded in one place")).Append("</div>");
        if (!staff) b.Append("<div class=\"next-step\"><div><span class=\"eyebrow\">Start independently</span><h2>You don’t need an invitation.</h2><p>Build your private profile and find a participating creditor. A search never shares your information.</p></div>").Append(Link("/collect/directory", "Find a creditor", "secondary")).Append("</div>");
        b.Append(Panel(staff ? "Accounts" : "Your accounts", CaseList(cases), "Open an account for its itemization, documents, proposals and payment schedule."));
        b.Append("<div class=\"grid two\">").Append(Panel(staff ? "Incoming inquiries" : "Your private profile", staff ? $"<p>{v.Client.Inquiries.Count} inquiries received. A profile is included only when the customer explicitly chooses to share it.</p>{Link("/collect/inquiries", "View inquiries", "secondary")}" : "<p>Keep your overall financial picture in one place. Sharing a snapshot with one creditor does not give every creditor access.</p>" + Link("/collect/profile", "View profile", "secondary")))
            .Append(Panel("Your creditor makes the assessment", "<p>Collect gives customers a place to submit financial context, including a handwritten budget. The creditor evaluates what is supplied; the app does not certify authenticity.</p><p class=\"small muted\">All preview documents and outcomes are fictional.</p>")).Append("</div>");
        return b.ToString();
    }
    static string CaseList(IEnumerable<CollectCase> items)
    {
        var rows = items.Select(x => $"<a class=\"account-row\" href=\"/collect/case/{H(x.Id)}\"><span class=\"account-symbol\" aria-hidden=\"true\">▥</span><span class=\"account-name\"><strong>{H(x.CreditorName)}</strong><small>{H(x.Reference)} · {H(x.DebtorLabel)}</small></span><span class=\"account-value\"><strong>{H(Money(x.BalanceCents))}</strong><small>{H(x.Agreement != null ? "Agreement recorded" : x.Disputes.Any(d => d.State == "open") ? "Review requested" : "Ready to review")}</small></span><span class=\"arrow\" aria-hidden=\"true\">↗</span></a>").ToArray();
        return rows.Length == 0 ? "<div class=\"empty\"><h3>No accounts connected yet</h3><p>An inquiry does not acknowledge a debt or automatically connect an account.</p></div>" : "<div class=\"account-list\">" + string.Concat(rows) + "</div>";
    }

    static string ProfilePage(CollectView v)
    {
        var b = new StringBuilder(Heading("Private by default", "Your financial picture.", "Create a profile independently. Share a snapshot with a specific creditor only when you choose to make an inquiry."));
        if (!v.Actor.Preview || !Owner(v)) return b.Append(Panel("Personal profile", "<p>Personal financial intake is available only to the fictional debtor in this preview. Real document and financial intake is not open.</p>" + (v.Actor.Preview ? "<p>Use the preview role selector to explore the debtor’s personal workspace.</p>" : PreviewForm(v.Csrf, "debtor", "Try the fictional profile")))).ToString();
        b.Append("<div class=\"grid profile-grid\">").Append(Panel("Income & essential expenses", ProfileForm(v, v.Personal.Profile, null, "personal"), "Use fictional amounts only. All expenses are monthly; income keeps its selected pay period and gross/net basis."))
            .Append(Panel("You control the next step", "<div class=\"privacy-mark\" aria-hidden=\"true\">◇</div><h3>Your profile stays with you.</h3><p>Finding an office shares nothing. An inquiry shares your name and request; adding a profile requires a separate choice.</p><p>Each shared profile is a snapshot. Later edits here do not rewrite earlier disclosures or the financial profile in an existing case.</p>" + Link("/collect/directory", "Find a creditor", "secondary"))).Append("</div>");
        return b.ToString();
    }
    static string ProfileForm(CollectView v, CollectProfile? p, CollectCase? item, string target)
    {
        var b = new StringBuilder(StartForm(v, "profile", item) + Hidden("target", target));
        b.Append("<div class=\"form-grid\">").Append(Input("income", "Income per pay period ($)", Amount(p?.IncomeCents ?? 0), "number", "min=\"0\" max=\"10000000\" step=\"0.01\" required"))
            .Append(Select("period", "Pay period", p?.Period ?? "monthly", ("weekly", "Weekly"), ("biweekly", "Every two weeks"), ("semimonthly", "Twice a month"), ("monthly", "Monthly")))
            .Append(Select("basis", "Income basis", p?.Basis ?? "net", ("net", "Net · take-home"), ("gross", "Gross · before deductions"))).Append("</div><h3 class=\"form-section\">Monthly essential expenses</h3><div class=\"form-grid\">");
        foreach (var (key, label) in new[] { ("housing", "Housing ($)"), ("utilities", "Utilities ($)"), ("food", "Food ($)"), ("transport", "Transport ($)"), ("health", "Health ($)"), ("other", "Other essentials ($)") })
            b.Append(Input(key, label, Amount(p?.Expenses.FirstOrDefault(x => x.Category == key)?.MonthlyCents ?? 0), "number", "min=\"0\" max=\"10000000\" step=\"0.01\" required"));
        b.Append("</div><p class=\"small muted\">Self-reported information. These figures do not determine what you can afford or automatically approve a proposal.</p>").Append(Button(target == "personal" ? "Save private profile" : "Save case profile")).Append("</form>");
        return b.ToString();
    }

    static string DirectoryPage(CollectView v)
    {
        var query = v.Path ?? "";
        var entries = CollectRules.Directory.Where(x => string.IsNullOrWhiteSpace(query) || x.Name.Contains(query, StringComparison.OrdinalIgnoreCase)).ToArray();
        var b = new StringBuilder(Heading("The creditor directory", "Find your next conversation.", "Find a participating creditor’s office. Browsing and searching do not share your identity, profile or financial information."));
        b.Append("<form class=\"search-form\" method=\"get\" action=\"/collect/directory\"><label class=\"field\"><span>Search by creditor name</span><input type=\"search\" name=\"q\" maxlength=\"100\" placeholder=\"Enter a creditor’s name\" value=\"").Append(H(query)).Append("\"></label>").Append(Button("Search", "secondary")).Append("</form><div class=\"section-line\"><h2>Explore offices</h2><span class=\"small muted\">Fictional directory · ").Append(entries.Length).Append(" results</span></div><div class=\"grid two\">");
        foreach (var entry in entries) b.Append("<article class=\"directory-card\"><div class=\"directory-icon\" aria-hidden=\"true\">▥</div>").Append(Tag("Fictional creditor", "quiet")).Append("<h2>").Append(H(entry.Name)).Append("</h2><p>").Append(H(entry.Description)).Append("</p>").Append(Link("/c/" + entry.Slug, "Visit office ↗", "secondary")).Append("</article>");
        b.Append("</div>");
        if (entries.Length == 0) b.Append("<div class=\"empty\"><h2>No matching office yet</h2><p>This preview lists fictional creditors only. It is not a complete directory of creditors or debts.</p><a href=\"/collect/directory\">View all sample offices</a></div>");
        return b.Append("<p class=\"small muted end-note\">A listing is not a debt validation, identity verification or endorsement. Only a creditor’s authorized team can confirm an account relationship.</p>").ToString();
    }
    static string OfficePage(CollectView v)
    {
        var entry = CollectRules.Directory.FirstOrDefault(x => x.Slug == v.Path);
        if (entry == null) return $"<div class=\"office-header\"><div class=\"directory-icon\" aria-hidden=\"true\">▥</div>{Tag("Fictional client office")}<h1>{H(v.Client.Name)}</h1><p>A dedicated space for thoughtful conversations.</p><p class=\"small\">Your office on Collect · {H(v.Client.Kind.Replace('-', ' '))}</p></div><div class=\"grid two\">" + Panel("Arrive on your own terms", "<p>Join Collect independently or follow an invitation from this office. Your profile is private until you choose to share a snapshot with a specific creditor.</p><p>There is no charge for debtor access.</p>" + Link("/collect/signup", "Create a free account", "secondary")) + Panel("Start a conversation", "<p>Explore this office’s fictional creditors to see how inquiries work. Existing invited accounts appear in your workspace.</p><div class=\"actions\">" + Link("/collect/directory", "Find your creditor", "secondary") + Link("/collect/account", "Your workspace", "secondary") + "</div><p class=\"small muted\">Real financial intake and live payments are not open.</p>") + "</div>";
        var b = new StringBuilder($"<div class=\"office-header\"><div class=\"directory-icon\" aria-hidden=\"true\">▥</div>{Tag("Fictional creditor office")}<h1>{H(entry.Name)}</h1><p>{H(entry.Description)}</p><p class=\"small\">A dedicated office on Collect</p></div><div class=\"grid two\">");
        b.Append(Panel("A place to begin", "<p>You can start a conversation even if you joined Collect independently. An inquiry does not admit liability, validate a debt or commit you to a payment.</p><p>Already invited to review an account? Your connected accounts are in your workspace.</p>" + Link("/collect/account", "Go to your workspace", "secondary")));
        if (v.Actor.Preview && Owner(v))
        {
            var existing = v.Personal.Inquiries.Any(x => x.CreditorId == entry.Id && x.State == "received");
            var form = existing ? "<div class=\"notice\"><strong>Your inquiry is in.</strong><p>This office has received your fictional inquiry. View exactly what you shared in your inquiry history.</p></div>" + Link("/collect/inquiries", "View your inquiry", "secondary") : StartForm(v, "inquire") + Hidden("creditor_id", entry.Id) +
                "<p>The office will receive your preview name and a request to discuss an account.</p><label class=\"check-field\"><input type=\"checkbox\" name=\"share_profile\" value=\"yes\"><span>Also share a snapshot of my current financial profile with this creditor’s office.</span></label><label class=\"check-field\"><input type=\"checkbox\" name=\"consent\" value=\"yes\" required><span>I choose to send this fictional inquiry to this office. I understand that sharing my profile is optional.</span></label>" + Button("Send fictional inquiry") + "</form>";
            b.Append(Panel("Reach this office", form, "Your profile is not shared unless you select the separate sharing option."));
        }
        else b.Append(Panel("Join on your own terms", "<p>Create your own Collect account, then find the offices you need. The current directory is a demonstration; real inquiries are not open.</p><div class=\"actions\">" + Link("/collect/signup", "Create an account") + PreviewForm(v.Csrf, "debtor", "Try a fictional inquiry", "secondary") + "</div>"));
        return b.Append("</div>").ToString();
    }

    static string ClientPage(CollectView v)
    {
        var b = new StringBuilder(Heading("For creditors & collection teams", "Your own office. A shared way forward.", "Invite customers to your branded office and hear from people who found you independently."));
        b.Append("<div class=\"pricing-banner\"><div><span class=\"eyebrow\">A dedicated client office</span><strong>$1,500 <small>one-time build</small> <span>+</span> $199 <small>/ month</small></strong><p>$1,699 initially, including the first month. Debtor access is always free.</p></div><span class=\"tag\">No billing in this preview</span></div>");
        if (CollectRules.Role(v.Client, v.Actor) != "admin") return b.Append(Panel("See the client experience", "<p>Manage a distinct office, review explicitly shared financial context and keep creditor payees separate from collection agencies.</p>" + (v.Actor.Preview ? StartForm(v, "switch") + Hidden("role", "admin") + Button("Explore the office admin view") + "</form>" : PreviewForm(v.Csrf, "admin", "Explore a fictional client office")) + "<p class=\"small muted\">Client onboarding for real accounts is not open. The preview has no real debts, messages or payments.</p>")).ToString();
        b.Append("<div class=\"grid two\">");
        b.Append(Panel("Office identity", StartForm(v, "brand") + Input("name", "Office name", v.Client.Name, "text", "maxlength=\"80\" required") + Input("slug", "Office address", v.Client.Slug, "text", "pattern=\"[a-z0-9-]+\" maxlength=\"60\" required") +
            "<p class=\"small muted\">collect.tide.casa/c/your-office</p>" + Input("accent", "Brand accent", v.Client.Accent, "color") + Select("kind", "Client type", v.Client.Kind, ("collection-agency", "Collection agency"), ("original-creditor", "Original creditor")) + Button("Save office") + "</form>"));
        b.Append(Panel("Office status", $"<p>{Tag(v.Client.State)}</p><p>Your public office gives customers one destination. Each case keeps the actual creditor and payment recipient separate from your agency.</p><p><strong>Market:</strong> {H(v.Client.Market)}</p>" + Link("/c/" + v.Client.Slug, "View office", "secondary") + (v.Client.State != "active" ? StartForm(v, "activate") + "<p class=\"small muted\">Preview activation uses fictional accounts only.</p>" + Button("Activate preview office") + "</form>" : "") + "<p class=\"small muted\">Activating a fictional office does not authorize real collection activity.</p>"));
        b.Append(Panel("Import sample accounts", StartForm(v, "import") + "<label class=\"field\"><span>Account CSV · fictional data only</span><textarea name=\"csv\" rows=\"7\" maxlength=\"16384\" required placeholder=\"reference,creditor_id,creditor_name,principal,interest,fees,credits,minimum\"></textarea></label><p class=\"small muted\">Required header: reference,creditor_id,creditor_name,principal,interest,fees,credits,minimum. Amounts are USD. Imports keep creditor identity separate and repeated identical imports are ignored.</p>" + Button("Import sample accounts", "secondary") + "</form>"));
        b.Append(Panel("Invite a team member", StartForm(v, "invite") + Input("email", "Email address", "", "email", "maxlength=\"254\" required autocomplete=\"off\"") + Select("role", "Office role", "reviewer", ("reviewer", "Document reviewer"), ("negotiator", "Negotiator")) + "<p class=\"small muted\">Preview invitations are not emailed. A real invitation must be claimed by its matching verified account.</p>" + Button("Create preview invitation", "secondary") + "</form>"));
        b.Append("</div>");
        return b.ToString();
    }

    static string InquiriesPage(CollectView v)
    {
        var staff = CollectRules.Staff(v.Client, v.Actor); var items = staff ? v.Client.Inquiries : v.Personal.Inquiries.Where(x => x.CustomerId == v.Actor.Id).ToList();
        var b = new StringBuilder(Heading(staff ? "Your office" : "Your conversations", staff ? "People taking the first step." : "See what you’ve shared.", staff ? "An inquiry connects a person to an office. It does not prove an account relationship or acknowledge a debt." : "Every inquiry records the creditor and the profile snapshot you chose to share. Later profile edits do not change that snapshot."));
        if (items.Count == 0) return b.Append(Panel("No inquiries yet", "<p>Start by finding an office. Your financial profile stays private unless you make a separate choice to share it.</p>" + Link("/collect/directory", "Find a creditor", "secondary"))).ToString();
        foreach (var inquiry in items.OrderByDescending(x => x.At))
        {
            var creditor = CollectRules.Directory.FirstOrDefault(x => x.Id == inquiry.CreditorId)?.Name ?? inquiry.CreditorId;
            b.Append(Panel(creditor, $"<div class=\"section-line\"><span>{H(inquiry.CustomerLabel)} · {H(Date(inquiry.At))}</span>{Tag(inquiry.State)}</div>" + (inquiry.SharedProfile == null ? "<p><strong>No financial profile shared.</strong> This inquiry contains a name and request only.</p>" : "<p><strong>Profile snapshot shared by the customer.</strong> Self-reported information for the creditor to evaluate.</p>" + ProfileSummary(inquiry.SharedProfile))));
        }
        return b.ToString();
    }
    static string ProfileSummary(CollectProfile? p) => p == null ? "<p class=\"muted\">No financial profile supplied.</p>" : $"<dl class=\"facts\"><div><dt>Reported income</dt><dd>{H(Money(p.IncomeCents))} / {H(p.Period)}</dd></div><div><dt>Income basis</dt><dd>{H(p.Basis)}</dd></div><div><dt>Monthly equivalent</dt><dd>{H(Money(CollectRules.Monthly(p.IncomeCents, p.Period)))}</dd></div><div><dt>Monthly essentials reported</dt><dd>{H(Money(p.Expenses.Sum(x => x.MonthlyCents)))}</dd></div><div><dt>Snapshot date</dt><dd>{H(Date(p.At))}</dd></div></dl>";

    static string CasePage(CollectView v)
    {
        var item = Visible(v).FirstOrDefault(x => x.Id == v.Path) ?? (string.IsNullOrEmpty(v.Path) ? Visible(v).FirstOrDefault() : null);
        if (item == null) return Heading("Account", "This account is unavailable.", "Return to your workspace to see accounts connected to you.") + Link("/collect/account", "Your workspace", "secondary");
        var debtor = item.DebtorId == v.Actor.Id; var role = CollectRules.Role(v.Client, v.Actor); var canReview = role is "admin" or "reviewer"; var canNegotiate = debtor || role is "admin" or "negotiator";
        var b = new StringBuilder($"<a class=\"back-link\" href=\"/collect/account\">← Your workspace</a>" + Heading(item.Reference, item.CreditorName, $"Managed by {v.Client.Name} · {item.DebtorLabel}"));
        b.Append("<div class=\"metrics\">").Append(Metric("Itemized balance", Money(item.BalanceCents), "USD · fictional account"))
            .Append(Metric("Agreement", item.Agreement == null ? "Not yet agreed" : Money(item.Agreement.TotalCents), item.Agreement == null ? "You can ask questions first" : "Recorded terms below"))
            .Append(Metric("Creditor payee", item.CreditorName, "Payments would go to the creditor")).Append("</div>");
        if (item.Disputes.Any(x => x.State == "open")) b.Append("<div class=\"notice warning\"><strong>Account review requested.</strong><p>Negotiation and payments are paused while an open request is reviewed.</p></div>");
        if (item.SupportRequested) b.Append("<div class=\"notice\" role=\"status\"><strong>Support request recorded.</strong><p>Your fictional request is visible to this office. No message has been sent outside the preview.</p></div>");
        b.Append("<nav class=\"anchor-nav\" aria-label=\"Account sections\"><a href=\"#account\">Account</a><a href=\"#financial\">Financial picture</a><a href=\"#documents\">Documents</a><a href=\"#proposals\">Proposals</a><a href=\"#support\">Questions &amp; preferences</a></nav>");
        b.Append("<div id=\"account\" class=\"grid two\">").Append(Panel("Account itemization", $"<dl class=\"facts\"><div><dt>Principal</dt><dd>{H(Money(item.PrincipalCents))}</dd></div><div><dt>Interest</dt><dd>{H(Money(item.InterestCents))}</dd></div><div><dt>Fees</dt><dd>{H(Money(item.FeesCents))}</dd></div><div><dt>Credits</dt><dd>−{H(Money(item.CreditsCents))}</dd></div><div class=\"total\"><dt>Balance</dt><dd>{H(Money(item.BalanceCents))}</dd></div></dl><p class=\"small muted\">Itemization date: {H(Day(item.ItemizationDate))}</p>"))
            .Append(Panel("Account notice", $"<p>{H(item.Notice)}</p><p>{Tag(item.NoticeStatus)}</p><p class=\"small muted\">You can view your account, ask for support or dispute it without providing income documents.</p>")).Append("</div>");
        if (role == "admin" && item.DebtorId == null) b.Append(Panel("Connect the customer", StartForm(v, "invite", item) + Hidden("role", "debtor") + Input("email", "Customer’s email address", "", "email", "maxlength=\"254\" required autocomplete=\"off\"") + "<p class=\"small muted\">A real invitation must be claimed by the matching verified account. Preview invitations are not emailed.</p>" + Button("Create preview invitation", "secondary") + "</form>"));
        b.Append("<div id=\"financial\" class=\"grid two\">").Append(Panel("This account’s financial profile", debtor ? ProfileForm(v, item.Profile, item, "case") : ProfileSummary(item.Profile), "The case profile is separate from the customer’s private overall profile."))
            .Append(Panel("Reported & documented income", Comparison(item), "A comparison describes supplied information. It is not an authenticity check, fraud score or ability-to-pay decision.")).Append("</div>");
        b.Append("<div id=\"documents\">").Append(Panel("Documents for review", Evidence(v, item, debtor, canReview), "Only generated fictional samples are available here. Real document uploads are closed.")).Append("</div>");
        b.Append("<div id=\"proposals\">").Append(Panel(item.Agreement == null ? "Work toward an agreement" : "Your recorded agreement", item.Agreement == null ? Offers(v, item, canNegotiate, debtor) : AgreementBody(v, item), "Review the creditor, amount, schedule and conditions before accepting.")).Append("</div>");
        b.Append("<div id=\"support\" class=\"grid two\">").Append(Panel("Questions & account review", Disputes(v, item, debtor), "Financial-document submission is never required to ask a question or request a review."));
        if (debtor) b.Append(Panel("Communication preferences", StartForm(v, "preference", item) + Select("opt_out", "Optional email messages", item.EmailOptOut ? "true" : "false", ("false", "Allow optional email"), ("true", "Opt out of optional email")) + "<p class=\"small muted\">The preview does not send messages. Essential notices and applicable delivery rules must be configured before a real launch.</p>" + Button("Save preference", "secondary") + "</form>"));
        b.Append("</div>");
        return b.ToString();
    }
    static string Comparison(CollectCase item)
    {
        var evidence = item.Evidence.Where(x => x.Kind == "paystub").OrderByDescending(x => x.UploadedAt).FirstOrDefault();
        var comparison = CollectRules.Compare(item.Profile, evidence, DateTimeOffset.UtcNow);
        if (comparison.Difference == null) return "<div class=\"comparison-empty\"><span class=\"comparison-icon\" aria-hidden=\"true\">≈</span><h3>Not comparable yet</h3><p>" + H(comparison.Reason) + "</p></div>";
        var signed = (comparison.Difference > 0 ? "+" : comparison.Difference < 0 ? "−" : "") + Money(Math.Abs(comparison.Difference.Value));
        return $"<div class=\"comparison-value\"><span>Creditor-reviewed minus reported · monthly</span><strong>{H(signed)}</strong><p>{H(comparison.Percent == null ? comparison.Reason : comparison.Percent.Value.ToString("+0.##;-0.##;0", CultureInfo.InvariantCulture) + "% of reported income")}</p></div><p class=\"small muted\">Weekly × 52 ÷ 12; every two weeks × 26 ÷ 12; twice monthly × 2. Gross and net amounts are compared only on matching bases.</p><p class=\"small muted\">Document: {H(evidence!.FileName)} · {H(Status(evidence.Status))} · review expires {H(evidence.Expires is { } expiry ? Date(expiry) : "not recorded")}</p>";
    }
    static string Evidence(CollectView v, CollectCase item, bool debtor, bool canReview)
    {
        var b = new StringBuilder();
        if (debtor) b.Append(StartForm(v, "sample-evidence", item)).Append("<div class=\"inline-form\">").Append(Select("kind", "Add a fictional document", "paystub", ("paystub", "Sample paystub"), ("bill", "Sample bill"), ("budget", "Sample handwritten budget"))).Append(Button("Add sample", "secondary")).Append("</div></form>");
        if (item.Evidence.Count == 0) b.Append("<div class=\"empty compact\"><p>No documents yet. Add a fictional sample as the debtor, then switch to a reviewer to examine it.</p></div>");
        foreach (var e in item.Evidence.OrderByDescending(x => x.UploadedAt))
        {
            b.Append("<article class=\"evidence-row\"><div><strong>").Append(H(e.FileName)).Append("</strong><p class=\"small muted\">").Append(H(e.Kind)).Append(" · ").Append(H(Date(e.UploadedAt))).Append(" · ").Append(H(e.Synthetic ? "Generated fictional sample" : "Supplied document")).Append("</p><p>").Append(Tag(Status(e.Status))).Append(" ").Append(Tag(e.Synthetic ? "Sample file" : e.Scan, "quiet")).Append("</p>");
            if (e.ReviewedAt is { } reviewed) b.Append("<p class=\"small muted\">Reviewed ").Append(H(Date(reviewed))).Append(" · method: ").Append(H(e.Method)).Append(" · valid through ").Append(H(e.Expires is { } exp ? Date(exp) : "not recorded")).Append("</p>");
            if (debtor || canReview) b.Append("<a href=\"/collect/document/").Append(H(item.Id)).Append('/').Append(H(e.Id)).Append("\">View sample document ↗</a>");
            b.Append("</div>");
            if (canReview && e.Scan == "clean" && (e.Reviewer == null || e.Status == "clarification-needed")) b.Append(StartForm(v, "review", item)).Append(Hidden("evidence_id", e.Id)).Append(Select("status", "Creditor review outcome", "document-reviewed", ("document-reviewed", "Creditor reviewed"), ("clarification-needed", "Needs clarification"))).Append(Button("Record review", "secondary")).Append("</form>");
            b.Append("</article>");
        }
        return b.Append("<p class=\"small muted\">Submitted → creditor reviewed. Creditors evaluate the customer’s documents, including handwritten budgets, and may request clarification. Collect does not certify authenticity.</p>").ToString();
    }
    static string Offers(CollectView v, CollectCase item, bool canNegotiate, bool debtor)
    {
        var b = new StringBuilder(); var last = item.Offers.LastOrDefault();
        if (last != null)
        {
            var active = last.State == "open" && last.Expires > DateTimeOffset.UtcNow;
            b.Append("<article class=\"offer-card\"><div class=\"section-line\"><h3>Proposal ").Append(last.Version).Append("</h3>").Append(Tag(active ? "Awaiting response" : last.State == "open" ? "expired" : last.State)).Append("</div><p class=\"small muted\">Proposed by ").Append(H(last.Party == "debtor" ? "the customer" : v.Client.Name)).Append(" · expires ").Append(H(Date(last.Expires))).Append("</p><dl class=\"facts\"><div><dt>Creditor / payee</dt><dd>").Append(H(item.CreditorName)).Append("</dd></div><div><dt>Negotiating office</dt><dd>").Append(H(v.Client.Name)).Append("</dd></div><div><dt>Customer</dt><dd>").Append(H(item.DebtorLabel)).Append("</dd></div><div><dt>Proposed total</dt><dd>").Append(H(Money(last.TotalCents))).Append("</dd></div><div><dt>Monthly installments</dt><dd>").Append(last.Installments).Append("</dd></div><div><dt>First due date</dt><dd>").Append(H(Day(last.FirstDate))).Append("</dd></div></dl><h4>Conditions</h4><p class=\"preserve-lines\">").Append(H(string.IsNullOrWhiteSpace(last.Terms) ? "No additional conditions supplied." : last.Terms)).Append("</p>");
            b.Append(Schedule(last.TotalCents, last.Installments, last.FirstDate));
            if (active && canNegotiate)
            {
                var proposer = debtor == (last.Party == "debtor");
                if (!proposer) b.Append(StartForm(v, "accept", item)).Append(Hidden("offer_id", last.Id)).Append("<label class=\"check-field\"><input type=\"checkbox\" name=\"consent\" value=\"yes\" required><span>I have reviewed the exact creditor, total, installment dates and conditions. I agree to this fictional proposal.</span></label>").Append(Button("Accept fictional proposal")).Append("</form>");
                else b.Append(StartForm(v, "withdraw", item)).Append(Hidden("offer_id", last.Id)).Append(Button("Withdraw proposal", "secondary")).Append("</form>");
            }
            b.Append("</article>");
        }
        else b.Append("<div class=\"empty compact\"><h3>Start with a proposal.</h3><p>Each proposal is recorded as a new version. The other party can review the exact terms before accepting.</p></div>");
        if (canNegotiate && !item.Disputes.Any(x => x.State == "open") && v.Client.State == "active" && item.AuthorityRecorded && item.NoticeStatus == "issued")
            b.Append("<details class=\"proposal-form\"" + (last == null ? " open" : "") + "><summary>").Append(last == null ? "Create a proposal" : "Make a new proposal or counteroffer").Append("</summary>").Append(StartForm(v, "offer", item)).Append("<div class=\"form-grid\">")
            .Append(Input("amount", "Total proposed amount ($)", Amount(item.BalanceCents), "number", "min=\"0.01\" step=\"0.01\" required")).Append(Input("installments", "Monthly installments", "3", "number", "min=\"1\" max=\"60\" required")).Append(Input("first_date", "First payment date", DateOnly.FromDateTime(DateTime.UtcNow.AddDays(14)).ToString("yyyy-MM-dd"), "date", "required")).Append("</div><label class=\"field\"><span>Conditions to review together</span><textarea name=\"terms\" rows=\"3\" maxlength=\"1200\" placeholder=\"Describe the terms you are proposing.\"></textarea></label><p class=\"small muted\">A new proposal replaces any open proposal. No payment is taken. Amounts outside the recorded authority cannot be accepted here.</p>").Append(Button(last == null ? "Send fictional proposal" : "Send new proposal")).Append("</form></details>");
        if (item.Offers.Count > 1) b.Append("<details class=\"history\"><summary>Proposal history · ").Append(item.Offers.Count).Append(" versions</summary><ol>").Append(string.Concat(item.Offers.OrderByDescending(x => x.Version).Select(o => $"<li>Version {o.Version} · {H(Money(o.TotalCents))} · {o.Installments} installments · {H(o.State)} · {H(Date(o.Created))}</li>"))).Append("</ol></details>");
        return b.ToString();
    }
    static string Schedule(long total, int count, DateOnly first) => ScheduleTable(Enumerable.Range(1, count).Select(n => new CollectInstallment(n, first.AddMonths(n - 1), total / count + (n <= total % count ? 1 : 0))));
    static string ScheduleTable(IEnumerable<CollectInstallment> schedule) => "<div class=\"table-wrap\"><table><caption>Monthly payment schedule</caption><thead><tr><th scope=\"col\">Installment</th><th scope=\"col\">Due date</th><th scope=\"col\">Amount</th></tr></thead><tbody>" + string.Concat(schedule.Select(x => $"<tr><td>{x.Number}</td><td>{H(Day(x.Due))}</td><td>{H(Money(x.Cents))}</td></tr>")) + "</tbody></table></div>";
    static string AgreementBody(CollectView v, CollectCase item)
    {
        var a = item.Agreement!;
        var b = new StringBuilder($"<p>{Tag(a.Synthetic ? "Fictional agreement" : "Agreement recorded")}</p><dl class=\"facts\"><div><dt>Creditor / payee</dt><dd>{H(a.CreditorName)}</dd></div><div><dt>Negotiating office</dt><dd>{H(a.ClientName)}</dd></div><div><dt>Total agreed</dt><dd>{H(Money(a.TotalCents))}</dd></div><div><dt>Accepted</dt><dd>{H(Date(a.AcceptedAt))}</dd></div></dl><h3>Conditions</h3><p class=\"preserve-lines\">{H(string.IsNullOrWhiteSpace(a.Terms) ? "No additional conditions supplied." : a.Terms)}</p>{ScheduleTable(a.Schedule)}");
        b.Append(Link("/collect/agreement/" + item.Id, "Open printable agreement", "secondary"));
        b.Append("<div class=\"notice payment-notice\"><strong>Payment simulation only</strong><p>No card details, bank details or money are collected. Live payments require an eligible provider. Any future payment must be payable directly to the recorded creditor.</p></div>");
        if (item.DebtorId == v.Actor.Id && v.Actor.Preview)
        {
            foreach (var installment in a.Schedule)
            {
                var payment = item.Payments.FirstOrDefault(x => x.Installment == installment.Number);
                b.Append("<div class=\"payment-row\"><div><strong>Installment ").Append(installment.Number).Append(" · ").Append(H(Money(installment.Cents))).Append("</strong><p class=\"small muted\">Payee: ").Append(H(a.CreditorName)).Append("</p></div>");
                if (payment == null && !item.Disputes.Any(x => x.State == "open")) b.Append(StartForm(v, "payment", item)).Append(Hidden("installment", installment.Number)).Append(Button("Simulate payment", "secondary")).Append("</form>");
                else if (payment != null)
                {
                    b.Append("<div>").Append(Tag("Simulated · " + payment.State));
                    var states = payment.State switch { "pending" => new[] { ("succeeded", "Success received"), ("failed", "Failed") }, "succeeded" => new[] { ("settled", "Settled"), ("returned", "Returned") }, "settled" => new[] { ("returned", "Returned"), ("refunded", "Refunded") }, _ => Array.Empty<(string, string)>() };
                    if (states.Length > 0) b.Append(StartForm(v, "payment-event", item)).Append(Hidden("payment_id", payment.Id)).Append(Select("state", "Next simulated event", states[0].Item1, states)).Append(Button("Apply simulated event", "secondary")).Append("</form>");
                    b.Append("</div>");
                }
                b.Append("</div>");
            }
        }
        else foreach (var payment in item.Payments) b.Append("<p>").Append(Tag("Simulated · " + payment.State)).Append(" Installment ").Append(payment.Installment).Append(" · ").Append(H(Money(payment.Cents))).Append("</p>");
        return b.ToString();
    }
    static string Disputes(CollectView v, CollectCase item, bool debtor)
    {
        var b = new StringBuilder();
        if (debtor) b.Append(StartForm(v, "dispute", item)).Append(Select("category", "What would you like to do?", "support", ("support", "Ask for support"), ("dispute", "Dispute this account"), ("original-creditor", "Request original-creditor information"))).Append("<p class=\"small muted\">This preview records a fictional request. A real office must provide its applicable written dispute and notice procedures.</p>").Append(Button("Record request", "secondary")).Append("</form>");
        foreach (var dispute in item.Disputes.OrderByDescending(x => x.At))
        {
            b.Append("<div class=\"request-row\"><div><strong>").Append(H(dispute.Category.Replace('-', ' '))).Append("</strong><p class=\"small muted\">").Append(H(Date(dispute.At))).Append(" · ").Append(H(dispute.State)).Append("</p></div>");
            if (CollectRules.Role(v.Client, v.Actor) == "admin" && dispute.State == "open") b.Append(StartForm(v, "resolve", item)).Append(Hidden("dispute_id", dispute.Id)).Append(Button("Resolve fictional request", "secondary")).Append("</form>");
            b.Append("</div>");
        }
        if (!debtor && item.Disputes.Count == 0) b.Append("<p class=\"muted\">No review requests recorded.</p>");
        return b.ToString();
    }

    static string AnalyticsPage(CollectView v)
    {
        var b = new StringBuilder(Heading("Bangel Debt Analytics", "Understand the differences.", "Compare reported income with reviewed document figures. Missing information stays visible, and a difference is never treated as proof of fraud or a recovery prediction."));
        if (!CollectRules.Staff(v.Client, v.Actor)) return b.Append(Panel("Office access required", "<p>Portfolio comparisons are available to authorized office staff. Your own account’s comparison appears within that account.</p>" + Link("/collect/account", "Your accounts", "secondary"))).ToString();
        var cases = Visible(v).ToArray();
        var results = cases.Select(c => (Case: c, Evidence: c.Evidence.Where(e => e.Kind == "paystub").OrderByDescending(e => e.UploadedAt).FirstOrDefault())).Select(x => (x.Case, x.Evidence, Value: CollectRules.Compare(x.Case.Profile, x.Evidence, DateTimeOffset.UtcNow))).ToArray();
        b.Append("<div class=\"metrics\">").Append(Metric("Accounts", cases.Length.ToString(), "Current office only")).Append(Metric("Comparable", results.Count(x => x.Value.Difference != null).ToString(), "Reviewed, current, matching basis")).Append(Metric("Not comparable", results.Count(x => x.Value.Difference == null).ToString(), "Kept visible in this report")).Append("</div>");
        b.Append(Panel("Income comparison", "<div class=\"table-wrap\"><table><caption>Reviewed minus reported · monthly USD equivalent</caption><thead><tr><th>Account</th><th>Difference</th><th>Percent</th><th>Evidence / reason</th></tr></thead><tbody>" + string.Concat(results.Select(x => $"<tr><td><a href=\"/collect/case/{H(x.Case.Id)}\">{H(x.Case.Reference)}</a><small>{H(x.Case.CreditorName)}</small></td><td>{H(x.Value.Difference is { } difference ? (difference > 0 ? "+" : difference < 0 ? "−" : "") + Money(Math.Abs(difference)) : "Unavailable")}</td><td>{H(x.Value.Percent is { } percent ? percent.ToString("+0.##;-0.##;0", CultureInfo.InvariantCulture) + "%" : "—")}</td><td>{H(x.Value.Reason)}<small>{H(x.Evidence?.FileName ?? "No paystub supplied")}</small></td></tr>")) + "</tbody></table></div>"));
        b.Append("<div class=\"grid two\">").Append(Panel("How to read a difference", "<p>Positive means the reviewed document’s monthly equivalent is higher than reported. Negative means it is lower. A zero reported amount has no percentage comparison.</p><p>Weekly income × 52 ÷ 12; every two weeks × 26 ÷ 12; twice monthly × 2. Gross and net are compared only on matching bases.</p>"))
            .Append(Panel("What the figures do not say", "<p>Document review does not authenticate a paystub. This view does not predict recovery, decide credit eligibility or determine an affordable payment.</p><p>All current preview accounts, evidence and outcomes are fictional.</p>")).Append("</div>");
        return b.ToString();
    }

    static string ReadinessPage(CollectView v) => Heading("A limited preview", "Clear about where we are.", "A functional place to explore the journey, with fictional data and explicit boundaries before real financial activity.") +
        "<div class=\"grid two\">" + Panel("Available in the fictional preview", "<ul class=\"feature-list\"><li>Independent debtor profile and creditor discovery</li><li>Optional, explicit profile sharing with an inquiry</li><li>Client office setup and separate staff roles</li><li>Case itemization and questions without income upload</li><li>Generated sample documents and recorded human review</li><li>Versioned proposals and printable agreements</li><li>Simulated creditor-bound payment events</li><li>Reported-versus-reviewed income comparison</li></ul>") +
        Panel("Required before real intake opens", "<ul class=\"feature-list pending\"><li>Named pilot client, creditor authority, debt type and state eligibility</li><li>Reviewed notices, dispute handling, retention and communication procedures</li><li>Production malware scanning and controlled real-document upload</li><li>A documented process for the creditor’s human evaluation of submitted documents</li><li>Staff MFA, operational security controls and incident response</li><li>Eligible payment provider, creditor onboarding and verified event integration</li><li>Operational backup recovery and launch review</li></ul>") + "</div><p class=\"small muted\">Based in Clearwater, Florida, with nationwide US availability intended. The preview does not establish operational eligibility in any state.</p>" +
        "<div class=\"notice\"><strong>No real documents or money.</strong><p>Use fictional information throughout the preview. Registration alone does not activate a financial workspace. Payments are simulated; no card or bank details are requested. The preview does not certify documents, authorize collection activity or guarantee regulatory compliance.</p></div>";

    public static string Agreement(CollectView view, CollectCase item)
    {
        if (item.Agreement == null) return Error("An agreement has not been recorded for this account.", 404);
        var a = item.Agreement;
        var body = Heading(a.Synthetic ? "Fictional preview · not a real debt agreement" : "Recorded agreement", "Agreement record", $"Account {item.Reference} · {a.Id}") +
            Panel("Parties & terms", $"<dl class=\"facts\"><div><dt>Customer</dt><dd>{H(item.DebtorLabel)}</dd></div><div><dt>Creditor / payee</dt><dd>{H(a.CreditorName)}</dd></div><div><dt>Negotiating office</dt><dd>{H(a.ClientName)}</dd></div><div><dt>Total agreed</dt><dd>{H(Money(a.TotalCents))} USD</dd></div><div><dt>Accepted at</dt><dd>{H(a.AcceptedAt.ToString("u"))}</dd></div><div><dt>Offer record</dt><dd>{H(a.OfferId)}</dd></div></dl><h3>Conditions</h3><p class=\"preserve-lines\">{H(string.IsNullOrWhiteSpace(a.Terms) ? "No additional conditions supplied." : a.Terms)}</p>{ScheduleTable(a.Schedule)}<p>This record preserves the accepted offer. Later financial-profile or document updates do not change these terms.</p><p class=\"small muted\">Use your browser’s print command to save or print this record. Live payments are not available in the preview.</p>") + Link("/collect/case/" + item.Id, "Return to account", "secondary print-hide");
        return Shell("Agreement record", body, view, "agreement");
    }

    public static string Auth(string page, string csrf, string returnTo, string? notice)
    {
        var kind = page switch { "verify-email" => "verify", "forgot-password" => "forgot", "reset-password" => "reset", "signup" or "verify" or "forgot" or "reset" => page, _ => "signin" };
        var title = kind switch { "signup" => "A first step, on your terms.", "verify" => "Check your email.", "forgot" => "Let’s get you back in.", "reset" => "Choose a new password.", _ => "Welcome back." };
        var intro = kind switch { "signup" => "Create your own account. You do not need a creditor invitation to join Collect.", "verify" => "Enter the verification code sent to your email address.", "forgot" => "We’ll send instructions if an account is available for this email.", "reset" => "Use the code from your email to reset your password.", _ => "Sign in to your Collect account." };
        var b = new StringBuilder("<div class=\"auth-layout\"><div class=\"auth-story\"><p class=\"eyebrow\">Your next chapter</p><h2>A little clarity.<br>A way forward.</h2><p>One place to understand your financial picture and find the right conversation.</p><div class=\"auth-story-note\"><span class=\"privacy-mark\" aria-hidden=\"true\">◇</span><p>Your profile is private until you choose to share it with a specific office.</p></div></div><section class=\"auth-card\">" + Heading("Collect account", title, intro));
        if (!string.IsNullOrEmpty(notice)) b.Append("<div class=\"notice\" role=\"status\">").Append(H(AuthNotice(notice))).Append("</div>");
        b.Append("<form method=\"post\" action=\"/auth/session/").Append(kind).Append("\">").Append(Hidden("__RequestVerificationToken", csrf)).Append(Hidden("entry", "")).Append(Hidden("return_to", string.IsNullOrWhiteSpace(returnTo) ? "/collect/account" : returnTo));
        b.Append(Input("email", "Email address", "", "email", "required maxlength=\"254\" autocomplete=\"email\""));
        if (kind is "verify" or "reset") b.Append(Input("code", "Email code", "", "text", "required maxlength=\"128\" autocomplete=\"one-time-code\""));
        if (kind is "signin" or "signup" or "reset") b.Append(Input("password", kind == "reset" ? "New password" : "Password", "", "password", "required maxlength=\"256\" autocomplete=\"" + (kind == "signin" ? "current-password" : "new-password") + "\""));
        if (kind is "signup" or "reset") b.Append(Input("confirm_password", "Confirm password", "", "password", "required maxlength=\"256\" autocomplete=\"new-password\""));
        b.Append(Button(kind switch { "signup" => "Create account", "verify" => "Verify email", "forgot" => "Send reset instructions", "reset" => "Reset password", _ => "Sign in" }, "full")).Append("</form>");
        if (kind == "signin") b.Append("<div class=\"auth-links\"><a href=\"/collect/forgot-password\">Forgot password?</a><span>New here? <a href=\"/collect/signup\">Create an account</a></span></div>");
        else b.Append("<p class=\"small\">Already have an account? <a href=\"/collect/signin\">Sign in</a></p>");
        if (kind == "verify") b.Append("<details><summary>Need a new code?</summary><form method=\"post\" action=\"/auth/session/resend\">").Append(Hidden("__RequestVerificationToken", csrf)).Append(Hidden("entry", "")).Append(Hidden("return_to", "/collect/account")).Append(Input("email", "Email address", "", "email", "required autocomplete=\"email\"")).Append(Button("Resend code", "secondary")).Append("</form></details>");
        b.Append("<p class=\"small muted auth-footnote\">Real financial intake and payments are not open. You can explore a separate fictional preview while launch preparation continues.</p></section></div>");
        return Shell(title, b.ToString(), null, "auth");
    }
    static string AuthNotice(string notice) => notice switch
    {
        "check-email" => "Check your email for the next step. If an account can be created, you’ll receive a verification code.",
        "reset-email" => "If an account is available for that email address, password reset instructions have been sent.",
        "email-verified" or "verified" => "Your email has been verified. You can sign in.",
        "password-reset" or "reset-complete" => "Your password has been updated. Sign in with your new password.",
        "signed-out" => "You’re signed out.",
        "local-signout" => "You’re signed out on this device. Server-side signout could not be confirmed.",
        "password-mismatch" => "The passwords don’t match. Enter the same new password in both fields.",
        "invalid" => "We couldn’t complete that request. Check your details and try again.",
        "invalid-credentials" => "We couldn’t sign you in with those details. Check them and try again.",
        "verification-required" => "Verify your email address before signing in.",
        "rate-limited" => "Please wait a little before trying again.",
        "unavailable" => "Account services are temporarily unavailable. Please try again shortly.",
        _ => "Please review your details and try again. If you requested an email, check your inbox."
    };
    public static string Welcome(string csrf, string email) => Shell("Your Collect account", Heading("Free debtor account", "You’ve taken the first step.", "You can join Collect independently. Your account is ready while we prepare participating creditor offices and the live financial workspace.") +
        $"<div class=\"notice\"><strong>Signed in as {H(email)}</strong><p>Your account does not yet have a live financial workspace. Do not submit real debts, documents or payment details in the fictional preview.</p></div><div class=\"grid two\">" +
        Panel("Find your creditor", "<p>Explore the sample directory. A search does not share your identity or any financial profile with an office.</p>" + Link("/collect/directory", "Browse fictional offices", "secondary")) +
        Panel("Try the journey", "<p>See private profiles, optional sharing, creditor document review and proposals in a separate fictional session.</p>" + PreviewForm(csrf, "debtor", "Explore the debtor preview")) + "</div>" +
        "<form method=\"post\" action=\"/auth/session/signout\">" + Hidden("__RequestVerificationToken", csrf) + Hidden("entry", "") + Hidden("return_to", "/collect/account") + Button("Sign out", "secondary") + "</form>", null, "welcome");
    public static string Error(string message, int status) => Shell("Unable to continue", "<div class=\"error-page\">" + Heading("Collect · " + status, "Let’s take a step back.", message) + "<div class=\"actions\">" + Link("/collect/account", "Return to your workspace") + Link("/", "Go to Collect home", "secondary") + "</div></div>", null, "error");

    static string Shell(string title, string body, CollectView? view, string section)
    {
        var staff = view != null && CollectRules.Staff(view.Client, view.Actor);
        var nav = new StringBuilder();
        if (view != null && view.Actor.Preview)
        {
            foreach (var item in staff ? new[] { ("overview", "/collect/account", "Workspace"), ("client", "/collect/client", "Office"), ("inquiries", "/collect/inquiries", "Inquiries"), ("analytics", "/collect/analytics", "Comparison") } : new[] { ("overview", "/collect/account", "Workspace"), ("profile", "/collect/profile", "My profile"), ("directory", "/collect/directory", "Find a creditor"), ("inquiries", "/collect/inquiries", "Inquiries") })
                nav.Append("<a href=\"").Append(item.Item2).Append("\"").Append(item.Item1 == section ? " aria-current=\"page\"" : "").Append('>').Append(item.Item3).Append("</a>");
        }
        else nav.Append("<a href=\"/collect/directory\"" + (section == "directory" ? " aria-current=\"page\"" : "") + ">Find a creditor</a><a href=\"/client\">For creditors</a>");
        var b = new StringBuilder("<!doctype html><html lang=\"en\"><head><meta charset=\"utf-8\"><meta name=\"viewport\" content=\"width=device-width, initial-scale=1\"><meta name=\"color-scheme\" content=\"light\"><meta name=\"description\" content=\"Collect brings a private financial profile, creditor discovery and thoughtful negotiation into one place. Explore the fictional preview.\"><meta name=\"robots\" content=\"noindex,nofollow\"><title>" + H(title) + " · Collect</title><link rel=\"stylesheet\" href=\"/collect.css\"><link rel=\"icon\" href=\"/collect-mark.svg\" type=\"image/svg+xml\"></head><body class=\"page-" + H(section) + "\"><a class=\"skip-link\" href=\"#main\">Skip to content</a><header class=\"site-header\"><div class=\"header-inner\"><a class=\"brand\" href=\"/\" aria-label=\"Collect home\"><img src=\"/collect-mark.svg\" width=\"31\" height=\"31\" alt=\"\"><span>collect<span class=\"brand-dot\">.</span></span></a><nav class=\"main-nav\" aria-label=\"Main navigation\">" + nav + "</nav><div class=\"header-actions\">");
        if (view?.Actor.Preview == true) b.Append("<a class=\"status-link\" href=\"/collect/readiness\"><span class=\"tiny-dot\"></span> Preview</a>");
        else if (section == "welcome") b.Append(Link("/collect/account", "Your account", "small-button secondary"));
        else b.Append("<a class=\"signin-link\" href=\"/collect/signin\">Sign in</a>").Append(Link("/collect/signup", "Join Collect", "small-button"));
        b.Append("</div></div></header>");
        if (view?.Actor.Preview == true)
        {
            var role = CollectRules.Role(view.Client, view.Actor) ?? "debtor";
            b.Append("<div class=\"preview-bar\"><div><strong>Fictional preview</strong><span> · no real debts, documents or payments</span></div>").Append(StartForm(view, "switch")).Append("<label><span>Explore as</span><select name=\"role\" aria-label=\"Preview role\">");
            foreach (var r in new[] { ("debtor", "Debtor"), ("admin", "Office admin"), ("reviewer", "Document reviewer"), ("negotiator", "Negotiator") }) b.Append("<option value=\"").Append(r.Item1).Append('"').Append(role == r.Item1 ? " selected" : "").Append('>').Append(r.Item2).Append("</option>");
            b.Append("</select></label><button type=\"submit\">Switch</button></form></div>");
        }
        b.Append("<main id=\"main\" class=\"container\">");
        if (!string.IsNullOrEmpty(view?.Notice)) b.Append("<div class=\"notice status-notice\" role=\"status\">").Append(H(view.Notice)).Append("</div>");
        b.Append(body).Append("</main><footer class=\"site-footer\"><div><a class=\"brand footer-brand\" href=\"/\"><img src=\"/collect-mark.svg\" width=\"24\" height=\"24\" alt=\"\"><span>collect.</span></a><p>A clearer way forward, together.</p></div><div class=\"footer-links\"><a href=\"/collect/directory\">Creditor directory</a><a href=\"/client\">For creditors</a><a href=\"/collect/readiness\">Preview &amp; privacy boundaries</a></div><p class=\"footer-small\">Collect by Tide Casa · Bangel Debt Analytics · Clearwater, Florida<br>Free for debtors. Fictional preview. Real financial intake and live payments are closed.</p></footer>");
        if (section == "home") b.Append("<script src=\"/collect-calculator.js\" defer></script>");
        b.Append("</body></html>");
        return b.ToString();
    }
}
