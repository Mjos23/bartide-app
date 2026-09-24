using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Data.Common;
using SkiaSharp;
using TideCasa.Api.Infrastructure;
using TideCasa.Contracts;

namespace TideCasa.Api.Features.Media;

public sealed class MediaException(string message, int status = 400, string code = "invalid_media") : Exception(message)
{
    public int Status { get; } = status;
    public string Code { get; } = code;
}

public sealed record MediaRecord(string Id, string TenantId, string Kind, string ObjectKey, string Name, string ContentType, long ByteSize, string Status, string CreatedAt);

public sealed class MediaStore(ApplicationDatabase database, IPrivateObjectStore objects, IConfiguration configuration, IHostEnvironment environment)
{
    private static readonly SemaphoreSlim UploadSlots = new(3, 3);
    public static long Limit(string kind) => kind switch { "photo" => 2 * 1024 * 1024, "menu" => 10 * 1024 * 1024, "video" => 50 * 1024 * 1024, _ => throw new MediaException("Choose a valid file type.") };
    private static string Table(string kind) => kind switch { "photo" => "bartide_photos", "menu" => "bartide_menu_files", "video" => "fit_videos", _ => throw new MediaException("Choose a valid file type.") };
    private static long Quota(string kind) => kind switch { "photo" => 200L * 1024 * 1024, "menu" => 50L * 1024 * 1024, _ => 1024L * 1024 * 1024 };
    private static int CountLimit(string kind) => kind == "menu" ? 10 : 500;
    private static string Now() => DateTimeOffset.UtcNow.ToString("O");
    private static MediaException Missing() => new("File unavailable.", 404, "file_unavailable");

    public async Task<MediaWorkspace> WorkspaceAsync(string tenant, AuthUser user, CancellationToken ct)
    {
        await using var db = await database.OpenAsync(ct); using var tx = db.BeginTransaction(deferred: false);
        var name = await Owner(db, tx, tenant, user, ct);
        var files = new List<MediaFile>();
        foreach (var kind in new[] { "photo", "menu", "video" })
            foreach (var record in await Records(db, tx, tenant, kind, null, ct))
                files.Add(Public(record, await Referenced(db, tx, record, ct)));
        await tx.CommitAsync(ct);
        return new(tenant, name, objects.Ready, objects.Label, files.OrderByDescending(x => x.CreatedAt).ToArray());
    }

