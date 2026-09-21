using System.Net;
using System.Text.RegularExpressions;
using Amazon.Runtime;
using Amazon.S3;
using Amazon.S3.Model;

namespace TideCasa.Api.Features.Media;

public sealed record ObjectInfo(long Length, string ContentType);
public sealed class ObjectRead(Stream stream, long length, IDisposable? owner = null) : IDisposable
{
    public Stream Stream { get; } = stream;
    public long Length { get; } = length;
    public void Dispose() { Stream.Dispose(); owner?.Dispose(); }
}
public interface IPrivateObjectStore
{
    bool Ready { get; }
    string Label { get; }
    Task PutAsync(string key, string path, string type, CancellationToken ct);
    Task<ObjectInfo?> HeadAsync(string key, CancellationToken ct);
    Task<ObjectRead?> ReadAsync(string key, long? start, long? end, CancellationToken ct);
    Task DeleteAsync(string key, CancellationToken ct);
}

public sealed class PrivateObjectStore : IPrivateObjectStore, IDisposable
{
    private readonly AmazonS3Client? client;
    private readonly string? localRoot;
    private readonly string bucket = "";
    public bool Ready => client is not null || localRoot is not null;
    public string Label => localRoot is not null ? "Local preview storage" : client is not null ? "Private file storage" : "Private uploads are being connected";

    public PrivateObjectStore(IConfiguration config, IHostEnvironment environment)
    {
        var mode = config["Media:Provider"] ?? "disabled";
        if (mode == "local")
        {
            if (!environment.IsDevelopment() || config["Media:AllowLocalStore"] != "true")
                throw new InvalidOperationException("Local media storage is permitted only in explicitly configured development.");
            var configured = config["Media:LocalRoot"];
            if (string.IsNullOrWhiteSpace(configured) || !Path.IsPathFullyQualified(configured))
                throw new InvalidOperationException("Development media storage needs a private absolute directory.");
            localRoot = Path.GetFullPath(configured).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
            var webRoot = Path.GetFullPath(Path.Combine(environment.ContentRootPath, "wwwroot")).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
            if (localRoot.StartsWith(webRoot, StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException("Media storage cannot be public static content.");
            Directory.CreateDirectory(localRoot);
        }
        else if (mode == "r2")
        {
            var account = config["Media:R2:AccountId"] ?? "";
            bucket = config["Media:R2:Bucket"] ?? "";
            var access = config["Media:R2:AccessKeyId"];
            var secret = config["Media:R2:SecretAccessKey"];
            if (!Regex.IsMatch(account, "^[a-fA-F0-9]{32}$") || bucket != "bartide-production-private" || string.IsNullOrWhiteSpace(access) || string.IsNullOrWhiteSpace(secret)) return;
            client = new AmazonS3Client(new BasicAWSCredentials(access, secret), new AmazonS3Config
            {
                ServiceURL = "https://" + account + ".r2.cloudflarestorage.com", AuthenticationRegion = "auto",
                ForcePathStyle = true, Timeout = TimeSpan.FromSeconds(90), MaxErrorRetry = 1
            });
        }
    }

    public async Task PutAsync(string key, string path, string type, CancellationToken ct)
    {
        Check(key);
        if (localRoot is not null)
        {
            var target = LocalPath(key); Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            var temp = target + ".pending-" + Guid.NewGuid().ToString("N");
            try
            {
                await using (var source = File.OpenRead(path))
                await using (var destination = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, true))
                    await source.CopyToAsync(destination, ct);
                File.Move(temp, target, true);
                await File.WriteAllTextAsync(target + ".type", type, ct);
            }
            finally { if (File.Exists(temp)) File.Delete(temp); }
            return;
        }
        if (client is null) throw new MediaException("Private uploads are not connected yet.", 503, "storage_unavailable");
        await client.PutObjectAsync(new PutObjectRequest
        {
            BucketName = bucket, Key = key, FilePath = path, ContentType = type,
            DisablePayloadSigning = true, DisableDefaultChecksumValidation = true
        }, ct);
    }

    public async Task<ObjectInfo?> HeadAsync(string key, CancellationToken ct)
    {
        Check(key);
        if (localRoot is not null)
        {
            var path = LocalPath(key);
            if (!File.Exists(path) || !File.Exists(path + ".type")) return null;
            return new(new FileInfo(path).Length, await File.ReadAllTextAsync(path + ".type", ct));
        }
        if (client is null) throw new MediaException("Private files are not connected yet.", 503, "storage_unavailable");
        try { var value = await client.GetObjectMetadataAsync(bucket, key, ct); return new(value.ContentLength, value.Headers.ContentType); }
        catch (AmazonS3Exception error) when (error.StatusCode == HttpStatusCode.NotFound) { return null; }
    }

    public async Task<ObjectRead?> ReadAsync(string key, long? start, long? end, CancellationToken ct)
    {
        Check(key);
        if (localRoot is not null)
        {
            var path = LocalPath(key); if (!File.Exists(path)) return null;
            var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, true);
            if (start.HasValue) stream.Seek(start.Value, SeekOrigin.Begin);
            return new(stream, start.HasValue ? end!.Value - start.Value + 1 : stream.Length);
        }
        if (client is null) throw new MediaException("Private files are not connected yet.", 503, "storage_unavailable");
        try
        {
            var request = new GetObjectRequest { BucketName = bucket, Key = key };
            if (start.HasValue) request.ByteRange = new ByteRange(start.Value, end!.Value);
            var response = await client.GetObjectAsync(request, ct);
            return new(response.ResponseStream, response.ContentLength, response);
        }
        catch (AmazonS3Exception error) when (error.StatusCode == HttpStatusCode.NotFound) { return null; }
    }

    public async Task DeleteAsync(string key, CancellationToken ct)
    {
        Check(key);
        if (localRoot is not null)
        {
            var path = LocalPath(key);
            // A failed PUT can leave neither the object nor its parent directory.
            // Treat only absence as successful deletion; permission/I/O failures retain metadata.
            try { File.Delete(path); } catch (DirectoryNotFoundException) { }
            try { File.Delete(path + ".type"); } catch (DirectoryNotFoundException) { }
            return;
        }
        if (client is null) throw new MediaException("Private files are not connected yet.", 503, "storage_unavailable");
        await client.DeleteObjectAsync(bucket, key, ct);
    }

    private static void Check(string key)
    {
        if (!Regex.IsMatch(key, "^(menu-photos|source-menus|course-videos)/[A-Za-z0-9_-]{1,128}/[0-9a-f-]{36}(\\.jpg|\\.mp4)?$", RegexOptions.CultureInvariant))
            throw new MediaException("File unavailable.", 404, "file_unavailable");
    }
    private string LocalPath(string key)
    {
        var path = Path.GetFullPath(Path.Combine(localRoot!, key.Replace('/', Path.DirectorySeparatorChar)));
        if (!path.StartsWith(localRoot!, StringComparison.OrdinalIgnoreCase)) throw new MediaException("File unavailable.", 404, "file_unavailable");
        return path;
    }
    public void Dispose() => client?.Dispose();
}
