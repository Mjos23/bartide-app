using System.Text.RegularExpressions;
using System.Data.Common;
using TideCasa.Api.Infrastructure;
using TideCasa.Contracts;

namespace TideCasa.Api.Features.Referrals;

public sealed class ReferralFailure(string message, int status = 400) : Exception(message)
{
    public int Status { get; } = status;
}

public sealed class ReferralStore(ApplicationDatabase database)
{
    public const string TermsVersion = "2026-09-19";
    private const string Columns = "id,name,introduction,status,code,discount_percent,terms_version,terms_accepted_at,created_at,updated_at";

    public async Task<ReferralDashboard> ReadAsync(AuthUser user, CancellationToken ct)
    {
        await using var db = await database.OpenAsync(ct);
        using var tx = db.BeginTransaction(deferred: true);
        var profile = (await Rows(db, tx, $"SELECT {Columns} FROM tide_referral_profiles WHERE user_id=@user", Profile, ct, ("@user", user.UserId))).SingleOrDefault();
        IReadOnlyList<ReferralSale> sales = [];
        ReferralTotals totals = new(0, 0, 0);
        if (profile is not null)
        {
            sales = await Rows(db, tx, "SELECT kind,gross_cents,refunded_cents,commission_cents,paid_at FROM tide_referral_sales WHERE profile_id=@id AND environment='live' ORDER BY paid_at DESC,event_id DESC LIMIT 100", r => new ReferralSale(r.GetString(0), r.ReadInt64(1), r.ReadInt64(2), r.ReadInt64(3), r.GetString(4)), ct, ("@id", profile.Id));
            totals = (await Rows(db, tx, "SELECT COUNT(*),COALESCE(SUM(gross_cents-refunded_cents),0),COALESCE(SUM(commission_cents),0) FROM tide_referral_sales WHERE profile_id=@id AND environment='live'", r => new ReferralTotals(r.ReadInt64(0), r.ReadInt64(1), r.ReadInt64(2)), ct, ("@id", profile.Id))).Single();
        }
        IReadOnlyList<ReferralApplication> applications = [];
        if (user.IsPlatformOwner)
            applications = await Rows(db, tx, $"SELECT {string.Join(',', Columns.Split(',').Select(c => "p." + c))},p.email,p.review_token,COALESCE(s.net,0),COALESCE(s.commission,0) FROM tide_referral_profiles p LEFT JOIN (SELECT profile_id,SUM(gross_cents-refunded_cents) net,SUM(commission_cents) commission FROM tide_referral_sales WHERE environment='live' GROUP BY profile_id) s ON s.profile_id=p.id ORDER BY CASE p.status WHEN 'pending' THEN 0 ELSE 1 END,p.created_at DESC,p.id LIMIT 200", r => new ReferralApplication(Profile(r), r.GetString(10), r.GetString(11), r.ReadInt64(12), r.ReadInt64(13)), ct);
        await tx.CommitAsync(ct);
        return new(user.IsPlatformOwner, profile, sales, totals, applications, TermsVersion);
    }

    public async Task ApplyAsync(AuthUser user, ApplyReferralRequest request, CancellationToken ct)
    {
        if (request is null || !request.AcceptedTerms || request.TermsVersion != TermsVersion) throw new ReferralFailure("Accept the current commission terms before applying.");
        var name = Text(request.Name, 1, 100);
        var introduction = Text(request.Introduction, 20, 1500);
        await using var db = await database.OpenAsync(ct); using var tx = db.BeginTransaction(deferred: false);
        var existing = (await Rows(db, tx, $"SELECT {Columns} FROM tide_referral_profiles WHERE user_id=@user", Profile, ct, ("@user", user.UserId))).SingleOrDefault();
        if (existing is not null)
        {
            if (existing.Name != name || existing.Introduction != introduction || existing.TermsVersion != request.TermsVersion)
                throw new ReferralFailure("Your application is already saved. Open your workspace to check its status.", 409);
            return; // An exact repeat cannot create a second profile or reset an owner review.
        }
        var now = DateTimeOffset.UtcNow.ToString("O");
        try
        {
            await Execute(db, tx, "INSERT INTO tide_referral_profiles(id,user_id,email,name,introduction,status,discount_percent,terms_version,terms_accepted_at,created_at,updated_at) VALUES(@id,@user,@email,@name,@intro,'pending',0,@terms,@now,@now,@now)", ct,
                ("@id", Guid.NewGuid().ToString()), ("@user", user.UserId), ("@email", user.Email.Trim().ToLowerInvariant()), ("@name", name), ("@intro", introduction), ("@terms", TermsVersion), ("@now", now));
            await tx.CommitAsync(ct);
        }
        catch (DbException e) when (DatabaseExtensions.IsConstraintViolation(e)) { throw new ReferralFailure("An application cannot be added for this account. Contact Tide Casa to review account access.", 409); }
    }

