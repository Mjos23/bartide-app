using System.Text.Json;
using System.Data.Common;
using TideCasa.Api.Infrastructure;
using TideCasa.Contracts;
namespace TideCasa.Api.Features.DemoRequests;

public sealed class DemoInboxStore(ApplicationDatabase database, DemoNotificationSender sender)
{
    public async Task<DemoInbox> ReadAsync(AuthUser user, CancellationToken ct)
    {
        if (!user.IsPlatformOwner) throw new UnauthorizedAccessException();
        await using var db = await database.OpenAsync(ct);
        await using var query = db.CreateCommand();
        query.CommandText = "SELECT d.request_json,d.status,COALESCE(i.version,0),COALESCE(i.note,''),COALESCE(n.state,'not_queued'),d.created_at FROM demo_requests d LEFT JOIN tide_demo_inbox i ON i.request_id=d.id LEFT JOIN tide_demo_notifications n ON n.request_id=d.id ORDER BY CASE WHEN d.status='closed' THEN 1 ELSE 0 END,d.created_at DESC LIMIT 200";
        await using var reader = await query.ExecuteReaderAsync(ct);
        var rows = new List<DemoInquiry>();
        while (await reader.ReadAsync(ct))
        {
            var request = JsonSerializer.Deserialize<DemoRequest>(reader.GetString(0));
            if (request is not null) rows.Add(new(request, reader.GetString(1), reader.ReadInt32(2), reader.GetString(3), reader.GetString(4), reader.GetString(5)));
        }
        return new(rows, sender.Ready);
    }
    public async Task<bool> UpdateAsync(string id, AuthUser user, UpdateDemoInquiryRequest request, CancellationToken ct)
    {
        if (!user.IsPlatformOwner) throw new UnauthorizedAccessException();
        if (!Guid.TryParseExact(id, "D", out _) || request is null || request.ExpectedVersion < 0 || request.Status is not ("requested" or "contacted" or "scheduled" or "closed")
            || request.Note is null || request.Note.Length > 2000 || request.Note.Any(c => char.IsControl(c) && c is not ('\r' or '\n' or '\t')))
            throw new ArgumentException("Check the request status and notes.");
        await using var db = await database.OpenAsync(ct); using var tx = db.BeginTransaction(deferred: false);
        await using var query = db.CreateCommand(); query.Transaction = tx;
        query.CommandText = "SELECT COALESCE(i.version,0) FROM demo_requests d LEFT JOIN tide_demo_inbox i ON i.request_id=d.id WHERE d.id=@id"; query.Parameters.AddWithValue("@id", id);
        var version = await query.ExecuteScalarAsync(ct); if (version is null || Convert.ToInt32(version) != request.ExpectedVersion) return false;
        await using var save = db.CreateCommand(); save.Transaction = tx;
        save.CommandText = "UPDATE demo_requests SET status=@status WHERE id=@id; INSERT INTO tide_demo_inbox(request_id,version,note,updated_at,updated_by) VALUES(@id,1,@note,@now,@actor) ON CONFLICT(request_id) DO UPDATE SET version=tide_demo_inbox.version+1,note=@note,updated_at=@now,updated_by=@actor";
        save.Parameters.AddWithValue("@id", id); save.Parameters.AddWithValue("@status", request.Status); save.Parameters.AddWithValue("@note", request.Note.Trim()); save.Parameters.AddWithValue("@now", DateTimeOffset.UtcNow.ToString("O")); save.Parameters.AddWithValue("@actor", user.UserId);
        await save.ExecuteNonQueryAsync(ct); await tx.CommitAsync(ct); return true;
    }
}
