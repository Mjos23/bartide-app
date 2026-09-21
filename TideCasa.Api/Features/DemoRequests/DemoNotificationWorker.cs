using System.Data.Common;
using TideCasa.Api.Infrastructure;
namespace TideCasa.Api.Features.DemoRequests;

public sealed class DemoNotificationWorker(ApplicationDatabase database, DemoNotificationSender sender, IConfiguration configuration,
    IHostEnvironment environment, ILogger<DemoNotificationWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                if (sender.Ready) await DispatchOneAsync(ct);
                var interval = environment.IsDevelopment() && configuration["Notifications:Mode"] == "local-test" ? 250 : 15000;
                await Task.Delay(interval, ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { break; }
            catch (Exception) { logger.LogWarning("Demo notification delivery will retry; check the owner inbox for status."); await Task.Delay(15000, ct); }
        }
    }
    private async Task DispatchOneAsync(CancellationToken ct)
    {
        string id, payload; int attempts; var now = DateTimeOffset.UtcNow; var lease = Guid.NewGuid().ToString("D");
        await using (var db = await database.OpenAsync(ct))
        {
            using var tx = db.BeginTransaction(deferred: false);
            await using var query = db.CreateCommand(); query.Transaction = tx;
            query.CommandText = "SELECT request_id,attempts,first_attempt_at,payload_json FROM tide_demo_notifications WHERE (state='pending' AND next_attempt_at<=@now) OR (state='sending' AND lease_until<@now) ORDER BY CASE WHEN first_attempt_at IS NULL THEN 1 ELSE 0 END,created_at LIMIT 1";
            query.Parameters.AddWithValue("@now", now.ToString("O"));
            string? first;
            await using (var reader = await query.ExecuteReaderAsync(ct))
            {
                if (!await reader.ReadAsync(ct)) return;
                id = reader.GetString(0); attempts = reader.ReadInt32(1); first = reader.IsDBNull(2) ? null : reader.GetString(2);
                payload = reader.IsDBNull(3) ? sender.Payload(id) : reader.GetString(3);
            }
            if (attempts >= 12 || first is not null && (!DateTimeOffset.TryParse(first, out var began) || began < now.AddHours(-20)))
            {
                await Change(db, tx, "UPDATE tide_demo_notifications SET state='review',error_code='delivery_needs_review',updated_at=@now,lease_id=NULL,lease_until=NULL WHERE request_id=@id", id, now, ct);
                await Change(db, tx, "UPDATE demo_requests SET notification_state='review' WHERE id=@id AND @now IS NOT NULL", id, now, ct);
                await tx.CommitAsync(ct); return;
            }
            if (first is null)
            {
                await using var quota = db.CreateCommand(); quota.Transaction = tx;
                quota.CommandText = "SELECT COUNT(*) FROM tide_demo_notifications WHERE first_attempt_at >= @day"; quota.Parameters.AddWithValue("@day", now.Date.ToString("yyyy-MM-dd"));
                var limit = int.TryParse(configuration["Notifications:MaxAlertsPerDay"], out var maximum) ? Math.Clamp(maximum, 1, 40) : 40;
                if (Convert.ToInt64(await quota.ExecuteScalarAsync(ct)) >= limit) return;
            }
            await using var claim = db.CreateCommand(); claim.Transaction = tx;
            claim.CommandText = "UPDATE tide_demo_notifications SET state='sending',attempts=attempts+1,first_attempt_at=COALESCE(first_attempt_at,@now),payload_json=COALESCE(payload_json,@payload),lease_id=@lease,lease_until=@until,updated_at=@now WHERE request_id=@id";
            claim.Parameters.AddWithValue("@now", now.ToString("O")); claim.Parameters.AddWithValue("@payload", payload); claim.Parameters.AddWithValue("@lease", lease); claim.Parameters.AddWithValue("@until", now.AddMinutes(2).ToString("O")); claim.Parameters.AddWithValue("@id", id);
            await claim.ExecuteNonQueryAsync(ct); await tx.CommitAsync(ct);
        }
        var result = await sender.SendAsync(id, payload, ct);
        await using (var db = await database.OpenAsync(ct))
        {
            using var tx = db.BeginTransaction(deferred: false);
            await using var save = db.CreateCommand(); save.Transaction = tx;
            save.CommandText = "UPDATE tide_demo_notifications SET state=@state,provider_id=@provider,error_code=@error,next_attempt_at=@next,updated_at=@now,lease_id=NULL,lease_until=NULL WHERE request_id=@id AND lease_id=@lease AND state='sending'";
            var state = result.ProviderId is not null ? "sent" : result.Retryable ? "pending" : "review";
            save.Parameters.AddWithValue("@state", state); save.Parameters.AddWithValue("@provider", (object?)result.ProviderId ?? DBNull.Value); save.Parameters.AddWithValue("@error", (object?)result.Code ?? DBNull.Value);
            var delay = environment.IsDevelopment() && configuration["Notifications:Mode"] == "local-test" ? 1 : Math.Min(3600, 15 * Math.Pow(2, attempts));
            save.Parameters.AddWithValue("@next", DateTimeOffset.UtcNow.AddSeconds(delay).ToString("O")); save.Parameters.AddWithValue("@now", DateTimeOffset.UtcNow.ToString("O")); save.Parameters.AddWithValue("@id", id); save.Parameters.AddWithValue("@lease", lease);
            var changed = await save.ExecuteNonQueryAsync(ct);
            if (changed == 1)
            {
                await using var reflect = db.CreateCommand(); reflect.Transaction = tx;
                reflect.CommandText = "UPDATE demo_requests SET notification_state=@state WHERE id=@id"; reflect.Parameters.AddWithValue("@state", state); reflect.Parameters.AddWithValue("@id", id); await reflect.ExecuteNonQueryAsync(ct);
            }
            await tx.CommitAsync(ct);
        }
    }
    private static async Task Change(DbConnection db, DbTransaction tx, string sql, string id, DateTimeOffset now, CancellationToken ct)
    { await using var command = db.CreateCommand(); command.Transaction = tx; command.CommandText = sql; command.Parameters.AddWithValue("@id", id); command.Parameters.AddWithValue("@now", now.ToString("O")); await command.ExecuteNonQueryAsync(ct); }
}