    public async Task<MediaUploadResult> UploadAsync(string tenant, string kind, AuthUser user, string requestKey,
        string filename, string type, long length, Stream input, CancellationToken ct)
    {
        var maximum = Limit(kind);
        if (!Guid.TryParseExact(requestKey, "D", out _) || length <= 0 || length > maximum) throw new MediaException("Choose a file within the upload limit.");
        type = type.Split(';')[0].Trim().ToLowerInvariant();
        if ((kind == "photo" && type != "image/jpeg") || (kind == "menu" && type is not ("image/jpeg" or "application/pdf")) || (kind == "video" && type != "video/mp4"))
            throw new MediaException("Choose a JPG photo, a PDF or JPG menu, or an MP4 video.", 415);
        var name = Filename(filename, type);
        await using (var check = await database.OpenAsync(ct))
        {
            using var tx = check.BeginTransaction(deferred: false);
            await Owner(check, tx, tenant, user, ct);
            await tx.CommitAsync(ct);
        }
        if (!objects.Ready) throw new MediaException("Private uploads are not connected yet.", 503, "storage_unavailable");
        var spool = SpoolDirectory();
        if (!await UploadSlots.WaitAsync(0, ct)) throw new MediaException("Other uploads are finishing. Try again shortly.", 429, "uploads_busy");
        var inputPath = Path.Combine(spool, Guid.NewGuid().ToString("N") + ".upload");
        string? processedPath = null; MediaRecord? reserved = null;
        try
        {
            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            long actual = 0; var buffer = new byte[81920];
            await using (var output = new FileStream(inputPath, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, true))
            {
                for (;;) { var count = await input.ReadAsync(buffer, ct); if (count == 0) break; actual += count; if (actual > length || actual > maximum) throw new MediaException("The file exceeded its declared size."); hash.AppendData(buffer, 0, count); await output.WriteAsync(buffer.AsMemory(0, count), ct); }
            }
            if (actual != length) throw new MediaException("The upload was incomplete. Select the file again.");
            var requestHash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(kind + "\n" + name + "\n" + type + "\n" + Convert.ToHexString(hash.GetHashAndReset()))));
            var width = 0; var height = 0;
            if (type == "image/jpeg")
            {
                using var stream = File.OpenRead(inputPath); using var codec = SKCodec.Create(stream);
                if (codec is null || codec.EncodedFormat != SKEncodedImageFormat.Jpeg || codec.Info.Width is < 1 or > 1600 || codec.Info.Height is < 1 or > 1600)
                    throw new MediaException("Use a JPG image no larger than 1600 pixels on either side.");
                using var bitmap = SKBitmap.Decode(codec);
                if (bitmap is null) throw new MediaException("This image could not be decoded.");
                width = bitmap.Width; height = bitmap.Height;
                using var image = SKImage.FromBitmap(bitmap); using var jpeg = image.Encode(SKEncodedImageFormat.Jpeg, 84);
                if (jpeg is null || jpeg.Size <= 0 || jpeg.Size > maximum) throw new MediaException("The optimized photo is too large.");
                processedPath = inputPath + ".jpg";
                await using var target = new FileStream(processedPath, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, true); jpeg.SaveTo(target);
            }
            else ValidateContainer(inputPath, type);
            var storedPath = processedPath ?? inputPath; var storedLength = new FileInfo(storedPath).Length;
            await using (var db = await database.OpenAsync(ct))
            {
                using var tx = db.BeginTransaction(deferred: false); await Owner(db, tx, tenant, user, ct);
                await using var lookup = Command(db, tx, "SELECT request_hash,media_id,state FROM tide_media_operations WHERE tenant_id=@tenant AND kind=@kind AND request_key=@key", ("@tenant", tenant), ("@kind", kind), ("@key", requestKey));
                (string Hash, string Id, string State)? previous = null;
                await using (var reader = await lookup.ExecuteReaderAsync(ct)) if (await reader.ReadAsync(ct)) previous = (reader.GetString(0), reader.GetString(1), reader.GetString(2));
                if (previous is { } prior)
                {
                    if (prior.Hash != requestHash) throw new MediaException("This upload request was already used for a different file.", 409, "upload_conflict");
                    if (prior.State != "ready") throw new MediaException("This upload needs review before trying it again. Check the current file list.", 409, "upload_pending");
                    var existing = (await Records(db, tx, tenant, kind, prior.Id, ct)).SingleOrDefault();
                    if (existing is null || existing.Status != "ready") throw Missing();
                    return new(Public(existing, await Referenced(db, tx, existing, ct)));
                }
                var table = Table(kind);
                var count = await Scalar(db, tx, $"SELECT COUNT(*) FROM {table} WHERE tenant_id=@tenant", ct, ("@tenant", tenant));
                var total = await Scalar(db, tx, $"SELECT COALESCE(SUM(byte_size),0) FROM {table} WHERE tenant_id=@tenant", ct, ("@tenant", tenant));
                var global = await Scalar(db, tx, "SELECT (SELECT COALESCE(SUM(byte_size),0) FROM bartide_photos)+(SELECT COALESCE(SUM(byte_size),0) FROM bartide_menu_files)+(SELECT COALESCE(SUM(byte_size),0) FROM fit_videos)", ct);
                var globalLimit = long.TryParse(configuration["Media:MaxApplicationBytes"], out var configured) && configured > 0 ? Math.Min(configured, 4L * 1024 * 1024 * 1024) : 4L * 1024 * 1024 * 1024;
                if (count >= CountLimit(kind) || total > Quota(kind) - storedLength || global > globalLimit - storedLength) throw new MediaException("Storage is full. Remove unused files before adding another.", 409, "storage_full");
                var id = Guid.NewGuid().ToString("D"); var prefix = kind switch { "photo" => "menu-photos", "menu" => "source-menus", _ => "course-videos" };
                var key = prefix + "/" + tenant + "/" + id + (kind == "photo" ? ".jpg" : kind == "video" ? ".mp4" : ""); var now = Now();
                var sql = kind == "photo"
                    ? "INSERT INTO bartide_photos(id,tenant_id,object_key,byte_size,width,height,status,created_at) VALUES(@id,@tenant,@object,@size,@width,@height,'pending',@now)"
                    : kind == "menu" ? "INSERT INTO bartide_menu_files(id,tenant_id,object_key,name,content_type,byte_size,status,created_at) VALUES(@id,@tenant,@object,@name,@type,@size,'pending',@now)"
                    : "INSERT INTO fit_videos(id,tenant_id,object_key,name,byte_size,status,created_at) VALUES(@id,@tenant,@object,@name,@size,'pending',@now)";
                await Run(db, tx, sql, ct, ("@id", id), ("@tenant", tenant), ("@object", key), ("@size", storedLength), ("@width", width), ("@height", height), ("@name", name), ("@type", type), ("@now", now));
                await Run(db, tx, "INSERT INTO tide_media_operations(id,tenant_id,kind,request_key,request_hash,media_id,object_key,state,created_at,updated_at) VALUES(@id,@tenant,@kind,@key,@hash,@media,@object,'pending',@now,@now)", ct,
                    ("@id", Guid.NewGuid().ToString("D")), ("@tenant", tenant), ("@kind", kind), ("@key", requestKey), ("@hash", requestHash), ("@media", id), ("@object", key), ("@now", now));
                await tx.CommitAsync(ct); reserved = new(id, tenant, kind, key, kind == "photo" ? "Menu photo" : name, type, storedLength, "pending", now);
            }
            await objects.PutAsync(reserved.ObjectKey, storedPath, type, ct);
            await using (var db = await database.OpenAsync(ct))
            {
                using var tx = db.BeginTransaction(deferred: false); await Owner(db, tx, tenant, user, ct);
                if (await Run(db, tx, $"UPDATE {Table(kind)} SET status='ready' WHERE id=@id AND tenant_id=@tenant AND status='pending'", ct, ("@id", reserved.Id), ("@tenant", tenant)) != 1) throw new MediaException("The upload state changed. Review your files.", 409);
                await Run(db, tx, "UPDATE tide_media_operations SET state='ready',updated_at=@now WHERE media_id=@id AND tenant_id=@tenant AND state='pending'", ct, ("@id", reserved.Id), ("@tenant", tenant), ("@now", Now()));
                await tx.CommitAsync(ct);
            }
            var result = new MediaUploadResult(Public(reserved with { Status = "ready" }, false)); reserved = null; return result;
        }
        catch
        {
            if (reserved is not null)
            {
                // Keep the reserved bytes and object association if cleanup is uncertain.
                try
                {
                    await using var db = await database.OpenAsync(CancellationToken.None);
                    using var tx = db.BeginTransaction(deferred: false);
                    await Run(db, tx, "UPDATE tide_media_operations SET state='cleanup',updated_at=@now WHERE media_id=@id AND tenant_id=@tenant AND state='pending'", CancellationToken.None,
                        ("@id", reserved.Id), ("@tenant", tenant), ("@now", Now()));
                    await tx.CommitAsync(CancellationToken.None);
                }
                catch (DbException) { }
            }
            throw;
        }
        finally
        {
            UploadSlots.Release();
            try { File.Delete(inputPath); if (processedPath is not null) File.Delete(processedPath); } catch (IOException) { }
        }
    }

    public async Task<MediaRecord> ReadPublicPhotoAsync(string id, CancellationToken ct)
    {
        if (!Guid.TryParseExact(id, "D", out _)) throw Missing();
        await using var db = await database.OpenAsync(ct); using var tx = db.BeginTransaction(deferred: false);
        var tenant = await TextScalar(db, tx, "SELECT p.tenant_id FROM bartide_photos p JOIN bartide_customers t ON t.id=p.tenant_id WHERE p.id=@id AND p.status='ready' AND t.status='active'", ct, ("@id", id));
        if (tenant is null) throw Missing();
        var file = (await Records(db, tx, tenant, "photo", id, ct)).SingleOrDefault();
        if (file is null || !await Referenced(db, tx, file, ct, publicAccess: true)) throw Missing();
        await tx.CommitAsync(ct); return file;
    }

    // Only known operation keys are eligible. Thirty minutes exceeds the enforced
    // five-minute upload deadline; active uploads are never scanned or deleted.
    public async Task CleanupAsync(CancellationToken ct)
    {
        if (!objects.Ready) return;
        var cutoff = DateTimeOffset.UtcNow.AddMinutes(-30).ToString("O");
        var candidates = new List<(string Tenant, string Kind, string Id)>();
        await using (var db = await database.OpenAsync(ct))
        {
            await using var command = Command(db, null, "SELECT tenant_id,kind,media_id FROM tide_media_operations WHERE state IN('pending','cleanup') AND updated_at<@cutoff ORDER BY updated_at LIMIT 30", ("@cutoff", cutoff));
            await using var reader = await command.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct)) candidates.Add((reader.GetString(0), reader.GetString(1), reader.GetString(2)));
        }
        foreach (var candidate in candidates)
        {
            MediaRecord? file;
            await using (var db = await database.OpenAsync(ct))
            {
                using var tx = db.BeginTransaction(deferred: false);
                var eligible = await Scalar(db, tx, "SELECT COUNT(*) FROM tide_media_operations WHERE tenant_id=@tenant AND media_id=@id AND state IN('pending','cleanup') AND updated_at<@cutoff", ct, ("@tenant", candidate.Tenant), ("@id", candidate.Id), ("@cutoff", cutoff));
                if (eligible != 1) continue;
                file = (await Records(db, tx, candidate.Tenant, candidate.Kind, candidate.Id, ct)).SingleOrDefault();
                // A referenced or unexpectedly-ready row needs operator review, never deletion.
                if (file is null || file.Status == "ready" || await Referenced(db, tx, file, ct)) continue;
                await Run(db, tx, $"UPDATE {Table(candidate.Kind)} SET status='deleting' WHERE id=@id AND tenant_id=@tenant", ct, ("@id", candidate.Id), ("@tenant", candidate.Tenant));
                await Run(db, tx, "UPDATE tide_media_operations SET state='cleanup',updated_at=@now WHERE media_id=@id AND tenant_id=@tenant", ct, ("@id", candidate.Id), ("@tenant", candidate.Tenant), ("@now", Now()));
                await tx.CommitAsync(ct);
            }
            await objects.DeleteAsync(file.ObjectKey, ct);
            await FinishDeleteAsync(candidate.Tenant, candidate.Kind, candidate.Id, ct);
        }
        foreach (var path in Directory.EnumerateFiles(SpoolDirectory(), "*.upload*", SearchOption.TopDirectoryOnly))
            if (File.GetLastWriteTimeUtc(path) < DateTime.UtcNow.AddMinutes(-30))
                try { File.Delete(path); } catch (IOException) { }
    }

    public async Task<MediaRecord> ReadAuthorizedAsync(string tenant, string kind, string id, AuthUser user, CancellationToken ct)
    {
        _ = Limit(kind); CheckId(tenant); if (!Guid.TryParseExact(id, "D", out _)) throw Missing();
        await using var db = await database.OpenAsync(ct); using var tx = db.BeginTransaction(deferred: false);
        var record = (await Records(db, tx, tenant, kind, id, ct)).SingleOrDefault();
        if (record is null || record.Status != "ready") throw Missing();
        if (!await CanManage(db, tx, tenant, user, ct))
        {
            if (kind != "video") throw Missing();
            var sql = db.Sql("""
                SELECT COUNT(*) FROM fit_lessons l JOIN fit_courses c ON c.id=l.course_id AND c.tenant_id=l.tenant_id
                JOIN bartide_customers t ON t.id=l.tenant_id JOIN bartide_enhanced_configs e ON e.tenant_id=t.id
                JOIN tide_staff_course_assignments a ON a.course_id=c.id AND a.tenant_id=t.id
                JOIN bartide_enhanced_members m ON m.id=a.member_id AND m.tenant_id=t.id
                WHERE t.id=@tenant AND t.status='active' AND t.vertical='bartide' AND json_extract(e.settings_json,'$.enabled')=1
                AND c.published=1 AND l.video_kind='upload' AND l.video_source=@video
                AND a.active=1 AND m.active=1 AND m.user_id=@user AND m.role IN('manager','bartender','server','kitchen','driver')
                """, """
                SELECT COUNT(*) FROM fit_lessons l JOIN fit_courses c ON c.id=l.course_id AND c.tenant_id=l.tenant_id
                JOIN bartide_customers t ON t.id=l.tenant_id JOIN bartide_enhanced_configs e ON e.tenant_id=t.id
                JOIN tide_staff_course_assignments a ON a.course_id=c.id AND a.tenant_id=t.id
                JOIN bartide_enhanced_members m ON m.id=a.member_id AND m.tenant_id=t.id
                WHERE t.id=@tenant AND t.status='active' AND t.vertical='bartide' AND (tide_json(e.settings_json)->'enabled') IN('true'::jsonb,'1'::jsonb)
                AND c.published=1 AND l.video_kind='upload' AND l.video_source=@video
                AND a.active=1 AND m.active=1 AND m.user_id=@user AND m.role IN('manager','bartender','server','kitchen','driver')
                """);
            if (await Scalar(db, tx, sql, ct, ("@tenant", tenant), ("@video", id), ("@user", user.UserId)) == 0) throw Missing();
        }
        await tx.CommitAsync(ct); return record;
    }

    public async Task DeleteAsync(string tenant, string kind, string id, AuthUser user, CancellationToken ct)
    {
        var table = Table(kind); if (!Guid.TryParseExact(id, "D", out _)) throw Missing();
        MediaRecord? record;
        await using (var db = await database.OpenAsync(ct))
        {
            using var tx = db.BeginTransaction(deferred: false); await Owner(db, tx, tenant, user, ct);
            record = (await Records(db, tx, tenant, kind, id, ct)).SingleOrDefault();
            if (record is null) return;
            if (await Referenced(db, tx, record, ct)) throw new MediaException("Remove this file from its menu items or lessons before deleting it.", 409, "file_referenced");
            var state = await TextScalar(db, tx, "SELECT state FROM tide_media_operations WHERE tenant_id=@tenant AND media_id=@id", ct, ("@tenant", tenant), ("@id", id));
            if (record.Status == "pending" && state != "cleanup") throw new MediaException("This upload is still finishing. Wait before removing it.", 409, "upload_pending");
            await Run(db, tx, $"UPDATE {table} SET status='deleting' WHERE id=@id AND tenant_id=@tenant", ct, ("@id", id), ("@tenant", tenant));
            await Run(db, tx, "UPDATE tide_media_operations SET state='cleanup',updated_at=@now WHERE media_id=@id AND tenant_id=@tenant", ct, ("@id", id), ("@tenant", tenant), ("@now", Now()));
            await tx.CommitAsync(ct);
        }
        await objects.DeleteAsync(record.ObjectKey, ct);
        await FinishDeleteAsync(tenant, kind, id, ct);
    }

    private async Task FinishDeleteAsync(string tenant, string kind, string id, CancellationToken ct)
    {
        await using (var db = await database.OpenAsync(ct))
        {
            using var tx = db.BeginTransaction(deferred: false);
            await Run(db, tx, $"DELETE FROM {Table(kind)} WHERE id=@id AND tenant_id=@tenant AND status='deleting'", ct, ("@id", id), ("@tenant", tenant));
            await Run(db, tx, "UPDATE tide_media_operations SET state='removed',updated_at=@now WHERE media_id=@id AND tenant_id=@tenant", ct, ("@id", id), ("@tenant", tenant), ("@now", Now()));
            await tx.CommitAsync(ct);
        }
    }

    private string SpoolDirectory()
    {
        var db = Path.GetFullPath(configuration["Storage:DatabasePath"] ?? "App_Data/tide-casa.db", environment.ContentRootPath);
        var fallback = database.IsPostgreSql ? Path.Combine(Path.GetTempPath(), "bartide", "media-spool")
            : Path.Combine(Path.GetDirectoryName(db)!, "media-spool");
        var path = Path.GetFullPath(configuration["Media:SpoolPath"] ?? fallback, environment.ContentRootPath);
        var webRoot = Path.GetFullPath(Path.Combine(environment.ContentRootPath, "wwwroot")).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        if ((path + Path.DirectorySeparatorChar).StartsWith(webRoot, StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException("Media spool storage cannot be public static content.");
        Directory.CreateDirectory(path); return path;
    }

    private static void ValidateContainer(string path, string type)
    {
        using var stream = File.OpenRead(path); var head = new byte[Math.Min(80, (int)stream.Length)]; stream.ReadExactly(head);
        if (type == "application/pdf")
        {
            var tail = new byte[Math.Min(2048, (int)stream.Length)]; stream.Seek(-tail.Length, SeekOrigin.End); stream.ReadExactly(tail);
            if (head.Length < 5 || !head.AsSpan(0, 5).SequenceEqual("%PDF-"u8) || !Encoding.ASCII.GetString(tail).Contains("%%EOF", StringComparison.Ordinal)) throw new MediaException("This file does not look like a complete PDF.");
        }
        else
        {
            if (head.Length < 24 || !head.AsSpan(4, 4).SequenceEqual("ftyp"u8)) throw new MediaException("Choose a supported MP4 video.");
            var box = System.Buffers.Binary.BinaryPrimitives.ReadUInt32BigEndian(head);
            if (box < 16 || box > stream.Length) throw new MediaException("Choose a complete MP4 video.");
            var brands = Encoding.ASCII.GetString(head, 8, (int)Math.Min(box, head.Length) - 8);
            if (!new[] { "isom", "iso2", "mp41", "mp42", "avc1", "M4V ", "MSNV", "dash", "cmfc", "cmfs" }.Any(brands.Contains)) throw new MediaException("Choose a supported MP4 video.");
        }
    }

    private static string Filename(string raw, string type)
    {
        if (raw.Length > 512) throw new MediaException("Use a shorter file name.");
        var name = new string(raw.Select(c => char.IsControl(c) || c is '/' or '\\' or '"' ? '-' : c).ToArray()).Trim();
        var suffix = type == "application/pdf" ? ".pdf" : type == "video/mp4" ? ".mp4" : ".jpg";
        var dot = name.LastIndexOf('.'); if (dot > 0) name = name[..dot];
        if (name.Length == 0) name = "Upload"; return name[..Math.Min(name.Length, 120)] + suffix;
    }
    private static MediaFile Public(MediaRecord record, bool referenced) => new(record.Id, record.Kind, record.Name, record.ContentType, record.ByteSize, record.Status, referenced, record.CreatedAt);
    private static void CheckId(string id) { if (!Regex.IsMatch(id, "^[A-Za-z0-9_-]{1,128}$")) throw Missing(); }
    private static async Task<string> Owner(DbConnection db, DbTransaction tx, string tenant, AuthUser user, CancellationToken ct)
    {
        if (!await CanManage(db, tx, tenant, user, ct)) throw new MediaException("This file workspace is not available to this account.", 403, "media_forbidden");
        return (await TextScalar(db, tx, "SELECT name FROM bartide_customers WHERE id=@tenant", ct, ("@tenant", tenant)))!;
    }
    private static async Task<bool> CanManage(DbConnection db, DbTransaction tx, string tenant, AuthUser user, CancellationToken ct)
    {
        CheckId(tenant);
        return !string.IsNullOrEmpty(user.UserId) && await Scalar(db, tx,
            "SELECT COUNT(*) FROM bartide_customers WHERE id=@tenant AND (@platform=1 OR (user_id=@user AND status IN('draft','building','active')))", ct,
            ("@tenant", tenant), ("@platform", user.IsPlatformOwner ? 1 : 0), ("@user", user.UserId)) == 1
            || await TenantStaffAccess.IsManagerAsync(db, tx, tenant, user, ct, allowPreparation: true);
    }
    private static async Task<List<MediaRecord>> Records(DbConnection db, DbTransaction tx, string tenant, string kind, string? id, CancellationToken ct)
    {
        var fields = kind switch { "photo" => "'Menu photo','image/jpeg'", "menu" => "name,content_type", _ => "name,'video/mp4'" };
        await using var command = Command(db, tx, $"SELECT id,tenant_id,object_key,{fields},byte_size,status,created_at FROM {Table(kind)} WHERE tenant_id=@tenant" + (id is null ? "" : " AND id=@id"), ("@tenant", tenant), ("@id", id));
        var records = new List<MediaRecord>(); await using var reader = await command.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            var file = new MediaRecord(reader.GetString(0), reader.GetString(1), kind, reader.GetString(2), reader.GetString(3), reader.GetString(4), reader.ReadInt64(5), reader.GetString(6), reader.GetString(7));
            var prefix = kind switch { "photo" => "menu-photos", "menu" => "source-menus", _ => "course-videos" };
            var expectedKey = prefix + "/" + tenant + "/" + file.Id + (kind == "photo" ? ".jpg" : kind == "video" ? ".mp4" : "");
            if (!Guid.TryParseExact(file.Id, "D", out _) || file.ObjectKey != expectedKey || file.ByteSize <= 0 || file.ByteSize > Limit(kind)) throw Missing();
            records.Add(file);
        }
        return records;
    }
    private static async Task<bool> Referenced(DbConnection db, DbTransaction tx, MediaRecord file, CancellationToken ct, bool publicAccess = false)
    {
        if (file.Kind == "video") return await Scalar(db, tx, "SELECT COUNT(*) FROM fit_lessons WHERE tenant_id=@tenant AND video_kind='upload' AND video_source=@id", ct, ("@tenant", file.TenantId), ("@id", file.Id)) > 0;
        if (file.Kind != "photo") return false;
        var json = await TextScalar(db, tx, "SELECT menu_json FROM bartide_customers WHERE id=@tenant", ct, ("@tenant", file.TenantId));
        using var menu = JsonDocument.Parse(json ?? "{}");
        if (menu.RootElement.TryGetProperty("items", out var items) && items.ValueKind == JsonValueKind.Array && items.EnumerateArray().Any(item => item.ValueKind == JsonValueKind.Object && item.TryGetProperty("photo_src", out var source) && source.ValueKind == JsonValueKind.String && source.GetString() == "/media/" + file.Id)) return true;
        if (menu.RootElement.TryGetProperty("venue", out var venue) && venue.ValueKind == JsonValueKind.Object)
            foreach (var key in new[] { "logo_src", "cover_src" })
                if (venue.TryGetProperty(key, out var source) && source.ValueKind == JsonValueKind.String && source.GetString() == "/media/" + file.Id) return true;
        if (publicAccess) return false;
        // Retain assets used by the current/previous private release; public delivery uses only current public references.
        var settings = await TextScalar(db, tx, "SELECT settings_json FROM bartide_enhanced_configs WHERE tenant_id=@tenant", ct, ("@tenant", file.TenantId));
        using var config = JsonDocument.Parse(settings ?? "{}");
        return config.RootElement.TryGetProperty("onboarding", out var onboarding) && onboarding.GetRawText().Contains("\"" + file.Id + "\"", StringComparison.Ordinal);
    }
    private static DbCommand Command(DbConnection db, DbTransaction? tx, string sql, params (string Name, object? Value)[] parameters)
    { var command = db.CreateCommand(); command.Transaction = tx; command.CommandText = sql; foreach (var (name, value) in parameters) command.Parameters.AddWithValue(name, value ?? DBNull.Value); return command; }
    private static async Task<int> Run(DbConnection db, DbTransaction? tx, string sql, CancellationToken ct, params (string, object?)[] parameters)
    { await using var command = Command(db, tx, sql, parameters); return await command.ExecuteNonQueryAsync(ct); }
    private static async Task<long> Scalar(DbConnection db, DbTransaction tx, string sql, CancellationToken ct, params (string, object?)[] parameters)
    { await using var command = Command(db, tx, sql, parameters); return Convert.ToInt64(await command.ExecuteScalarAsync(ct)); }
    private static async Task<string?> TextScalar(DbConnection db, DbTransaction tx, string sql, CancellationToken ct, params (string, object?)[] parameters)
    { await using var command = Command(db, tx, sql, parameters); var value = await command.ExecuteScalarAsync(ct); return value is null or DBNull ? null : Convert.ToString(value); }
}
