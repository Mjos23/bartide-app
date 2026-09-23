using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Components.Authorization;
using TideCasa.Blazor.Features.Authentication;
using TideCasa.Blazor.Features.Ordering;
using TideCasa.Blazor.Features.RestaurantManagement;
using TideCasa.Blazor.Features.StaffTraining;
using TideCasa.Blazor.Features.Media;
using TideCasa.Blazor.Features.Rewards;
using TideCasa.Blazor.Features.Events;
using TideCasa.Blazor.Features.DemoInbox;
using TideCasa.Blazor.Features.WorkspaceRegistration;
using TideCasa.Blazor.Features.ServiceBilling;
using TideCasa.Blazor.Features.MerchantPayments;
using TideCasa.Blazor.Features.Referrals;
using TideCasa.Blazor.Features.LaunchReview;
using TideCasa.Blazor.Features.BusinessPosts;
using TideCasa.Blazor.Features.SalesPipeline;
using TideCasa.Blazor.Features.EmailTracking;
using TideCasa.Blazor.Components;
using TideCasa.Blazor.Services;
using System.Net;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.DataProtection.KeyManagement;
using Microsoft.AspNetCore.HttpOverrides;
using TideCasa.Hosting;

var builder = WebApplication.CreateBuilder(args);
builder.Services.AddRazorComponents().AddInteractiveServerComponents();
var protection = builder.Services.AddDataProtection().SetApplicationName("TideCasa.Blazor");
if (builder.Configuration["DataProtection:Provider"] == "PostgreSql")
{
    builder.Services.AddSingleton<PostgresKeyRepository>();
    builder.Services.AddOptions<KeyManagementOptions>().Configure<PostgresKeyRepository>((options, repository) => options.XmlRepository = repository);
}
else
{
    if (builder.Configuration["ReverseProxy:Provider"] == "DigitalOceanAppPlatform")
        throw new InvalidOperationException("App Platform requires durable PostgreSQL data-protection storage.");
    var keysPath = Path.GetFullPath(builder.Configuration["DataProtection:KeysPath"] ?? "App_Data/keys", builder.Environment.ContentRootPath);
    Directory.CreateDirectory(keysPath);
    protection.PersistKeysToFileSystem(new DirectoryInfo(keysPath));
}
builder.Services.Configure<ForwardedHeadersOptions>(options =>
{
    options.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto;
    options.ForwardLimit = 1;
    if (IPAddress.TryParse(builder.Configuration["ReverseProxy:KnownProxy"], out var proxy)) options.KnownProxies.Add(proxy);
});
builder.Services.AddHttpContextAccessor();
builder.Services.AddTransient<AppPlatformApiHandler>();
builder.Services.ConfigureHttpClientDefaults(http => http.AddHttpMessageHandler<AppPlatformApiHandler>());
builder.Services.AddCascadingAuthenticationState();
builder.Services.AddAuthorization();
builder.Services.AddSingleton<ServerTicketStore>();
builder.Services.AddScoped<AccountApiClient>();
builder.Services.AddScoped<OrderingRequestContext>();
builder.Services.AddScoped<FreshCookieEvents>();
builder.Services.AddAuthentication(CookieAuthenticationDefaults.AuthenticationScheme).AddCookie(options =>
{
    options.Cookie.Name = builder.Environment.IsDevelopment() ? "TideCasa.Auth" : "__Host-TideCasa.Auth";
    options.Cookie.HttpOnly = true;
    options.Cookie.Path = "/";
    options.Cookie.SameSite = SameSiteMode.Lax;
    options.Cookie.SecurePolicy = builder.Environment.IsDevelopment() ? CookieSecurePolicy.SameAsRequest : CookieSecurePolicy.Always;
    options.LoginPath = "/signin";
    options.ExpireTimeSpan = TimeSpan.FromHours(1);
    options.SlidingExpiration = false;
    options.EventsType = typeof(FreshCookieEvents);
});
builder.Services.AddOptions<CookieAuthenticationOptions>(CookieAuthenticationDefaults.AuthenticationScheme)
    .Configure<ServerTicketStore>((options, store) => options.SessionStore = store);
