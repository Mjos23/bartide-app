using System.Data.Common;
using System.Security.Cryptography;
using TideCasa.Api.Infrastructure;
using TideCasa.Contracts;

namespace TideCasa.Api.Features.EmailTracking;

public sealed class EmailTrackingFailure(string message, int status = 400) : Exception(message)
{
    public int Status { get; } = status;
}

public sealed class EmailTrackingStore(ApplicationDatabase database)
{
    public async Task InitializeAsync(CancellationToken ct = default)
    {
        await using var db = await database.OpenAsync(ct); using var tx = db.BeginTransaction(deferred: false);
        await Execute(db, tx, "INSERT INTO tide_email_campaigns(id,name,is_test,link_token,created_at) VALUES(@id,@name,1,@token,@now) ON CONFLICT(id) DO NOTHING", ct,
            ("@id", "bartide-test-20260923"), ("@name", "BarTide email verification — TEST"), ("@token", EmailCampaignDefaults.TestToken), ("@now", Now()));
        await Prune(db, tx, ct); await tx.CommitAsync(ct);
    }

    public async Task<EmailVisit> StartAsync(StartEmailVisit request, CancellationToken ct)
    {
        if (request is null || !Hex(request.CampaignToken, 32)) throw new EmailTrackingFailure("Invalid campaign.");
        await using var db = await database.OpenAsync(ct); using var tx = db.BeginTransaction(deferred: false);
        var campaigns = await Rows(db, tx, "SELECT id FROM tide_email_campaigns WHERE link_token=@token", r => r.GetString(0), ct, ("@token", request.CampaignToken));
        if (campaigns.Count != 1) throw new EmailTrackingFailure("Campaign not found.", 404);
        await Prune(db, tx, ct);
        if (await Scalar(db, tx, "SELECT COUNT(*) FROM tide_email_visits", ct) >= 100000
            || await Scalar(db, tx, "SELECT COUNT(*) FROM tide_email_visits WHERE campaign_id=@id AND created_at>=@today", ct, ("@id", campaigns[0]), ("@today", DateTimeOffset.UtcNow.ToString("yyyy-MM-dd"))) >= 5000)
            throw new EmailTrackingFailure("Tracking capacity reached.", 429);
        var id = Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(24));
        await Execute(db, tx, "INSERT INTO tide_email_visits(id,campaign_id,created_at,expires_at) VALUES(@id,@campaign,@now,@expires)", ct,
            ("@id", id), ("@campaign", campaigns[0]), ("@now", Now()), ("@expires", DateTimeOffset.UtcNow.AddMinutes(30).ToString("O")));
        await tx.CommitAsync(ct); return new(id);
    }

    public async Task<EmailClickReceipt> ClickAsync(RecordEmailClick request, CancellationToken ct)
    {
        if (request is null || !Hex(request.SessionId, 48) || request.Path is null || !EmailCampaignDefaults.Paths.ContainsKey(request.Path))
            throw new EmailTrackingFailure("Invalid click.");
        await using var db = await database.OpenAsync(ct); using var tx = db.BeginTransaction(deferred: false);
        if (await Scalar(db, tx, "SELECT COUNT(*) FROM tide_email_visits WHERE id=@id AND expires_at>@now", ct, ("@id", request.SessionId), ("@now", Now())) != 1)
            throw new EmailTrackingFailure("Visit expired.", 404);
        var prior = await Rows(db, tx, "SELECT sequence,path FROM tide_email_clicks WHERE visit_id=@id ORDER BY sequence DESC LIMIT 1", r => (Number: r.ReadInt32(0), Path: r.GetString(1)), ct, ("@id", request.SessionId));
        var sequence = prior.Count == 0 ? 1 : prior[0].Number + 1;
        // Deduplicate retries/double clicks and bound each anonymous journey.
        if (sequence > 20 || prior.Count > 0 && prior[0].Path == request.Path) { await tx.CommitAsync(ct); return new(false); }
        await Execute(db, tx, "INSERT INTO tide_email_clicks(visit_id,sequence,path,created_at) VALUES(@id,@sequence,@path,@now)", ct,
            ("@id", request.SessionId), ("@sequence", sequence), ("@path", request.Path), ("@now", Now()));
        await tx.CommitAsync(ct); return new(true);
    }

    public async Task<EmailCampaign> CreateAsync(AuthUser user, CreateEmailCampaign request, CancellationToken ct)
    {
        Owner(user);
        if (request is null || !Guid.TryParseExact(request.RequestKey, "D", out _) || string.IsNullOrWhiteSpace(request.Name)
            || request.Name.Length > 100 || request.Name.Any(char.IsControl)) throw new EmailTrackingFailure("Enter a campaign name of 1–100 characters.");
        await using var db = await database.OpenAsync(ct); using var tx = db.BeginTransaction(deferred: false);
        var existing = await Rows(db, tx, "SELECT id,name,is_test,link_token,created_at FROM tide_email_campaigns WHERE id=@id", ReadCampaign, ct, ("@id", request.RequestKey));
        if (existing.Count == 1)
        {
            if (existing[0].Name != request.Name.Trim() || existing[0].IsTest != request.IsTest) throw new EmailTrackingFailure("Refresh the campaign form before saving.", 409);
            await tx.CommitAsync(ct); return existing[0];
        }
        if (await Scalar(db, tx, "SELECT COUNT(*) FROM tide_email_campaigns", ct) >= 100) throw new EmailTrackingFailure("Campaign capacity reached.", 409);
        var campaign = new EmailCampaign(request.RequestKey, request.Name.Trim(), request.IsTest, Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(16)), Now(), 0, 0, 0, 0, 0);
        await Execute(db, tx, "INSERT INTO tide_email_campaigns(id,name,is_test,link_token,created_at) VALUES(@id,@name,@test,@token,@now)", ct,
            ("@id", campaign.Id), ("@name", campaign.Name), ("@test", campaign.IsTest ? 1 : 0), ("@token", campaign.LinkToken), ("@now", campaign.CreatedAt));
        await tx.CommitAsync(ct); return campaign;
    }

    public async Task<EmailCampaignReport> ReportAsync(AuthUser user, bool tests, string? selected, CancellationToken ct)
    {
        Owner(user);
        if (selected?.Length > 100) throw new EmailTrackingFailure("Invalid campaign.");
        await using var db = await database.OpenAsync(ct); using var tx = db.BeginTransaction(deferred: true);
        var cutoff = DateTimeOffset.UtcNow.AddDays(-90).ToString("O");
        var campaigns = await Rows(db, tx, """
            SELECT c.id,c.name,c.is_test,c.link_token,c.created_at,
            (SELECT COUNT(*) FROM tide_email_visits v WHERE v.campaign_id=c.id AND v.created_at>=@cutoff),
            (SELECT COUNT(DISTINCT v.id) FROM tide_email_visits v JOIN tide_email_clicks e ON e.visit_id=v.id WHERE v.campaign_id=c.id AND v.created_at>=@cutoff AND e.path='demo'),
            (SELECT COUNT(DISTINCT v.id) FROM tide_email_visits v JOIN tide_email_clicks e ON e.visit_id=v.id WHERE v.campaign_id=c.id AND v.created_at>=@cutoff AND e.path='purchase'),
            (SELECT COUNT(DISTINCT v.id) FROM tide_email_visits v JOIN tide_email_clicks e ON e.visit_id=v.id WHERE v.campaign_id=c.id AND v.created_at>=@cutoff AND e.path='contact'),
            (SELECT COUNT(DISTINCT v.id) FROM tide_email_visits v JOIN tide_email_clicks e ON e.visit_id=v.id WHERE v.campaign_id=c.id AND v.created_at>=@cutoff AND e.path='sample')
            FROM tide_email_campaigns c WHERE c.is_test=@test ORDER BY c.created_at DESC,c.id LIMIT 100
            """, r => ReadCampaign(r) with { Visits = r.ReadInt32(5), DemoClicks = r.ReadInt32(6), PurchaseClicks = r.ReadInt32(7), ContactClicks = r.ReadInt32(8), SampleClicks = r.ReadInt32(9) }, ct,
            ("@test", tests ? 1 : 0), ("@cutoff", cutoff));
        var active = campaigns.FirstOrDefault(c => c.Id == selected) ?? campaigns.FirstOrDefault();
        List<EmailPathway> pathways = [];
        if (active is not null)
        {
            var rows = await Rows(db, tx, "SELECT v.id,e.path FROM (SELECT id FROM tide_email_visits WHERE campaign_id=@id AND created_at>=@cutoff ORDER BY created_at DESC,id LIMIT 1000) v LEFT JOIN tide_email_clicks e ON e.visit_id=v.id ORDER BY v.id,e.sequence", r => (Id: r.GetString(0), Path: r.IsDBNull(1) ? null : r.GetString(1)), ct, ("@id", active.Id), ("@cutoff", cutoff));
            pathways = rows.GroupBy(r => r.Id).Select(g => "Email → Home" + string.Concat(g.Where(x => x.Path is not null).Select(x => " → " + EmailCampaignDefaults.Paths.GetValueOrDefault(x.Path!, "Other"))))
                .GroupBy(path => path).Select(g => new EmailPathway(g.Key, g.Count())).OrderByDescending(p => p.Visits).ThenBy(p => p.Path).Take(20).ToList();
        }
        await tx.CommitAsync(ct); return new(campaigns, pathways, active?.Id);
    }

    private static EmailCampaign ReadCampaign(DbDataReader r) => new(r.GetString(0), r.GetString(1), r.ReadBoolean(2), r.GetString(3), r.GetString(4), 0, 0, 0, 0, 0);
    public static bool Hex(string? value, int length) => value?.Length == length && value.All(c => c is >= '0' and <= '9' or >= 'a' and <= 'f');
    private static void Owner(AuthUser user) { if (!user.IsPlatformOwner) throw new EmailTrackingFailure("This report requires Tide Casa owner access.", 403); }
    private static string Now() => DateTimeOffset.UtcNow.ToString("O");
    private static Task<int> Prune(DbConnection db, DbTransaction tx, CancellationToken ct) => Execute(db, tx, "DELETE FROM tide_email_visits WHERE created_at<@cutoff", ct, ("@cutoff", DateTimeOffset.UtcNow.AddDays(-90).ToString("O")));
    private static async Task<List<T>> Rows<T>(DbConnection db, DbTransaction tx, string sql, Func<DbDataReader,T> read, CancellationToken ct, params (string,object?)[] values)
    {
        await using var command = Command(db, tx, sql, values); await using var reader = await command.ExecuteReaderAsync(ct);
        List<T> rows = []; while (await reader.ReadAsync(ct)) rows.Add(read(reader)); return rows;
    }
    private static async Task<long> Scalar(DbConnection db, DbTransaction tx, string sql, CancellationToken ct, params (string,object?)[] values)
    { await using var command = Command(db, tx, sql, values); return Convert.ToInt64(await command.ExecuteScalarAsync(ct)); }
    private static async Task<int> Execute(DbConnection db, DbTransaction tx, string sql, CancellationToken ct, params (string,object?)[] values)
    { await using var command = Command(db, tx, sql, values); return await command.ExecuteNonQueryAsync(ct); }
    private static DbCommand Command(DbConnection db, DbTransaction tx, string sql, (string,object?)[] values)
    { var command = db.CreateCommand(); command.Transaction = tx; command.CommandText = sql; foreach (var (key,value) in values) command.Parameters.AddWithValue(key, value ?? DBNull.Value); return command; }
}
