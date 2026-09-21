using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Http.Features;
using TideCasa.Blazor.Features.Authentication;
using TideCasa.Blazor.Services;

namespace TideCasa.Blazor.Features.Media;

public static partial class MediaFlow
{
    public static IEndpointRouteBuilder MapTideCasaMedia(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapPost("/private-media/manage/{tenant}/{kind}/upload", UploadAsync).RequireAuthorization();
        endpoints.MapPost("/private-media/manage/{tenant}/{kind}/{id}/delete", DeleteAsync).RequireAuthorization();
        endpoints.MapMethods("/private-media/{tenant}/{kind}/{id}", ["GET", "HEAD"], ReadAsync).RequireAuthorization();
        endpoints.MapMethods("/media/{id}", ["GET", "HEAD"], ReadPublicPhotoAsync);
        return endpoints;
    }

    private static async Task<IResult> UploadAsync(string tenant, string kind, HttpContext context, IAntiforgery antiforgery, MediaClient api)
    {
        var maximum = Limit(kind);
        if (!Identifier().IsMatch(tenant) || maximum == 0) return Results.NotFound();
        if (context.Items.ContainsKey(AuthFlow.UnavailableItem)) return AuthFlow.ServiceUnavailable();
        if (context.Items[AuthFlow.TokenItem] is not string token) return Results.Unauthorized();
        if (!SameOrigin(context.Request) || !context.Request.HasFormContentType || context.Request.ContentLength is not > 0 || context.Request.ContentLength > maximum + 65536) return Redirect(tenant, "invalid");
        var body = context.Features.Get<IHttpMaxRequestBodySizeFeature>();
        if (body is { IsReadOnly: false }) body.MaxRequestBodySize = maximum + 65536;
        context.Features.Set<IFormFeature>(new FormFeature(context.Request, new FormOptions
        {
            BufferBody = false, MemoryBufferThreshold = 64 * 1024, MultipartBodyLengthLimit = maximum,
            ValueLengthLimit = 4096, ValueCountLimit = 5, KeyLengthLimit = 100,
            MultipartHeadersCountLimit = 8, MultipartHeadersLengthLimit = 4096, MultipartBoundaryLengthLimit = 128
        }));
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(context.RequestAborted); deadline.CancelAfter(TimeSpan.FromMinutes(5));
        try
        {
            await antiforgery.ValidateRequestAsync(context);
            var form = await context.Request.ReadFormAsync(deadline.Token);
            if (form.Count > 3 || form.Any(field => field.Value.Count != 1) || form.Files.Count != 1 || !Guid.TryParseExact(form["requestId"], "D", out _)) return Redirect(tenant, "invalid");
            var file = form.Files[0];
            if (file.Name != "file" || file.Length is <= 0 || file.Length > maximum || file.FileName.Length > 512 || !AllowedType(kind, file.ContentType)) return Redirect(tenant, "invalid");
            await using var input = file.OpenReadStream();
            var result = await api.UploadAsync(tenant, kind, token, form["requestId"].ToString(), file.FileName, file.ContentType, file.Length, input, deadline.Token);
            return Redirect(tenant, result.Succeeded ? "uploaded" : NoticeCode(result.Code, result.Uncertain));
        }
        catch (AntiforgeryValidationException) { return Redirect(tenant, "expired"); }
        catch (Exception error) when (error is InvalidDataException or BadHttpRequestException or IOException or OperationCanceledException)
        { return Redirect(tenant, "invalid"); }
    }

    private static async Task<IResult> DeleteAsync(string tenant, string kind, string id, HttpContext context, IAntiforgery antiforgery, MediaClient api)
    {
        if (!Identifier().IsMatch(tenant) || Limit(kind) == 0 || !Guid.TryParseExact(id, "D", out _)) return Results.NotFound();
        if (context.Items.ContainsKey(AuthFlow.UnavailableItem)) return AuthFlow.ServiceUnavailable();
        if (context.Items[AuthFlow.TokenItem] is not string token) return Results.Unauthorized();
        if (!SameOrigin(context.Request) || !context.Request.HasFormContentType) return Results.BadRequest();
        var body = context.Features.Get<IHttpMaxRequestBodySizeFeature>(); if (body is { IsReadOnly: false }) body.MaxRequestBodySize = 8192;
        try
        {
            await antiforgery.ValidateRequestAsync(context);
            var form = await context.Request.ReadFormAsync(context.RequestAborted);
            if (form.Files.Count != 0 || form.Count > 2) return Redirect(tenant, "invalid");
        }
        catch (Exception error) when (error is AntiforgeryValidationException or InvalidDataException or BadHttpRequestException) { return Redirect(tenant, "expired"); }
        var result = await api.DeleteAsync(tenant, kind, id, token, context.RequestAborted);
        return Redirect(tenant, result.Succeeded ? "removed" : NoticeCode(result.Code, result.Uncertain));
    }

    private static async Task<IResult> ReadAsync(string tenant, string kind, string id, HttpContext context, MediaClient api)
    {
        if (!Identifier().IsMatch(tenant) || Limit(kind) == 0 || !Guid.TryParseExact(id, "D", out _)) return Results.NotFound();
        if (context.Items.ContainsKey(AuthFlow.UnavailableItem)) return AuthFlow.ServiceUnavailable();
        if (context.Items[AuthFlow.TokenItem] is not string token) return Results.Unauthorized();
        var range = context.Request.Headers.Range.ToString(); if (range.Length > 100) return Results.StatusCode(416);
        using var response = await api.ReadAsync(tenant, kind, id, token, HttpMethods.IsHead(context.Request.Method), range, context.RequestAborted);
        return await ProxyAsync(kind, context, response);
    }

