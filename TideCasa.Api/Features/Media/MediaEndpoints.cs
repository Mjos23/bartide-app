using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using Amazon.S3;
using Microsoft.AspNetCore.Http.Features;
using System.Data.Common;
using TideCasa.Api.Features.Authentication;
using TideCasa.Api.Infrastructure;
using TideCasa.Contracts;

namespace TideCasa.Api.Features.Media;

public static class MediaEndpoints
{
    public static IServiceCollection AddTideCasaMedia(this IServiceCollection services, IConfiguration configuration, IHostEnvironment environment)
    {
        if (configuration["PublicDemo:Enabled"] == "true" && configuration["Media:Provider"] == "demo-bundled")
            services.AddSingleton<IPrivateObjectStore, BundledDemoObjectStore>();
        else services.AddSingleton<IPrivateObjectStore, PrivateObjectStore>();
        services.AddScoped<MediaStore>();
        services.AddHostedService<MediaCleanup>();
        return services;
    }

    public static void MapTideCasaMedia(this WebApplication app)
    {
        var group = app.MapGroup("/api/v1/tenants/{tenant}/media").RequireAuthorization().RequireRateLimiting("media").WithTags("Private media").AddEndpointFilter<MediaFilter>();
        group.MapGet("", async (string tenant, HttpContext context, MediaStore store, CancellationToken ct) => Results.Ok(await store.WorkspaceAsync(tenant, User(context), ct)));
        group.MapPost("/{kind}", UploadAsync).WithMetadata(new ApiBodyLimit(50 * 1024 * 1024));
        group.MapDelete("/{kind}/{id}", async (string tenant, string kind, string id, HttpContext context, MediaStore store, CancellationToken ct) =>
        { await store.DeleteAsync(tenant, kind, id, User(context), ct); return Results.NoContent(); });
        group.MapMethods("/{kind}/{id}", ["GET", "HEAD"], async (string tenant, string kind, string id, HttpContext context, MediaStore store, IPrivateObjectStore objects, CancellationToken ct) =>
            await StreamAsync(await store.ReadAuthorizedAsync(tenant, kind, id, User(context), ct), context, objects, ct));
        app.MapMethods("/api/v1/media/photos/{id}", ["GET", "HEAD"], async (string id, HttpContext context, MediaStore store, IPrivateObjectStore objects, CancellationToken ct) =>
            await StreamAsync(await store.ReadPublicPhotoAsync(id, ct), context, objects, ct)).RequireRateLimiting("media").AddEndpointFilter<MediaFilter>();
    }

    private static async Task<IResult> UploadAsync(string tenant, string kind, HttpContext context, MediaStore store)
    {
        var limit = MediaStore.Limit(kind);
        if (context.Features.Get<IHttpMaxRequestBodySizeFeature>() is { IsReadOnly: false } body) body.MaxRequestBodySize = limit;
        if (context.Request.ContentLength is not > 0 || context.Request.ContentLength > limit || context.Request.Headers.ContainsKey("Content-Encoding"))
            throw new MediaException("Choose a file within the upload limit.", 400);
        if (context.Request.Headers["X-Request-Id"].Count != 1 || context.Request.Headers["X-File-Name"].Count != 1)
            throw new MediaException("The file upload request is incomplete.");
        var encodedName = context.Request.Headers["X-File-Name"].ToString();
        if (encodedName.Length > 4608) throw new MediaException("Use a shorter file name.");
        var name = Uri.UnescapeDataString(encodedName);
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(context.RequestAborted); deadline.CancelAfter(TimeSpan.FromMinutes(5));
        return Results.Ok(await store.UploadAsync(tenant, kind, User(context), context.Request.Headers["X-Request-Id"].ToString(), name,
            context.Request.ContentType ?? "", context.Request.ContentLength.Value, context.Request.Body, deadline.Token));
    }