    public async Task ReviewAsync(string id, AuthUser user, ReviewReferralRequest request, CancellationToken ct)
    {
        if (!user.IsPlatformOwner) throw new ReferralFailure("Only the Tide Casa owner can review applications.", 403);
        if (!Guid.TryParseExact(id, "D", out _) || request is null || request.ExpectedReviewToken is null || request.ExpectedReviewToken.Length > 100
            || request.Status is not ("pending" or "active" or "paused" or "declined") || request.DiscountPercent is < 0 or > 100)
            throw new ReferralFailure("Choose a valid status and an initial-sale discount from 0% to 100%.");
        var code = request.Code?.Trim().ToUpperInvariant(); if (code == "") code = null;
        if (code is not null && !Regex.IsMatch(code, "^[A-Z0-9][A-Z0-9-]{2,31}$", RegexOptions.CultureInvariant) || request.Status == "active" && code is null)
            throw new ReferralFailure("Choose a 3–32 character letter, number or hyphen referral code.");
        await using var db = await database.OpenAsync(ct); using var tx = db.BeginTransaction(deferred: false);
        var existing = (await Rows(db, tx, "SELECT code,review_token FROM tide_referral_profiles WHERE id=@id", r => new { Code = r.IsDBNull(0) ? null : r.GetString(0), Token = r.GetString(1) }, ct, ("@id", id))).SingleOrDefault();
        if (existing is null) throw new ReferralFailure("Application not found.", 404);
        if (existing.Token != request.ExpectedReviewToken) throw new ReferralFailure("This profile changed. Refresh before reviewing it again.", 409);
        if (existing.Code is not null && existing.Code != code) throw new ReferralFailure("An assigned code stays with its profile. Pause it to stop new referrals.", 409);
        var now = DateTimeOffset.UtcNow.ToString("O"); var token = Guid.NewGuid().ToString();
        try
        {
            var changed = await Execute(db, tx, "UPDATE tide_referral_profiles SET status=@status,code=@code,discount_percent=@discount,review_token=@token,updated_at=@now WHERE id=@id AND review_token=@expected", ct,
                ("@status", request.Status), ("@code", code), ("@discount", request.DiscountPercent), ("@token", token), ("@now", now), ("@id", id), ("@expected", request.ExpectedReviewToken));
            if (changed != 1) throw new ReferralFailure("This profile changed. Refresh before reviewing it again.", 409);
            await Execute(db, tx, "INSERT INTO tide_referral_reviews(id,profile_id,reviewer_id,status,code,discount_percent,created_at) VALUES(@id,@profile,@actor,@status,@code,@discount,@now)", ct,
                ("@id", token), ("@profile", id), ("@actor", user.UserId), ("@status", request.Status), ("@code", code), ("@discount", request.DiscountPercent), ("@now", now));
            await tx.CommitAsync(ct);
        }
        catch (DbException e) when (DatabaseExtensions.IsConstraintViolation(e)) { throw new ReferralFailure("That code is unavailable. Refresh and choose another code.", 409); }
    }

    private static string Text(string? value, int min, int max)
    {
        var text = value?.Trim();
        if (text is null || text.Length < min || text.Length > max || text.Any(c => char.IsControl(c) && c is not ('\r' or '\n' or '\t')))
            throw new ReferralFailure("Add your name and 20–1,500 characters about your interest in becoming a sales engineer.");
        return text;
    }
    private static ReferralProfile Profile(DbDataReader r) => new(r.GetString(0), r.GetString(1), r.GetString(2), r.GetString(3), r.IsDBNull(4) ? null : r.GetString(4), r.ReadInt32(5), r.GetString(6), r.GetString(7), r.GetString(8), r.GetString(9));
    private static async Task<List<T>> Rows<T>(DbConnection db, DbTransaction tx, string sql, Func<DbDataReader,T> read, CancellationToken ct, params (string, object?)[] values)
    {
        await using var command = db.CreateCommand(); command.Transaction = tx; command.CommandText = sql;
        foreach (var (key,value) in values) command.Parameters.AddWithValue(key,value ?? DBNull.Value);
        await using var reader = await command.ExecuteReaderAsync(ct); var rows = new List<T>();
        while (await reader.ReadAsync(ct)) rows.Add(read(reader)); return rows;
    }
    private static async Task<int> Execute(DbConnection db, DbTransaction tx, string sql, CancellationToken ct, params (string, object?)[] values)
    {
        await using var command = db.CreateCommand(); command.Transaction = tx; command.CommandText = sql;
        foreach (var (key,value) in values) command.Parameters.AddWithValue(key,value ?? DBNull.Value);
        return await command.ExecuteNonQueryAsync(ct);
    }
}