    private static async Task<IResult> ReadPublicPhotoAsync(string id, HttpContext context, MediaClient api)
    {
        if (!Guid.TryParseExact(id, "D", out _)) return Results.NotFound();
        var range = context.Request.Headers.Range.ToString(); if (range.Length > 100) return Results.StatusCode(416);
        using var response = await api.ReadPublicPhotoAsync(id, HttpMethods.IsHead(context.Request.Method), range, context.RequestAborted);
        return await ProxyAsync("photo", context, response);
    }

    private static async Task<IResult> ProxyAsync(string kind, HttpContext context, HttpResponseMessage? response)
    {
        if (response is null) return Results.StatusCode(503);
        var status = (int)response.StatusCode;
        if (status == 416)
        {
            if (response.Content.Headers.ContentRange is { } value) context.Response.Headers.ContentRange = value.ToString();
            return Results.StatusCode(416);
        }
        if (status is not (200 or 206)) return Results.StatusCode(status is 401 or 403 or 404 ? status : 503);
        if (response.Content.Headers.ContentLength is not long length || length < 0 || length > Limit(kind)
            || !AllowedType(kind, response.Content.Headers.ContentType?.MediaType ?? "")) return Results.StatusCode(503);
        context.Response.StatusCode = status; context.Response.ContentLength = length;
        context.Response.ContentType = response.Content.Headers.ContentType!.MediaType;
        context.Response.Headers.CacheControl = "private, no-store";
        context.Response.Headers["X-Content-Type-Options"] = "nosniff";
        context.Response.Headers["Referrer-Policy"] = "no-referrer";
        context.Response.Headers["Content-Security-Policy"] = "default-src 'none'; sandbox";
        context.Response.Headers.AcceptRanges = "bytes";
        if (response.Content.Headers.ContentRange is { } contentRange) context.Response.Headers.ContentRange = contentRange.ToString();
        if (response.Content.Headers.ContentDisposition is { } disposition) context.Response.Headers.ContentDisposition = disposition.ToString();
        if (!HttpMethods.IsHead(context.Request.Method))
        {
            await using var stream = await response.Content.ReadAsStreamAsync(context.RequestAborted);
            var buffer = new byte[81920]; long remaining = length;
            while (remaining > 0)
            {
                var read = await stream.ReadAsync(buffer.AsMemory(0, (int)Math.Min(buffer.Length, remaining)), context.RequestAborted);
                if (read == 0) { context.Abort(); return Results.Empty; }
                await context.Response.Body.WriteAsync(buffer.AsMemory(0, read), context.RequestAborted); remaining -= read;
            }
        }
        return Results.Empty;
    }

    public static string? Notice(string? value) => value switch
    {
        "uploaded" => "Your file is ready.", "removed" => "The file was removed.",
        "expired" => "That form expired. Select your file and try again.",
        "referenced" => "This file is used by a menu item or lesson. Remove that connection first.",
        "full" => "Your storage allowance is full. Remove unused files first.",
        "pending" => "An upload is still finishing or needs cleanup. Refresh your file list before trying again.",
        "unconfirmed" => "We couldn’t confirm that change. Check your file list before trying again.",
        "invalid" => "The file was not saved. Check its format and size, then try again.", _ => null
    };
    private static string NoticeCode(string? code, bool uncertain) => code switch { "file_referenced" => "referenced", "storage_full" => "full", "upload_pending" => "pending", _ => uncertain ? "unconfirmed" : "invalid" };
    private static long Limit(string kind) => kind switch { "photo" => 2 * 1024 * 1024, "menu" => 10 * 1024 * 1024, "video" => 50 * 1024 * 1024, _ => 0 };
    private static bool AllowedType(string kind, string type) => kind switch { "photo" => type == "image/jpeg", "menu" => type is "image/jpeg" or "application/pdf", "video" => type == "video/mp4", _ => false };
    private static IResult Redirect(string tenant, string notice) => Results.LocalRedirect("/workspace/" + Uri.EscapeDataString(tenant) + "/media?notice=" + notice);
    private static bool SameOrigin(HttpRequest request)
    {
        if (!Uri.TryCreate(request.Scheme + "://" + request.Host, UriKind.Absolute, out var expected)) return false;
        var origin = request.Headers.Origin.ToString(); var value = string.IsNullOrEmpty(origin) ? request.Headers.Referer.ToString() : origin;
        return Uri.TryCreate(value, UriKind.Absolute, out var source) && string.IsNullOrEmpty(source.UserInfo)
            && source.Scheme == expected.Scheme && source.IdnHost.Equals(expected.IdnHost, StringComparison.OrdinalIgnoreCase)
            && source.Port == expected.Port && (string.IsNullOrEmpty(origin) || source.AbsolutePath == "/") && request.Headers["Sec-Fetch-Site"] != "cross-site";
    }
    [GeneratedRegex("^[A-Za-z0-9_-]{1,128}$", RegexOptions.CultureInvariant)] private static partial Regex Identifier();
}