    private static async Task<IResult> StreamAsync(MediaRecord file, HttpContext context, IPrivateObjectStore objects, CancellationToken ct)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct); deadline.CancelAfter(TimeSpan.FromMinutes(5));
        var info = await objects.HeadAsync(file.ObjectKey, deadline.Token);
        if (info is null || info.Length != file.ByteSize || info.ContentType != file.ContentType)
            throw new MediaException("This file is temporarily unavailable.", 503, "file_unavailable");
        var range = context.Request.Headers.Range.ToString();
        if (!TryRange(range, file.ByteSize, out var start, out var end))
        {
            context.Response.Headers.ContentRange = "bytes */" + file.ByteSize.ToString(CultureInfo.InvariantCulture);
            return Results.StatusCode(416);
        }
        var length = start.HasValue ? end!.Value - start.Value + 1 : file.ByteSize;
        // Open the object before committing successful headers. HEAD deliberately makes no GET request.
        using var source = HttpMethods.IsHead(context.Request.Method) ? null : await objects.ReadAsync(file.ObjectKey, start, end, deadline.Token);
        if (!HttpMethods.IsHead(context.Request.Method) && (source is null || source.Length != length))
            throw new MediaException("This file is temporarily unavailable.", 503, "file_unavailable");
        context.Response.StatusCode = start.HasValue ? 206 : 200;
        context.Response.ContentType = file.ContentType;
        context.Response.ContentLength = length;
        context.Response.Headers.CacheControl = "private, no-store";
        context.Response.Headers.AcceptRanges = "bytes";
        context.Response.Headers.XContentTypeOptions = "nosniff";
        context.Response.Headers["Referrer-Policy"] = "no-referrer";
        context.Response.Headers.ContentSecurityPolicy = "default-src 'none'; sandbox";
        var disposition = new ContentDispositionHeaderValue(file.Kind == "menu" ? "attachment" : "inline") { FileName = "download" + Path.GetExtension(file.Name), FileNameStar = file.Name };
        context.Response.Headers.ContentDisposition = disposition.ToString();
        if (start.HasValue) context.Response.Headers.ContentRange = $"bytes {start}-{end}/{file.ByteSize}";
        if (source is not null)
        {
            var buffer = new byte[81920]; var remaining = length;
            while (remaining > 0)
            {
                var read = await source.Stream.ReadAsync(buffer.AsMemory(0, (int)Math.Min(buffer.Length, remaining)), deadline.Token);
                if (read == 0) { context.Abort(); return Results.Empty; }
                await context.Response.Body.WriteAsync(buffer.AsMemory(0, read), deadline.Token); remaining -= read;
            }
        }
        return Results.Empty;
    }

    private static bool TryRange(string header, long length, out long? start, out long? end)
    {
        start = end = null;
        if (header.Length == 0) return true;
        if (header.Length > 100 || !header.StartsWith("bytes=", StringComparison.Ordinal) || header.Contains(',')) return false;
        var fields = header[6..].Split('-');
        if (fields.Length != 2 || length <= 0) return false;
        if (fields[0].Length == 0)
        {
            if (!long.TryParse(fields[1], NumberStyles.None, CultureInfo.InvariantCulture, out var suffix) || suffix <= 0) return false;
            start = Math.Max(0, length - suffix); end = length - 1; return true;
        }
        if (!long.TryParse(fields[0], NumberStyles.None, CultureInfo.InvariantCulture, out var first) || first >= length) return false;
        var last = length - 1;
        if (fields[1].Length != 0 && (!long.TryParse(fields[1], NumberStyles.None, CultureInfo.InvariantCulture, out last) || last < first)) return false;
        start = first; end = Math.Min(last, length - 1); return true;
    }

    private static AuthUser User(HttpContext context) => (AuthUser)context.Items[RegisteredBearerHandler.UserItem]!;
}

public sealed class MediaFilter : IEndpointFilter
{
    public async ValueTask<object?> InvokeAsync(EndpointFilterInvocationContext context, EndpointFilterDelegate next)
    {
        try { return await next(context); }
        catch (MediaException error) { return Error(context.HttpContext, error.Status, error.Message, error.Code); }
        catch (Exception error) when (error is DbException or AmazonS3Exception or IOException or UnauthorizedAccessException or InvalidOperationException or OperationCanceledException or System.Text.Json.JsonException)
        { return Error(context.HttpContext, 503, "Your files are temporarily unavailable. Please try again.", "media_unavailable"); }
    }
    private static IResult Error(HttpContext context, int status, string title, string code)
    {
        if (context.Response.HasStarted) { context.Abort(); return Results.Empty; }
        return Results.Problem(statusCode: status, title: title, extensions: new Dictionary<string, object?> { ["code"] = code });
    }
}

public sealed class MediaCleanup(IServiceScopeFactory scopes, ILogger<MediaCleanup> logger, IConfiguration configuration, IHostEnvironment environment, IPrivateObjectStore objects) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var interval = environment.IsDevelopment() && int.TryParse(configuration["Media:DevelopmentCleanupSeconds"], out var seconds) ? Math.Clamp(seconds, 1, 120) : 120;
                await Task.Delay(TimeSpan.FromSeconds(interval), stoppingToken);
                if (!objects.Ready) continue;
                using var scope = scopes.CreateScope();
                await scope.ServiceProvider.GetRequiredService<MediaStore>().CleanupAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            catch (Exception) { logger.LogWarning("Private media cleanup will retry on its next run."); }
        }
    }
}
