using System.Text.RegularExpressions;

namespace TideCasa.Api.Features.Media;

/// <summary>Immutable fictional photos/video shipped with the isolated public demo.</summary>
public sealed class BundledDemoObjectStore(IWebHostEnvironment environment) : IPrivateObjectStore
{
    public bool Ready => true;
    public string Label => "Sample photos and training video (fixed for this shared demo)";
    private string FilePath(string key)
    {
        if (!Regex.IsMatch(key, "^(menu-photos|course-videos)/gulf-lantern/[0-9a-f-]{36}(\\.jpg|\\.mp4)?$", RegexOptions.CultureInvariant))
            throw new MediaException("Sample file unavailable.", 404, "file_unavailable");
        return Path.Combine(environment.ContentRootPath, "DemoMedia", key.Replace('/', Path.DirectorySeparatorChar));
    }
    public Task PutAsync(string key, string path, string type, CancellationToken ct) =>
        throw new MediaException("Photos and videos are fixed in this shared sample. Uploads are available in your own business app.", 403, "demo_media_read_only");
    public Task DeleteAsync(string key, CancellationToken ct) =>
        throw new MediaException("Sample photos and videos are preserved for the next presentation.", 403, "demo_media_read_only");
    public Task<ObjectInfo?> HeadAsync(string key, CancellationToken ct)
    {
        var path = FilePath(key);
        return Task.FromResult<ObjectInfo?>(File.Exists(path)
            ? new(new FileInfo(path).Length, key.StartsWith("course-videos/", StringComparison.Ordinal) ? "video/mp4" : "image/jpeg") : null);
    }
    public Task<ObjectRead?> ReadAsync(string key, long? start, long? end, CancellationToken ct)
    {
        var path = FilePath(key);
        if (!File.Exists(path)) return Task.FromResult<ObjectRead?>(null);
        var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, true);
        if (start.HasValue) stream.Seek(start.Value, SeekOrigin.Begin);
        return Task.FromResult<ObjectRead?>(new(stream, start.HasValue ? end!.Value - start.Value + 1 : stream.Length));
    }
}
