using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading.RateLimiting;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Http.Features;
using TideCasa.Blazor.Features.Authentication;
using TideCasa.Contracts;

namespace TideCasa.Blazor.Features.Collect;

public sealed record CollectSession(string Workspace, string Role, DateTimeOffset Expires);

public sealed class CollectApplication(CollectRepository repository, IDataProtectionProvider protection, IWebHostEnvironment environment) : IDisposable
{
    public const string Host = "collect.tide.casa";
    private readonly IDataProtector sessions = protection.CreateProtector("TideCasa.Collect.PreviewSessions.v1");
    private readonly PartitionedRateLimiter<string> limiter = PartitionedRateLimiter.Create<string, string>(key =>
        RateLimitPartition.GetFixedWindowLimiter(key, _ => new FixedWindowRateLimiterOptions { PermitLimit = key.EndsWith(":start") ? 5 : 80, Window = TimeSpan.FromMinutes(5), QueueLimit = 0, AutoReplenishment = true }));
    private string CookieName => environment.IsDevelopment() ? "Tide.Collect.Preview" : "__Host-Tide.Collect.Preview";
    public static bool IsHost(string host) => string.Equals(host, Host, StringComparison.OrdinalIgnoreCase);
    public static bool IsPath(string? path) => path == "/collect" || path?.StartsWith("/collect/", StringComparison.Ordinal) == true;
    public static bool IsEntry(string host, string returnTo) => IsHost(host) || IsPath(returnTo);
    public static string AuthPage(string page) => "/collect" + page;
    private CollectSession? Session(HttpContext context)
    {
        if (!context.Request.Cookies.TryGetValue(CookieName, out var value) || value.Length > 2000) return null;
        try
        {
            var session = JsonSerializer.Deserialize<CollectSession>(sessions.Unprotect(value));
            return session?.Expires > DateTimeOffset.UtcNow && session.Role is "admin" or "reviewer" or "negotiator" or "debtor" ? session : null;
        }
        catch (Exception error) when (error is CryptographicException or JsonException) { return null; }
    }
    private void SetSession(HttpContext context, CollectSession session) => context.Response.Cookies.Append(CookieName, sessions.Protect(JsonSerializer.Serialize(session)),
        new CookieOptions { HttpOnly = true, Secure = !environment.IsDevelopment(), SameSite = SameSiteMode.Strict, Path = "/", Expires = session.Expires, IsEssential = true });
    private async Task<CollectView?> View(HttpContext context)
    {
        var session = Session(context); if (session == null) return null;
        var client = await repository.Read(session.Workspace, DateTimeOffset.UtcNow, context.RequestAborted); if (client == null) return null;
        var actor = new CollectActor(client.Id + ":" + session.Role, session.Role + "@example.invalid", true);
        return new(client, actor, client.Personal, Csrf(context), context.Request.Path);
    }
    private static string Csrf(HttpContext context) => context.RequestServices.GetRequiredService<IAntiforgery>().GetAndStoreTokens(context).RequestToken!;
    private static async Task Html(HttpContext context, string html, int status = 200)
    {
        context.Response.StatusCode = status; context.Response.ContentType = "text/html; charset=utf-8"; await context.Response.WriteAsync(html, context.RequestAborted);
    }
    private static bool SameOrigin(HttpRequest request)
    {
        var value = request.Headers.Origin.ToString();
        if (string.IsNullOrEmpty(value)) value = request.Headers.Referer.ToString();
        return Uri.TryCreate(value, UriKind.Absolute, out var uri) && string.IsNullOrEmpty(uri.UserInfo)
            && uri.Scheme == request.Scheme && uri.Authority.Equals(request.Host.Value, StringComparison.OrdinalIgnoreCase)
            && request.Headers["Sec-Fetch-Site"] != "cross-site";
    }
    public async Task Handle(HttpContext context, RequestDelegate next)
    {
        var path = context.Request.Path.Value ?? "/";
        var host = IsHost(context.Request.Host.Host);
        var localAlias = environment.IsDevelopment() && (path == "/client" || path.StartsWith("/c/", StringComparison.Ordinal));
        if (!host && !IsPath(path) && !localAlias) { await next(context); return; }
        // Collect is a distinct public host. Restaurant identities/permissions do not create Collect membership.
        if (!host && !environment.IsDevelopment()) { context.Response.StatusCode = 404; return; }
        context.Response.Headers.CacheControl = "no-store, private";
        context.Response.Headers.Pragma = "no-cache";
        context.Response.Headers.XContentTypeOptions = "nosniff";
        context.Response.Headers["Referrer-Policy"] = "same-origin";
        context.Response.Headers["X-Frame-Options"] = "DENY";
        context.Response.Headers["Content-Security-Policy"] = "default-src 'none'; script-src 'self'; style-src 'self'; img-src 'self' data:; font-src 'self'; form-action 'self'; base-uri 'none'; frame-ancestors 'none'";
        context.Response.Headers["Permissions-Policy"] = "camera=(), microphone=(), geolocation=()";
        context.Response.Headers["X-Robots-Tag"] = "noindex, nofollow";
        if (path.StartsWith("/auth/session/", StringComparison.Ordinal) || path == "/health") { await next(context); return; }
        if (path == "/robots.txt") { context.Response.ContentType = "text/plain"; await context.Response.WriteAsync("User-agent: *\nDisallow: /\n"); return; }
        if (path == "/client") { context.Response.Redirect("/collect/client"); return; }
        try
        {
            if (HttpMethods.IsPost(context.Request.Method)) { await Post(context, path); return; }
            if (!HttpMethods.IsGet(context.Request.Method)) throw new CollectFault(405, "This page does not accept that method.");
            if (path is "/" or "/collect" or "/collect/") { await Html(context, CollectPages.Home(Csrf(context))); return; }
            var auth = path.StartsWith("/collect/", StringComparison.Ordinal) ? path[9..] : path.TrimStart('/');
            if (auth is "signin" or "signup" or "verify-email" or "forgot-password" or "reset-password")
            {
                await Html(context, CollectPages.Auth(auth, Csrf(context), "/collect/account", context.Request.Query["notice"])); return;
            }
            var view = await View(context);
            if (path == "/collect/account" && view == null && context.Items[AuthFlow.AccountItem] is AccountOverview account)
            {
                await Html(context, CollectPages.Welcome(Csrf(context), account.User.Email)); return;
            }
            if (path is "/collect/directory" or "/collect/client" or "/collect/readiness" || path.StartsWith("/c/", StringComparison.Ordinal))
            {
                // Public directory entries are fictional organization data only; never search accounts or debtors.
                view ??= new(CollectRules.Fixture("00000000000000000000000000000000", DateTimeOffset.UtcNow), new("anonymous", "", false), new(), Csrf(context), path);
            }
            if (view == null) { context.Response.Redirect("/collect/signin"); return; }
            var section = path switch
            {
                "/collect/account" => "overview", "/collect/profile" => "profile", "/collect/directory" => "directory", "/collect/client" => "client",
                "/collect/inquiries" => "inquiries", "/collect/analytics" => "analytics", "/collect/readiness" => "readiness", _ => ""
            };
            if (path.StartsWith("/c/", StringComparison.Ordinal))
            {
                var slug = path[3..]; CollectRules.Require(CollectRules.Directory.Any(x => x.Slug == slug) || slug == view.Client.Slug, "Office unavailable.", 404);
                section = "office"; view = view with { Path = slug };
            }
            if (path.StartsWith("/collect/case/", StringComparison.Ordinal))
            {
                var id = path[14..]; _ = CollectRules.Case(view.Client, view.Actor, id); section = "case"; view = view with { Path = id };
            }
            if (path.StartsWith("/collect/agreement/", StringComparison.Ordinal))
            {
                var item = CollectRules.Case(view.Client, view.Actor, path[19..]); CollectRules.Require(item.Agreement != null, "Agreement unavailable.", 404);
                await Html(context, CollectPages.Agreement(view, item)); return;
            }
            if (path.StartsWith("/collect/document/", StringComparison.Ordinal))
            {
                var parts = path[18..].Split('/'); CollectRules.Require(parts.Length == 2, "Document unavailable.", 404);
                var item = CollectRules.Case(view.Client, view.Actor, parts[0]);
                CollectRules.Require(item.DebtorId == view.Actor.Id || CollectRules.Role(view.Client, view.Actor) is "admin" or "reviewer", "Document unavailable.", 404);
                var document = item.Evidence.SingleOrDefault(x => x.Id == parts[1]);
                CollectRules.Require(document is { Synthetic: true, Scan: "clean" }, "Document unavailable.", 404);
                using var accessLease = await limiter.AcquireAsync((context.Connection.RemoteIpAddress?.ToString() ?? "unknown") + ":document", 1, context.RequestAborted);
                CollectRules.Require(accessLease.IsAcquired, "Please wait before downloading more samples.", 429);
                await repository.RecordAccess(view.Client.Id, new(Guid.NewGuid().ToString("N"), DateTimeOffset.UtcNow, view.Actor.Id, "evidence.sample_viewed:" + document!.Id, item.Id), context.RequestAborted);
                context.Response.ContentType = "text/plain; charset=utf-8"; context.Response.Headers.ContentDisposition = "attachment; filename=fictional-sample.txt";
                await context.Response.WriteAsync(SampleDocument(document!), context.RequestAborted); return;
            }
            CollectRules.Require(section != "", "Page unavailable.", 404);
            if (section == "analytics" || section == "inquiries" && view.Actor.Id != view.Personal.OwnerId) CollectRules.Demand(view.Client, view.Actor, "admin", "reviewer", "negotiator");
            if (section == "profile") CollectRules.Require(view.Actor.Id == view.Personal.OwnerId, "Profile unavailable.", 404);
            if (section == "directory") view = view with { Path = context.Request.Query["q"].ToString()[..Math.Min(100, context.Request.Query["q"].ToString().Length)] };
            await Html(context, CollectPages.Workspace(view, section));
        }
        catch (CollectFault fault) { await Html(context, CollectPages.Error(fault.Message, fault.Status), fault.Status); }
        catch (Exception error) when (error is AntiforgeryValidationException or InvalidDataException or BadHttpRequestException or FormatException or OverflowException)
        { await Html(context, CollectPages.Error("The form expired or contains invalid details. Reload the page and try again.", 400), 400); }
        catch (Exception error) when (error is Npgsql.NpgsqlException or IOException or CryptographicException)
        {
            context.RequestServices.GetRequiredService<ILogger<CollectApplication>>().LogWarning("Collect storage unavailable: {Type}; SQL state {SqlState}", error.GetType().Name, error is Npgsql.PostgresException postgres ? postgres.SqlState : "unavailable");
            await Html(context, CollectPages.Error("The workspace is temporarily unavailable. Please try again shortly.", 503), 503);
        }
    }
    private async Task Post(HttpContext context, string path)
    {
        CollectRules.Require(path == "/collect/start" || path.StartsWith("/collect/action/", StringComparison.Ordinal), "Page unavailable.", 404);
        CollectRules.Require(SameOrigin(context.Request) && context.Request.HasFormContentType, "Open this form on Collect and try again.");
        using var lease = await limiter.AcquireAsync((context.Connection.RemoteIpAddress?.ToString() ?? "unknown") + (path == "/collect/start" ? ":start" : ":action"), 1, context.RequestAborted);
        CollectRules.Require(lease.IsAcquired, "Please wait a few minutes before trying again.", 429);
        if (context.Features.Get<IHttpMaxRequestBodySizeFeature>() is { IsReadOnly: false } limit) limit.MaxRequestBodySize = 32768;
        await context.RequestServices.GetRequiredService<IAntiforgery>().ValidateRequestAsync(context);
        var form = await context.Request.ReadFormAsync(context.RequestAborted);
        CollectRules.Require(form.Count <= 20 && form.Files.Count == 0 && form.All(x => x.Value.Count == 1) && form.Sum(x => x.Value.ToString().Length) < 24000,
            "Real file uploads are not open in this preview. Check the form details.");
        string Value(string key, int max = 1200) { var value = form[key].ToString().Trim(); CollectRules.Require(value.Length <= max, "A field exceeds its size limit."); return value; }
        var now = DateTimeOffset.UtcNow;
        if (path == "/collect/start")
        {
            var role = Value("role"); CollectRules.Require(role is "debtor" or "admin", "Choose a preview entrance.");
            var old = Session(context); if (old != null) await repository.Delete(old.Workspace, context.RequestAborted);
            var fixture = CollectRules.Fixture(Guid.NewGuid().ToString("N"), now); await repository.Create(fixture, now, context.RequestAborted);
            SetSession(context, new(fixture.Id, role, fixture.Expires!.Value)); context.Response.Redirect(role == "debtor" ? "/collect/profile" : "/collect/account"); return;
        }
        var view = await View(context); CollectRules.Require(view != null, "Your fictional workspace expired. Start a new preview.", 401);
        var client = view!.Client; var actor = view.Actor;
        CollectRules.Require(int.TryParse(Value("revision"), out var revision) && revision == client.Revision, "This page has changed. Reload it before submitting.", 409);
        var action = path[16..]; var destination = "/collect/account";
        var caseId = Value("case_id", 32);
        var item = caseId.Length > 0 ? CollectRules.Case(client, actor, caseId) : null;
        CollectCase Case() { CollectRules.Require(item != null, "Account unavailable.", 404); return item!; }
        switch (action)
        {
            case "switch":
                var role = Value("role"); CollectRules.Require(role is "admin" or "reviewer" or "negotiator" or "debtor", "Choose a preview role.");
                SetSession(context, new(client.Id, role, client.Expires!.Value)); context.Response.Redirect("/collect/account"); return;
            case "reset":
                await repository.Delete(client.Id, context.RequestAborted); context.Response.Cookies.Delete(CookieName, new CookieOptions { Path = "/", Secure = !environment.IsDevelopment() }); context.Response.Redirect("/collect"); return;
            case "brand":
                CollectRules.Demand(client, actor, "admin");
                var name = Value("name", 80); var slug = Value("slug", 60); var accent = Value("accent", 7); var kind = Value("kind", 30);
                CollectRules.Require(name.Length > 1 && Regex.IsMatch(slug, "^[a-z0-9]+(?:-[a-z0-9]+)*$") && Regex.IsMatch(accent, "^#[a-fA-F0-9]{6}$") && kind is "collection-agency" or "original-creditor", "Check the office name, address, color and business type.");
                client.Name = name; client.Slug = slug; client.Accent = accent; client.Kind = kind; CollectRules.Audit(client, actor, "branding.updated", now); destination = "/collect/client"; break;
            case "activate":
                CollectRules.Demand(client, actor, "admin"); client.State = "active"; CollectRules.Audit(client, actor, "preview.activated", now); destination = "/collect/client"; break;
            case "import": CollectRules.Import(client, actor, Value("csv", 16384), now); destination = "/collect/client"; break;
            case "invite":
                var invitation = CollectRules.Invite(client, actor, Value("email", 254), Value("role", 30), now, item?.Id);
                // No email is sent. Preview invitation tokens are displayed once, never logged.
                await repository.Save(client, revision, now, context.RequestAborted);
                await Html(context, CollectPages.Error("Fictional invitation created. One-time code: " + invitation + ". No email was sent. The matching verified account is required to claim it.", 200)); return;
            case "profile":
                var income = CollectRules.Money(Value("income")); var expenses = new[] { "housing", "utilities", "food", "transport", "health", "other" }.Select(k => new CollectExpense(k, CollectRules.Money(Value(k) is "" ? "0" : Value(k)))).ToList();
                var profile = new CollectProfile(income, Value("period", 20), Value("basis", 10), expenses, now);
                if (Value("target") == "personal")
                {
                    CollectRules.Require(actor.Id == client.Personal.OwnerId, "Profile unavailable.", 404);
                    // Use identical field rules, without disclosing the independent profile to a case.
                    CollectRules.ValidateProfile(profile); CollectRules.Audit(client, actor, "personal_profile.updated", now);
                    client.Personal.Profile = profile; destination = "/collect/profile";
                }
                else { CollectRules.SetProfile(client, actor, Case(), profile, now); destination = "/collect/case/" + item!.Id; }
                break;
            case "sample-evidence":
                var account = Case(); var evidenceKind = Value("kind", 20); var evidenceId = Guid.NewGuid().ToString("N");
                var evidence = new CollectEvidence(evidenceId, evidenceKind, "Fictional " + evidenceKind, "text/plain", 1, "", "clean", "uploaded", evidenceKind == "paystub" ? 175000 : null,
                    "biweekly", "net", DateOnly.FromDateTime(now.AddDays(-14).UtcDateTime), DateOnly.FromDateTime(now.AddDays(-1).UtcDateTime), now, Synthetic: true);
                var content = SampleDocument(evidence);
                CollectRules.AddEvidence(client, actor, account, evidence with { Bytes = Encoding.UTF8.GetByteCount(content), Sha256 = CollectRules.Hash(content) }, now); destination = "/collect/case/" + account.Id; break;
            case "review": CollectRules.Review(client, actor, Case(), Value("evidence_id", 32), Value("status", 30), now); destination = "/collect/case/" + item!.Id; break;
            case "offer":
                CollectRules.Negotiate(client, actor, Case(), CollectRules.Money(Value("amount")), int.Parse(Value("installments"), CultureInfo.InvariantCulture), DateOnly.ParseExact(Value("first_date"), "yyyy-MM-dd", CultureInfo.InvariantCulture), Value("terms"), now); destination = "/collect/case/" + item!.Id; break;
            case "accept": CollectRules.Require(Value("consent") == "yes", "Review and confirm the exact agreement terms."); CollectRules.Accept(client, actor, Case(), Value("offer_id", 32), now); destination = "/collect/case/" + item!.Id; break;
            case "withdraw": CollectRules.Withdraw(client, actor, Case(), Value("offer_id", 32), now); destination = "/collect/case/" + item!.Id; break;
            case "dispute":
                var disputed = Case(); CollectRules.Debtor(client, actor, disputed); var category = Value("category", 30);
                CollectRules.Require(category is "dispute" or "original-creditor" or "support", "Choose a request type.");
                if (category == "support") disputed.SupportRequested = true;
                else { CollectRules.Require(disputed.Disputes.Count < 10 && !disputed.Disputes.Any(x => x.State == "open" && x.Category == category), "This request is already open.", 409); disputed.Disputes.Add(new(Guid.NewGuid().ToString("N"), category, now)); }
                CollectRules.Audit(client, actor, "account." + category, now, disputed.Id); destination = "/collect/case/" + disputed.Id; break;
            case "resolve":
                CollectRules.Demand(client, actor, "admin"); var resolved = Case(); var index = resolved.Disputes.FindIndex(x => x.Id == Value("dispute_id", 32));
                CollectRules.Require(index >= 0, "Request unavailable.", 404); resolved.Disputes[index] = resolved.Disputes[index] with { State = "resolved-in-simulation" };
                CollectRules.Audit(client, actor, "dispute.simulation.resolved", now, resolved.Id); destination = "/collect/case/" + resolved.Id; break;
            case "preference":
                var preferred = Case(); CollectRules.Debtor(client, actor, preferred); preferred.EmailOptOut = Value("opt_out") == "true"; CollectRules.Audit(client, actor, "communications.updated", now, preferred.Id); destination = "/collect/case/" + preferred.Id; break;
            case "inquire":
                CollectRules.Require(Value("consent") == "yes", "Confirm the selected creditor and what you want to share.");
                CollectRules.Inquire(client, actor, Value("creditor_id", 100), Value("share_profile") == "yes", now); destination = "/collect/profile"; break;
            case "payment": CollectRules.BeginPayment(client, actor, Case(), int.Parse(Value("installment"), CultureInfo.InvariantCulture), now); destination = "/collect/case/" + item!.Id; break;
            case "payment-event":
                var paymentCase = Case(); var payment = paymentCase.Payments.SingleOrDefault(x => x.Id == Value("payment_id", 32)); CollectRules.Require(payment != null, "Payment unavailable.", 404);
                CollectRules.SimulateEvent(client, actor, paymentCase, payment!.Id, Guid.NewGuid().ToString("N"), payment.LastSequence + 1, Value("state", 20), now); destination = "/collect/case/" + paymentCase.Id; break;
            default: throw new CollectFault(404, "Action unavailable.");
        }
        await repository.Save(client, revision, now, context.RequestAborted); context.Response.Redirect(destination);
    }
    private static string SampleDocument(CollectEvidence evidence) => "FICTIONAL SAMPLE — NOT A FINANCIAL DOCUMENT\n\nKind: " + evidence.Kind
        + "\nNet income per two-week period: " + (evidence.IncomeCents is { } amount ? (amount / 100m).ToString("F2", CultureInfo.InvariantCulture) + " USD" : "Not applicable")
        + "\nPeriod: " + evidence.PeriodStart.ToString("yyyy-MM-dd") + " to " + evidence.PeriodEnd.ToString("yyyy-MM-dd")
        + "\nSource: Built-in Collect preview fixture. No independent source verification or authenticity assertion.\n";
    public void Dispose() => limiter.Dispose();
}