builder.Services.AddAntiforgery(options =>
{
    options.Cookie.HttpOnly = true;
    options.Cookie.SameSite = SameSiteMode.Strict;
    options.Cookie.SecurePolicy = builder.Environment.IsDevelopment() ? CookieSecurePolicy.SameAsRequest : CookieSecurePolicy.Always;
});
var authApiBase = AccountApiClient.ValidateBaseUrl(builder.Configuration["Api:BaseUrl"] ?? "http://localhost:5100/", builder.Environment.IsDevelopment());
builder.Services.AddHttpClient<TideCasaApiClient>(client =>
{
    client.BaseAddress = authApiBase; client.Timeout = TimeSpan.FromSeconds(20); client.MaxResponseContentBufferSize = 1024 * 1024;
}).ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler { AllowAutoRedirect = false, UseCookies = false }).RemoveAllLoggers();
builder.Services.AddHttpClient(AccountApiClient.ClientName, client =>
{
    client.BaseAddress = authApiBase;
    client.Timeout = TimeSpan.FromSeconds(20);
    client.MaxResponseContentBufferSize = 64 * 1024;
}).ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler { AllowAutoRedirect = false, UseCookies = false });
builder.Services.AddHttpClient<RestaurantOrderingClient>(client =>
{
    client.BaseAddress = authApiBase;
    client.Timeout = TimeSpan.FromSeconds(20);
    client.MaxResponseContentBufferSize = 1024 * 1024;
}).ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler { AllowAutoRedirect = false, UseCookies = false });
builder.Services.AddHttpClient<RestaurantManagementClient>(client =>
{
    client.BaseAddress = authApiBase;
    client.Timeout = TimeSpan.FromSeconds(20);
    client.MaxResponseContentBufferSize = 8 * 1024 * 1024;
}).ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler { AllowAutoRedirect = false, UseCookies = false });
builder.Services.AddHttpClient<StaffTrainingClient>(client =>
{
    client.BaseAddress = authApiBase;
    client.Timeout = TimeSpan.FromSeconds(20);
    client.MaxResponseContentBufferSize = 16 * 1024 * 1024;
}).ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler { AllowAutoRedirect = false, UseCookies = false });
builder.Services.AddHttpClient<MediaClient>(client =>
{
    client.BaseAddress = authApiBase;
    client.Timeout = TimeSpan.FromMinutes(5);
}).ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler { AllowAutoRedirect = false, UseCookies = false }).RemoveAllLoggers();
builder.Services.AddHttpClient<RewardsClient>(client =>
{
    client.BaseAddress = authApiBase; client.Timeout = TimeSpan.FromSeconds(30); client.MaxResponseContentBufferSize = 8 * 1024 * 1024;
}).ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler { AllowAutoRedirect = false, UseCookies = false }).RemoveAllLoggers();
builder.Services.AddHttpClient<EventsClient>(client =>
{
    client.BaseAddress = authApiBase; client.Timeout = TimeSpan.FromSeconds(20); client.MaxResponseContentBufferSize = 8 * 1024 * 1024;
}).ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler { AllowAutoRedirect = false, UseCookies = false }).RemoveAllLoggers();
builder.Services.AddHttpClient<DemoInboxClient>(client =>
{
    client.BaseAddress = authApiBase; client.Timeout = TimeSpan.FromSeconds(20); client.MaxResponseContentBufferSize = 8 * 1024 * 1024;
}).ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler { AllowAutoRedirect = false, UseCookies = false }).RemoveAllLoggers();
builder.Services.AddHttpClient<ServiceBillingClient>(client =>
{
    client.BaseAddress = authApiBase; client.Timeout = TimeSpan.FromSeconds(100); client.MaxResponseContentBufferSize = 8 * 1024 * 1024;
}).ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler { AllowAutoRedirect = false, UseCookies = false }).RemoveAllLoggers();
builder.Services.AddHttpClient<MerchantPaymentsClient>(client =>
{
    client.BaseAddress = authApiBase; client.Timeout = TimeSpan.FromSeconds(100); client.MaxResponseContentBufferSize = 2 * 1024 * 1024;
}).ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler { AllowAutoRedirect = false, UseCookies = false }).RemoveAllLoggers();
builder.Services.AddHttpClient<ReferralsClient>(client =>
{
    client.BaseAddress = authApiBase; client.Timeout = TimeSpan.FromSeconds(20); client.MaxResponseContentBufferSize = 8 * 1024 * 1024;
}).ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler { AllowAutoRedirect = false, UseCookies = false }).RemoveAllLoggers();
builder.Services.AddHttpClient<LaunchReviewClient>(client =>
{
    client.BaseAddress = authApiBase; client.Timeout = TimeSpan.FromSeconds(20); client.MaxResponseContentBufferSize = 8 * 1024 * 1024;
}).ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler { AllowAutoRedirect = false, UseCookies = false }).RemoveAllLoggers();
builder.Services.AddHttpClient<BusinessPostsClient>(client =>
{
    client.BaseAddress = authApiBase; client.Timeout = TimeSpan.FromSeconds(20); client.MaxResponseContentBufferSize = 1024 * 1024;
}).ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler { AllowAutoRedirect = false, UseCookies = false }).RemoveAllLoggers();
builder.Services.AddHttpClient<SalesPipelineClient>(client =>
{
    client.BaseAddress = authApiBase; client.Timeout = TimeSpan.FromSeconds(20); client.MaxResponseContentBufferSize = 2 * 1024 * 1024;
}).ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler { AllowAutoRedirect = false, UseCookies = false }).RemoveAllLoggers();
builder.Services.AddHttpClient<EmailTrackingClient>(client =>
{
    client.BaseAddress = authApiBase; client.Timeout = TimeSpan.FromSeconds(3); client.MaxResponseContentBufferSize = 2 * 1024 * 1024;
}).ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler { AllowAutoRedirect = false, UseCookies = false }).RemoveAllLoggers();
var app = builder.Build();
app.UseAppPlatformIngress(builder.Configuration, api: false);
app.UseForwardedHeaders();
app.UseEmailTracking();
app.UseGuestPurchaseCookie();
if (!app.Environment.IsDevelopment()) app.UseExceptionHandler("/error", createScopeForErrors: true);
app.UsePublicSeo();
app.UseStaticFiles();
app.Use(async (context, next) =>
{
    context.RequestServices.GetRequiredService<OrderingRequestContext>().CaptureConnection(context.Connection.RemoteIpAddress);
    // Account/auth responses must not survive logout in a shared browser cache.
    if (context.Request.Path.StartsWithSegments("/account") || context.Request.Path.StartsWithSegments("/auth")
        || context.Request.Path.StartsWithSegments("/workspace") || context.Request.Path.StartsWithSegments("/ordering")
        || context.Request.Path.StartsWithSegments("/order") || context.Request.Path.StartsWithSegments("/menu")
        || context.Request.Path.StartsWithSegments("/rewards") || context.Request.Path.StartsWithSegments("/events")
        || context.Request.Path.StartsWithSegments("/event-management") || context.Request.Path.StartsWithSegments("/event-reservations")
        || context.Request.Path.StartsWithSegments("/private-media")
        || context.Request.Path.StartsWithSegments("/owner")
        || context.Request.Path.StartsWithSegments("/start")
        || context.Request.Path.StartsWithSegments("/service-billing")
        || context.Request.Path.StartsWithSegments("/merchant-payments")
        || context.Request.Path.StartsWithSegments("/referral-forms")
        || context.Request.Path.StartsWithSegments("/updates")
        || context.Request.Path.StartsWithSegments("/post-management")
        || context.Request.Path.StartsWithSegments("/post-subscriptions")
        || new[] { "/signin", "/signup", "/verify-email", "/forgot-password", "/reset-password", "/signout" }.Contains(context.Request.Path.Value, StringComparer.OrdinalIgnoreCase))
    {
        context.Response.Headers.CacheControl = "no-store";
        context.Response.Headers.Pragma = "no-cache";
        context.Response.Headers["X-Frame-Options"] = "DENY";
        // Native form POSTs under no-referrer carry Origin: null and fail our
        // origin validation. Keep same-site forms usable without external referrers.
        context.Response.Headers["Referrer-Policy"] = "same-origin";
    }
    await next();
});
app.UseAuthentication();
app.UseAuthorization();
app.UseAntiforgery();
app.MapTideCasaAuthentication();
app.MapRestaurantOrdering();
app.MapRestaurantManagement();
app.MapStaffTrainingForms();
app.MapTideCasaMedia();
app.MapRewardForms();
app.MapTideCasaEvents();
app.MapDemoInboxForms();
app.MapWorkspaceRegistration();
app.MapServiceBillingForms();
app.MapTideCasaMerchantPayments();
app.MapReferralForms();
app.MapTideCasaLaunchReview();
app.MapBusinessPostForms();
app.MapSalesPipelineForms();
app.MapEmailTrackingForms();
app.MapGet("/start", (HttpContext context) => Results.LocalRedirect("/start/" + (context.Request.Query["business"] == "general" ? "business" : "restaurant")
    + "?referralCode=" + Uri.EscapeDataString(context.Request.Query["ref"].ToString()[..Math.Min(context.Request.Query["ref"].ToString().Length, 32)])));
app.MapPublicSeo();
app.MapGet("/health", () => Results.Ok(new { status = "ok", service = "tide-casa-web" }));
app.MapRazorComponents<App>().AddInteractiveServerRenderMode();
app.Run();
