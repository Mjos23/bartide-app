using System.Threading.RateLimiting;
using TideCasa.Hosting;
using System.Net;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.RateLimiting;
using TideCasa.Api.Features.DemoRequests;
using TideCasa.Api.Features.Pricing;
using TideCasa.Api.Infrastructure;
using TideCasa.Api.Features.Authentication;
using TideCasa.Api.Features.Accounts;
using TideCasa.Api.Features.RestaurantOrdering;
using TideCasa.Api.Features.StaffTraining;
using TideCasa.Api.Features.Media;
using TideCasa.Api.Features.Rewards;
using TideCasa.Api.Features.Events;
using TideCasa.Api.Features.ServiceBilling;
using TideCasa.Api.Features.MerchantPayments;
using TideCasa.Api.Features.Referrals;
using TideCasa.Api.Features.LaunchReview;
using TideCasa.Api.Features.BusinessPosts;
using TideCasa.Api.Features.SalesPipeline;

var builder = WebApplication.CreateBuilder(args);
builder.WebHost.ConfigureKestrel(options => options.Limits.MaxRequestBodySize = 16 * 1024);
builder.Services.AddProblemDetails();
builder.Services.AddOpenApi();
builder.Services.Configure<ForwardedHeadersOptions>(options =>
{
    options.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto;
    options.ForwardLimit = 1;
    foreach (var name in new[] { "ReverseProxy:KnownProxy", "ReverseProxy:KnownClientProxy" })
        if (IPAddress.TryParse(builder.Configuration[name], out var proxy)) options.KnownProxies.Add(proxy);
});
builder.Services.AddSingleton<ApplicationDatabase>();
builder.Services.AddSingleton<SchemaMigrator>();
builder.Services.AddSingleton<FeatureMigrator>();
builder.Services.AddSingleton<PostgresSchemaMigrator>();
builder.Services.AddScoped<WorkspaceAccessStore>();
builder.Services.AddScoped<WorkspaceRegistrationStore>();
builder.Services.AddTideCasaAuthentication();
builder.Services.AddSingleton<DemoRequestStore>();
builder.Services.AddScoped<DemoRequestService>();
builder.Services.AddHttpClient<DemoNotificationSender>(client => client.Timeout = TimeSpan.FromSeconds(25))
    .ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler { AllowAutoRedirect = false, UseCookies = false }).RemoveAllLoggers();
builder.Services.AddScoped<DemoInboxStore>();
builder.Services.AddHostedService<DemoNotificationWorker>();
builder.Services.AddScoped<RestaurantOrderingStore>();
builder.Services.AddScoped<StaffTrainingStore>();
builder.Services.AddTideCasaMedia(builder.Configuration, builder.Environment);
builder.Services.AddScoped<RewardsStore>();
builder.Services.AddScoped<EventsStore>();
builder.Services.AddTideCasaServiceBilling(builder.Configuration, builder.Environment);
builder.Services.AddTideCasaMerchantPayments();
builder.Services.AddScoped<ReferralStore>();
builder.Services.AddTideCasaLaunchReview();
builder.Services.AddTideCasaBusinessPosts(builder.Configuration);
builder.Services.AddSingleton<SalesPipelineStore>();
builder.Services.AddRateLimiter(options =>
{
    options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
    options.AddPolicy("restaurant-ordering", context => RateLimitPartition.GetFixedWindowLimiter(
        context.Connection.RemoteIpAddress?.ToString() ?? "unknown", _ => new FixedWindowRateLimiterOptions
        { PermitLimit = 120, Window = TimeSpan.FromMinutes(1), QueueLimit = 0, AutoReplenishment = true }));
    options.AddPolicy("public-form", context => RateLimitPartition.GetFixedWindowLimiter(
        context.Connection.RemoteIpAddress?.ToString() ?? "unknown", _ => new FixedWindowRateLimiterOptions
        { PermitLimit = 30, Window = TimeSpan.FromMinutes(1), QueueLimit = 0, AutoReplenishment = true }));
    options.AddPolicy("media", context =>
    {
        var reading = HttpMethods.IsGet(context.Request.Method) || HttpMethods.IsHead(context.Request.Method);
        var identity = context.Items[RegisteredBearerHandler.UserItem] is TideCasa.Contracts.AuthUser user
            ? "user:" + user.UserId : "ip:" + (context.Connection.RemoteIpAddress?.ToString() ?? "unknown");
        return RateLimitPartition.GetFixedWindowLimiter(identity + (reading ? ":read" : ":write"), _ => new FixedWindowRateLimiterOptions
        { PermitLimit = reading ? 600 : 30, Window = TimeSpan.FromMinutes(1), QueueLimit = 0, AutoReplenishment = true });
    });
});
var app = builder.Build();
app.UseAppPlatformIngress(builder.Configuration, api: true);
app.UseForwardedHeaders();
app.UseExceptionHandler();
app.Use(async (context, next) =>
{
    context.Response.Headers.CacheControl = "no-store";
    context.Response.Headers.XContentTypeOptions = "nosniff";
    await next();
});
app.UseAuthentication();
app.UseRateLimiter();
app.UseAuthorization();
app.Use(async (context, next) =>
{
    if (context.GetEndpoint()?.Metadata.GetMetadata<ApiBodyLimit>() is { } limit
        && context.Features.Get<IHttpMaxRequestBodySizeFeature>() is { IsReadOnly: false } body)
        body.MaxRequestBodySize = limit.Bytes;
    await next();
});
if (app.Environment.IsDevelopment()) app.MapOpenApi();
app.MapGet("/health", () => Results.Ok(new { status = "ok", service = "tide-casa-api" }));
app.MapDemoRequests();
app.MapDemoInbox();
app.MapPricing();
app.MapTideCasaAuthentication();
app.MapAccounts();
app.MapRestaurantOrdering();
app.MapRestaurantManagement();
app.MapStaffTraining();
app.MapTideCasaMedia();
app.MapRewards();
app.MapTideCasaEvents();
app.MapTideCasaServiceBilling();
app.MapTideCasaMerchantPayments();
app.MapReferrals();
app.MapTideCasaLaunchReview();
app.MapTideCasaBusinessPosts();
app.MapSalesPipeline();
if (app.Services.GetRequiredService<ApplicationDatabase>().IsPostgreSql)
    await app.Services.GetRequiredService<PostgresSchemaMigrator>().InitializeAsync();
else
{
    await app.Services.GetRequiredService<SchemaMigrator>().InitializeAsync();
    await app.Services.GetRequiredService<DemoRequestStore>().InitializeAsync();
    await app.Services.GetRequiredService<FeatureMigrator>().InitializeAsync();
}
await app.Services.GetRequiredService<SalesPipelineStore>().InitializeAsync();
app.Run();

public partial class Program;
