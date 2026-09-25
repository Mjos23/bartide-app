using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Data.Common;
using TideCasa.Api.Infrastructure;
using TideCasa.Contracts;

namespace TideCasa.Api.Features.Rewards;

public sealed class RewardsException(string message, int status = 400, string code = "invalid_reward_change") : Exception(message)
{
    public int Status { get; } = status;
    public string Code { get; } = code;
}

/// <summary>Tenant-scoped durable reward issuance. All money/payment facts originate in storage.</summary>
public sealed partial class RewardsStore(ApplicationDatabase database, TimeProvider clock)
{
    private sealed record Business(string Id, string Slug, string Name, string Owner);
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private string Now() => clock.GetUtcNow().ToString("O");
    private static string Id() => Guid.NewGuid().ToString("D");
    private delegate Task<RewardChangeResult> Change(DbConnection db, DbTransaction tx, Business business, CancellationToken ct);

    public async Task<RewardWallet> WalletAsync(string slug, AuthUser user, CancellationToken ct)
    {
        await using var db = await database.OpenAsync(ct);
        using var tx = db.BeginTransaction(deferred: false);
        var b = await Tenant(db, tx, slug, true, ct);
        var member = await OwnMember(db, tx, b.Id, user, ct);
        if (member is not null) await Reconcile(db, tx, b.Id, member, ct);
        var rules = await Rules(db, tx, b.Id, ct, activeOnly: true);
        var progress = new List<RewardProgress>();
        if (member is not null)
            foreach (var rule in rules.Where(r => r.Kind != "points"))
            {
                var period = rule.Kind == "monthly-visits" ? Local(clock.GetUtcNow(), rule.TimeZoneId).ToString("yyyy-MM", CultureInfo.InvariantCulture) : "lifetime";
                var units = await Qualified(db, tx, member, rule.VersionId, period, ct);
                var remaining = rule.Kind == "monthly-visits" ? Math.Max(0, rule.Threshold - units) : rule.Threshold - units % rule.Threshold;
                progress.Add(new(rule.Id, rule.VersionId, units, remaining, period));
            }
        var wallet = new RewardWallet(b.Id, b.Slug, b.Name, member,
            member is null ? 0 : await PointsBalance(db, tx, b.Id, member, ct), rules, progress,
            member is null ? [] : await Issued(db, tx, b.Id, member, ct));
        await tx.CommitAsync(ct);
        return wallet;
    }

    public async Task<RewardsWorkspace> WorkspaceAsync(string id, AuthUser user, CancellationToken ct)
    {
        await using var db = await database.OpenAsync(ct);
        using var tx = db.BeginTransaction(deferred: false);
        var b = await Tenant(db, tx, id, false, ct); Manager(b, user);
        var members = await Rows(db, tx, "SELECT m.id,m.name,COALESCE(SUM(l.delta),0) FROM tide_loyalty_members m LEFT JOIN tide_loyalty_ledger l ON l.member_id=m.id AND l.tenant_id=m.tenant_id WHERE m.tenant_id=@t GROUP BY m.id ORDER BY m.name LIMIT 10000",
            r => new RewardMember(r.GetString(0), r.GetString(1), r.ReadInt64(2)), ct, ("@t", id));
        for (var index = 0; index < members.Count; index++)
        {
            var member = members[index];
            await Reconcile(db, tx, id, member.Id, ct);
            members[index] = new(member.Id, member.Name, await PointsBalance(db, tx, id, member.Id, ct));
        }
        var rewards = await Rows(db, tx, IssuedSelect + " JOIN tide_loyalty_members m ON m.id=i.member_id AND m.tenant_id=i.tenant_id WHERE i.tenant_id=@t ORDER BY CASE i.state WHEN 'requested' THEN 0 ELSE 1 END,i.earned_at DESC LIMIT 500",
            r => new ManagedCustomerReward(r.GetString(10), r.GetString(11), ReadIssued(r)), ct, ("@t", id));
        var audits = await Rows(db, tx, "SELECT id,member_id,source_kind,note,units,state,occurred_at,actor FROM tide_reward_qualifications WHERE tenant_id=@t ORDER BY occurred_at DESC LIMIT 200",
            r => new RewardAudit(r.GetString(0), r.GetString(1), r.GetString(2), r.GetString(3), r.ReadInt32(4), r.GetString(5), r.GetString(6), r.GetString(7)), ct, ("@t", id));
        var employees = await Rows(db, tx, "SELECT m.id,m.name,m.active,COALESCE(SUM(l.delta),0) FROM bartide_enhanced_members m LEFT JOIN tide_employee_reward_ledger l ON l.member_id=m.id AND l.tenant_id=m.tenant_id WHERE m.tenant_id=@t GROUP BY m.id ORDER BY m.name",
            r => new EmployeeRewardBalance(r.GetString(0), r.GetString(1), r.ReadInt32(2) == 1, r.ReadInt64(3)), ct, ("@t", id));
        var result = new RewardsWorkspace(id, b.Slug, b.Name, await Rules(db, tx, id, ct), members, rewards, audits, employees,
            await EmployeeEntries(db, tx, id, null, ct));
        await tx.CommitAsync(ct);
        return result;
    }

    public async Task<EmployeeRewardsWallet> EmployeeWalletAsync(string id, AuthUser user, CancellationToken ct)
    {
        await using var db = await database.OpenAsync(ct);
        using var tx = db.BeginTransaction(deferred: false);
        var b = await Tenant(db, tx, id, false, ct);
        // Establish the same verified-email-to-stable-ID binding used by the staff
        // workspace, so a first visit directly to this API does not depend on a UI route.
        await Run(db, tx, db.Sql("UPDATE bartide_enhanced_members SET user_id=@u WHERE tenant_id=@t AND user_id IS NULL AND email=@email COLLATE NOCASE AND active=1 AND role IN('kitchen','driver')",
            "UPDATE bartide_enhanced_members SET user_id=@u WHERE tenant_id=@t AND user_id IS NULL AND translate(email,'ABCDEFGHIJKLMNOPQRSTUVWXYZ','abcdefghijklmnopqrstuvwxyz')=translate(@email,'ABCDEFGHIJKLMNOPQRSTUVWXYZ','abcdefghijklmnopqrstuvwxyz') COLLATE \"C\" AND active=1 AND role IN('kitchen','driver')"), ct,
            ("@u", user.UserId), ("@t", id), ("@email", user.Email));
        var members = await Rows(db, tx, "SELECT id FROM bartide_enhanced_members WHERE tenant_id=@t AND user_id=@u AND active=1 AND role IN('kitchen','driver')", r => r.GetString(0), ct, ("@t", id), ("@u", user.UserId));
        if (members.Count != 1) throw Forbidden();
        var member = members[0];
        var result = new EmployeeRewardsWallet(id, b.Name, member, await Number(db, tx, "SELECT COALESCE(SUM(delta),0) FROM tide_employee_reward_ledger WHERE tenant_id=@t AND member_id=@m", ct, ("@t", id), ("@m", member)), await EmployeeEntries(db, tx, id, member, ct));
        await tx.CommitAsync(ct);
        return result;
    }

    public Task<RewardChangeResult> JoinAsync(string slug, AuthUser user, JoinRewardsRequest request, CancellationToken ct) =>
        Execute(slug, true, user, request?.RequestId, new { operation = "join", request }, false, async (db, tx, b, token) =>
        {
            Required(request); var name = Text(request!.Name, "Name", 80);
            var existing = await OwnMember(db, tx, b.Id, user, token);
            if (existing is not null) return new(existing, "You already belong to this rewards program.");
            if (await Number(db, tx, "SELECT COUNT(*) FROM tide_loyalty_members WHERE tenant_id=@t", token, ("@t", b.Id)) >= 10000)
                throw new RewardsException("This rewards program has reached its membership limit.", 409, "program_full");
            var id = Id();
            await Run(db, tx, "INSERT INTO tide_loyalty_members(id,tenant_id,user_id,email,name,joined_at,updated_at) VALUES(@id,@t,@u,@email,@name,@now,@now)", token,
                ("@id", id), ("@t", b.Id), ("@u", user.UserId), ("@email", user.Email), ("@name", name), ("@now", Now()));
            return new(id, "Your rewards membership is ready.");
        }, ct);

    public Task<RewardChangeResult> SaveRuleAsync(string tenantId, string? ruleId, AuthUser user, SaveRewardRuleRequest request, CancellationToken ct) =>
        Execute(tenantId, false, user, request?.RequestId, new { operation = "save-rule", ruleId, request }, true, async (db, tx, b, token) =>
        {
            Required(request); var title = Text(request!.Title, "Rule title", 100); var reward = Text(request.Reward, "Reward terms", 1000, multiline: true);
            var zone = Text(request.TimeZoneId, "Business timezone", 100); _ = Local(clock.GetUtcNow(), zone);
            if (request.Kind is not ("punch-card" or "monthly-visits" or "points") || request.Threshold is < 1 or > 10000 || request.Kind == "monthly-visits" && request.Threshold > 31)
                throw new RewardsException("Choose a reward type and valid threshold (monthly visits: 1–31).");
            string? item = null;
            if (request.Kind == "punch-card")
            {
                item = Text(request.ItemId, "Qualifying menu item", 64);
                var menus = await Rows(db, tx, "SELECT menu_json FROM bartide_customers WHERE id=@t", r => r.GetString(0), token, ("@t", b.Id));
                using var menu = JsonDocument.Parse(menus[0]);
                if (!menu.RootElement.TryGetProperty("items", out var items) || !items.EnumerateArray().Any(i => i.TryGetProperty("id", out var p) && p.GetString() == item))
                    throw new RewardsException("Select an item from this business's menu.");
            }
            else if (!string.IsNullOrWhiteSpace(request.ItemId)) throw new RewardsException("This reward type does not use a menu item.");
            var versionId = Id(); var now = Now(); int version;
            if (ruleId is null)
            {
                if (request.ExpectedVersion != -1) throw Stale();
                if (await Number(db, tx, "SELECT COUNT(*) FROM tide_reward_rules WHERE tenant_id=@t", token, ("@t", b.Id)) >= 50)
                    throw new RewardsException("This business supports up to 50 reward rules.", 409, "rules_full");
                ruleId = Id(); version = 0;
                await Run(db, tx, "INSERT INTO tide_reward_rules(id,tenant_id,current_version_id,version,created_at,updated_at) VALUES(@id,@t,@v,0,@now,@now)", token,
                    ("@id", ruleId), ("@t", b.Id), ("@v", versionId), ("@now", now));
            }
            else
            {
                if (request.ExpectedVersion < 0 || await Run(db, tx, "UPDATE tide_reward_rules SET current_version_id=@v,version=version+1,updated_at=@now WHERE id=@id AND tenant_id=@t AND version=@expected", token,
                    ("@v", versionId), ("@now", now), ("@id", ruleId), ("@t", b.Id), ("@expected", request.ExpectedVersion)) != 1) throw Stale();
                version = checked(request.ExpectedVersion + 1);
            }
            await Run(db, tx, "INSERT INTO tide_reward_rule_versions(id,rule_id,version,title,reward,kind,threshold,item_id,timezone_id,active,created_at) VALUES(@id,@r,@version,@title,@reward,@kind,@threshold,@item,@zone,@active,@now)", token,
                ("@id", versionId), ("@r", ruleId), ("@version", version), ("@title", title), ("@reward", reward), ("@kind", request.Kind), ("@threshold", request.Threshold), ("@item", item), ("@zone", zone), ("@active", request.Active ? 1 : 0), ("@now", now));
            return new(ruleId, "Rule saved. This version starts fresh progress; previously earned rewards keep their original terms.");
        }, ct);

    public Task<RewardChangeResult> QualifyAsync(string tenantId, AuthUser user, RecordRewardQualificationRequest request, CancellationToken ct) =>
        Execute(tenantId, false, user, request?.RequestId, new { operation = "qualify", request }, true, async (db, tx, b, token) =>
        {
            Required(request); await Member(db, tx, b.Id, request!.MemberId, token);
            var rule = await CurrentRule(db, tx, b.Id, request.RuleId, token);
            if (!rule.Active || rule.Kind == "points") throw new RewardsException("Choose an active purchase or visit reward rule.");
            var reference = Text(request.SourceReference, "Verified source reference", 100);
            var note = Text(request.Note, "Verification note", 500, multiline: true);
            var units = request.Units; string? item = request.ItemId;
            var now = clock.GetUtcNow(); var sourceKey = "manual:" + reference;
            if (request.Source == "paid-order")
            {
                if (rule.Kind != "punch-card" || !Guid.TryParseExact(reference, "D", out _) || item != rule.ItemId)
                    throw new RewardsException("Choose a paid order and the rule's qualifying menu item.");
                var orders = await Rows(db, tx, "SELECT payload_json,status FROM bartide_enhanced_orders WHERE tenant_id=@t AND id=@id", r => (Json: r.GetString(0), Status: r.GetString(1)), token, ("@t", b.Id), ("@id", reference));
                if (orders.Count != 1) throw Missing();
                using var order = JsonDocument.Parse(orders[0].Json);
                if (orders[0].Status == "cancelled" || !order.RootElement.TryGetProperty("payment_status", out var payment) || payment.GetString() is not ("paid" or "paid_in_person"))
                    throw new RewardsException("Only a verified paid, non-refunded order can earn rewards.", 409, "order_unpaid");
                units = 0;
                foreach (var line in order.RootElement.GetProperty("lines").EnumerateArray())
                    if (line.GetProperty("item_id").GetString() == item && line.GetProperty("unit_cents").GetInt32() > 0)
                        units = checked(units + line.GetProperty("quantity").GetInt32());
                if (units == 0) throw new RewardsException("That paid order does not contain the qualifying item.");
                sourceKey = "order:" + reference + ":" + item;
            }
            else if (request.Source == "verified-visit")
            {
                if (rule.Kind != "monthly-visits" || units != 1 || !string.IsNullOrWhiteSpace(item)) throw new RewardsException("A verified visit records one visit, without a menu item.");
                item = null;
            }
            else if (request.Source == "verified-purchase")
            {
                if (rule.Kind != "punch-card" || item != rule.ItemId || units is < 1 or > 100) throw new RewardsException("Record 1–100 paid items matching this punch card.");
            }
            else throw new RewardsException("Choose a verified visit, in-person purchase, or paid order.");
            if (units is < 1 or > 100) throw new RewardsException("A single qualification supports up to 100 paid units.");
            var local = Local(now, rule.TimeZoneId);
            var day = rule.Kind == "monthly-visits" ? local.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) : null;
            var period = day is null ? "lifetime" : local.ToString("yyyy-MM", CultureInfo.InvariantCulture);
            if (await Number(db, tx, "SELECT COUNT(*) FROM tide_reward_qualifications WHERE tenant_id=@t AND rule_id=@r AND (source_key=@source OR (member_id=@m AND local_day=@day))", token,
                ("@t", b.Id), ("@r", rule.Id), ("@source", sourceKey), ("@m", request.MemberId), ("@day", day)) > 0)
                throw new RewardsException("That purchase or local-day visit was already recorded for this reward rule.", 409, "qualification_exists");
            var id = Id();
            await Run(db, tx, "INSERT INTO tide_reward_qualifications(id,tenant_id,rule_id,version_id,member_id,source_key,source_kind,source_reference,units,item_id,local_day,period,state,note,actor,occurred_at) VALUES(@id,@t,@r,@v,@m,@key,@kind,@ref,@units,@item,@day,@period,'eligible',@note,@actor,@now)", token,
                ("@id", id), ("@t", b.Id), ("@r", rule.Id), ("@v", rule.VersionId), ("@m", request.MemberId), ("@key", sourceKey), ("@kind", request.Source), ("@ref", reference), ("@units", units), ("@item", item), ("@day", day), ("@period", period), ("@note", note), ("@actor", user.UserId), ("@now", now.ToString("O")));
            await Reconcile(db, tx, b.Id, request.MemberId, token);
            return new(id, "Verified qualification recorded.");
        }, ct);

    public Task<RewardChangeResult> VoidAsync(string tenantId, string qualificationId, AuthUser user, VoidRewardQualificationRequest request, CancellationToken ct) =>
        Execute(tenantId, false, user, request?.RequestId, new { operation = "void", qualificationId, request }, true, async (db, tx, b, token) =>
        {
            Required(request); var reason = Text(request!.Reason, "Reason", 500, multiline: true);
            var members = await Rows(db, tx, "SELECT member_id FROM tide_reward_qualifications WHERE tenant_id=@t AND id=@id", r => r.GetString(0), token, ("@t", b.Id), ("@id", qualificationId));
            if (members.Count != 1) throw Missing();
            await Run(db, tx, "UPDATE tide_reward_qualifications SET state='void',void_reason=@reason WHERE tenant_id=@t AND id=@id AND state='eligible'", token, ("@reason", user.UserId + ": " + reason), ("@t", b.Id), ("@id", qualificationId));
            await Reconcile(db, tx, b.Id, members[0], token);
            return new(qualificationId, "Qualification voided. Unconsumed rewards were recalculated; fulfillment history is preserved.");
        }, ct);

    public Task<RewardChangeResult> PointsAsync(string tenantId, AuthUser user, AwardCustomerPointsRequest request, CancellationToken ct) =>
        Execute(tenantId, false, user, request?.RequestId, new { operation = "points", request }, true, async (db, tx, b, token) =>
        {
            Required(request); await Member(db, tx, b.Id, request!.MemberId, token);
            if (request.Points is < 1 or > 10000) throw new RewardsException("Award 1–10,000 verified points.");
            var reference = Text(request.SourceReference, "Verified source reference", 100);
            var reason = Text(request.Reason, "Reason", 500, multiline: true);
            var eventKey = "dotnet-award:" + reference;
            if (await Number(db, tx, "SELECT COUNT(*) FROM tide_loyalty_ledger WHERE tenant_id=@t AND event_key=@key", token, ("@t", b.Id), ("@key", eventKey)) > 0)
                throw new RewardsException("Points were already recorded for that source.", 409, "points_source_exists");
            if (await PointsBalance(db, tx, b.Id, request.MemberId, token) + request.Points > 1_000_000)
                throw new RewardsException("This member has reached the program's points limit.", 409, "points_limit");
            var id = Id();
            await CustomerLedger(db, tx, id, b.Id, request.MemberId, eventKey, request.Points, "dotnet-award", reason, user.UserId, token);
            return new(id, "Verified customer points recorded.");
        }, ct);

    public Task<RewardChangeResult> RequestAsync(string slug, string rewardId, AuthUser user, RequestRewardRedemptionRequest request, CancellationToken ct) =>
        Execute(slug, true, user, request?.RequestId, new { operation = "request", rewardId, request }, false, async (db, tx, b, token) =>
        {
            var member = await RequireOwnMember(db, tx, b.Id, user, token);
            await Reconcile(db, tx, b.Id, member, token);
            var reward = (await Rows(db, tx, IssuedSelect + " JOIN tide_loyalty_members m ON m.id=i.member_id AND m.tenant_id=i.tenant_id WHERE i.tenant_id=@t AND i.member_id=@m AND i.id=@id", ReadIssued, token,
                ("@t", b.Id), ("@m", member), ("@id", rewardId))).SingleOrDefault() ?? throw Missing();
            if (reward.State == "requested") return new(rewardId, "This reward is already awaiting staff fulfillment.");
            if (reward.State != "available") throw new RewardsException("This reward is not available to redeem.", 409, "reward_unavailable");
            await Run(db, tx, "UPDATE tide_reward_issued SET state='requested',updated_at=@now WHERE id=@id AND tenant_id=@t AND member_id=@m AND state='available'", token,
                ("@now", Now()), ("@id", rewardId), ("@t", b.Id), ("@m", member));
            return new(rewardId, "Show this reward to staff. It is awaiting fulfillment.");
        }, ct);

    public Task<RewardChangeResult> RedeemPointsAsync(string slug, AuthUser user, RedeemPointsRewardRequest request, CancellationToken ct) =>
        Execute(slug, true, user, request?.RequestId, new { operation = "points-redemption", request }, false, async (db, tx, b, token) =>
        {
            Required(request); var member = await RequireOwnMember(db, tx, b.Id, user, token);
            await Reconcile(db, tx, b.Id, member, token);
            var rule = await CurrentRule(db, tx, b.Id, request!.RuleId, token);
            if (!rule.Active || rule.Kind != "points") throw new RewardsException("Choose an active points reward.");
            if (await PointsBalance(db, tx, b.Id, member, token) < rule.Threshold) throw new RewardsException("You do not have enough points for this reward.", 409, "insufficient_points");
            // One pending request per rule prevents accidental second-click reservations even with a new request key.
            if (await Number(db, tx, "SELECT COUNT(*) FROM tide_reward_issued i JOIN tide_reward_rule_versions v ON v.id=i.version_id WHERE i.tenant_id=@t AND i.member_id=@m AND v.rule_id=@r AND i.state='requested'", token, ("@t", b.Id), ("@m", member), ("@r", rule.Id)) > 0)
                throw new RewardsException("This reward already has a pending request.", 409, "redemption_pending");
            var ordinal = await Number(db, tx, "SELECT COALESCE(MAX(ordinal),0)+1 FROM tide_reward_issued WHERE member_id=@m AND version_id=@v AND period='points'", token, ("@m", member), ("@v", rule.VersionId));
            var id = Id(); var now = Now();
            await Run(db, tx, "INSERT INTO tide_reward_issued(id,tenant_id,member_id,version_id,period,ordinal,state,points_cost,earned_at,updated_at) VALUES(@id,@t,@m,@v,'points',@ordinal,'requested',@cost,@now,@now)", token,
                ("@id", id), ("@t", b.Id), ("@m", member), ("@v", rule.VersionId), ("@ordinal", ordinal), ("@cost", rule.Threshold), ("@now", now));
            await CustomerLedger(db, tx, Id(), b.Id, member, "dotnet-reserve:" + id, -rule.Threshold, "dotnet-reserve", "Reserved: " + rule.Title, user.UserId, token);
            return new(id, "Points reserved. Show the reward to staff for fulfillment.");
        }, ct);

    public Task<RewardChangeResult> ResolveAsync(string tenantId, string rewardId, AuthUser user, ResolveRewardRedemptionRequest request, CancellationToken ct) =>
        Execute(tenantId, false, user, request?.RequestId, new { operation = "resolve", rewardId, request }, true, async (db, tx, b, token) =>
        {
            Required(request); var note = Text(request!.Note, "Fulfillment note", 500, multiline: true);
            if (request.Action is not ("fulfill" or "cancel")) throw new RewardsException("Choose fulfill or cancel.");
            var rows = await Rows(db, tx, "SELECT member_id,points_cost FROM tide_reward_issued WHERE tenant_id=@t AND id=@id", r => (Member: r.GetString(0), Cost: r.ReadInt32(1)), token, ("@t", b.Id), ("@id", rewardId));
            if (rows.Count != 1) throw Missing();
            var row = rows[0]; await Reconcile(db, tx, b.Id, row.Member, token);
            var state = await Value(db, tx, "SELECT state FROM tide_reward_issued WHERE id=@id AND tenant_id=@t", token, ("@id", rewardId), ("@t", b.Id));
            if (state != "requested") throw new RewardsException("This reward is no longer awaiting fulfillment. Refresh its status.", 409, "redemption_changed");
            var next = request.Action == "fulfill" ? "fulfilled" : row.Cost > 0 ? "cancelled" : "available";
            await Run(db, tx, "UPDATE tide_reward_issued SET state=@state,resolution_note=@note,updated_at=@now WHERE id=@id AND tenant_id=@t AND state='requested'", token,
                ("@state", next), ("@note", user.UserId + ": " + note), ("@now", Now()), ("@id", rewardId), ("@t", b.Id));
            if (request.Action == "cancel" && row.Cost > 0)
                await CustomerLedger(db, tx, Id(), b.Id, row.Member, "dotnet-release:" + rewardId, row.Cost, "dotnet-release", "Canceled reward reservation", user.UserId, token);
            return new(rewardId, request.Action == "fulfill" ? "Reward fulfillment recorded." : "Request canceled. The reward or its reserved points are available again.");
        }, ct);

    public Task<RewardChangeResult> EmployeePointsAsync(string tenantId, AuthUser user, ChangeEmployeePointsRequest request, CancellationToken ct) =>
        Execute(tenantId, false, user, request?.RequestId, new { operation = "employee-points", request }, true, async (db, tx, b, token) =>
        {
            Required(request); var reference = Text(request!.SourceReference, "Verified source reference", 100); var reason = Text(request.Reason, "Reason", 500, multiline: true);
            if (request.Delta is < -10000 or > 10000 or 0) throw new RewardsException("Use a nonzero points change between −10,000 and 10,000.");
            if (await Number(db, tx, "SELECT COUNT(*) FROM bartide_enhanced_members WHERE tenant_id=@t AND id=@m AND active=1 AND role IN('kitchen','driver')", token, ("@t", b.Id), ("@m", request.MemberId)) != 1) throw Missing();
            if (await Number(db, tx, "SELECT COUNT(*) FROM tide_employee_reward_ledger WHERE tenant_id=@t AND source_reference=@source", token, ("@t", b.Id), ("@source", reference)) > 0)
                throw new RewardsException("That employee reward source was already recorded.", 409, "employee_source_exists");
            var balance = await Number(db, tx, "SELECT COALESCE(SUM(delta),0) FROM tide_employee_reward_ledger WHERE tenant_id=@t AND member_id=@m", token, ("@t", b.Id), ("@m", request.MemberId));
            if (balance + request.Delta < 0) throw new RewardsException("This employee does not have enough points.", 409, "insufficient_points");
            var id = Id();
            await Run(db, tx, "INSERT INTO tide_employee_reward_ledger(id,tenant_id,member_id,source_reference,delta,reason,actor,created_at) VALUES(@id,@t,@m,@source,@delta,@reason,@actor,@now)", token,
                ("@id", id), ("@t", b.Id), ("@m", request.MemberId), ("@source", reference), ("@delta", request.Delta), ("@reason", reason), ("@actor", user.UserId), ("@now", Now()));
            return new(id, request.Delta > 0 ? "Employee reward points granted." : "Employee reward fulfillment recorded.");
        }, ct);

    private async Task<RewardChangeResult> Execute(string key, bool slug, AuthUser user, string? requestId, object request, bool manager, Change change, CancellationToken ct)
    {
        if (!Guid.TryParseExact(requestId, "D", out _)) throw new RewardsException("Refresh this form and try again.");
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(request, Json))));
        await using var db = await database.OpenAsync(ct);
        using var tx = db.BeginTransaction(deferred: false);
        var b = await Tenant(db, tx, key, slug, ct);
        if (manager) Manager(b, user);
        var old = await Rows(db, tx, "SELECT request_hash,result_id,message FROM tide_reward_commands WHERE tenant_id=@t AND actor=@u AND request_id=@key", r => new[] { r.GetString(0), r.GetString(1), r.GetString(2) }, ct,
            ("@t", b.Id), ("@u", user.UserId), ("@key", requestId));
        if (old.Count == 1)
        {
            if (old[0][0] != hash) throw new RewardsException("That request was already used with different details.", 409, "request_conflict");
            return new(old[0][1], old[0][2]);
        }
        var result = await change(db, tx, b, ct);
        await Run(db, tx, "INSERT INTO tide_reward_commands(tenant_id,actor,request_id,request_hash,result_id,message,created_at) VALUES(@t,@u,@key,@hash,@id,@message,@now)", ct,
            ("@t", b.Id), ("@u", user.UserId), ("@key", requestId), ("@hash", hash), ("@id", result.Id), ("@message", result.Message), ("@now", Now()));
        await tx.CommitAsync(ct);
        return result;
    }

    private async Task Reconcile(DbConnection db, DbTransaction tx, string tenant, string member, CancellationToken ct)
    {
        await ImportPaidOrders(db, tx, tenant, member, ct);
        var groups = await Rows(db, tx, "SELECT DISTINCT q.version_id,q.period,v.kind,v.threshold FROM tide_reward_qualifications q JOIN tide_reward_rule_versions v ON v.id=q.version_id WHERE q.tenant_id=@t AND q.member_id=@m", r => (Version: r.GetString(0), Period: r.GetString(1), Kind: r.GetString(2), Threshold: r.ReadInt32(3)), ct, ("@t", tenant), ("@m", member));
        foreach (var group in groups)
        {
            var units = await Qualified(db, tx, member, group.Version, group.Period, ct);
            var earned = group.Kind == "monthly-visits" ? (units >= group.Threshold ? 1 : 0) : units / group.Threshold;
            // Consumed rewards retain their history and ordinal: later qualifications cannot issue
            // another reward into the same slot after a refund or owner correction.
            await Run(db, tx, "UPDATE tide_reward_issued SET state='void',updated_at=@now WHERE tenant_id=@t AND member_id=@m AND version_id=@v AND period=@p AND ordinal>@earned AND state IN('available','requested')", ct,
                ("@now", Now()), ("@t", tenant), ("@m", member), ("@v", group.Version), ("@p", group.Period), ("@earned", earned));
            await Run(db, tx, "UPDATE tide_reward_issued SET state='available',updated_at=@now WHERE tenant_id=@t AND member_id=@m AND version_id=@v AND period=@p AND ordinal<=@earned AND state='void'", ct,
                ("@now", Now()), ("@t", tenant), ("@m", member), ("@v", group.Version), ("@p", group.Period), ("@earned", earned));
            var issuedThrough = await Number(db, tx, "SELECT COALESCE(MAX(ordinal),0) FROM tide_reward_issued WHERE tenant_id=@t AND member_id=@m AND version_id=@v AND period=@p", ct,
                ("@t", tenant), ("@m", member), ("@v", group.Version), ("@p", group.Period));
            for (var ordinal = issuedThrough + 1; ordinal <= earned; ordinal++)
                await Run(db, tx, "INSERT INTO tide_reward_issued(id,tenant_id,member_id,version_id,period,ordinal,state,points_cost,earned_at,updated_at) VALUES(@id,@t,@m,@v,@p,@n,'available',0,@now,@now)", ct,
                    ("@id", Id()), ("@t", tenant), ("@m", member), ("@v", group.Version), ("@p", group.Period), ("@n", ordinal), ("@now", Now()));
        }
    }

    private Task<long> Qualified(DbConnection db, DbTransaction tx, string member, string version, string period, CancellationToken ct) =>
        Number(db, tx, db.Sql("SELECT COALESCE(SUM(q.units),0) FROM tide_reward_qualifications q WHERE q.member_id=@m AND q.version_id=@v AND q.period=@p AND q.state='eligible' AND julianday(q.occurred_at)<=julianday(@now) AND (q.source_kind<>'paid-order' OR EXISTS(SELECT 1 FROM bartide_enhanced_orders o WHERE o.id=q.source_reference AND o.tenant_id=q.tenant_id AND o.status NOT IN('cancelled','canceled','payment_review','paid_needs_review') AND json_extract(o.payload_json,'$.payment_status') IN('paid','paid_in_person')))",
            "SELECT COALESCE(SUM(q.units),0) FROM tide_reward_qualifications q WHERE q.member_id=@m AND q.version_id=@v AND q.period=@p AND q.state='eligible' AND tide_iso_instant(q.occurred_at)<=tide_iso_instant(@now) AND (q.source_kind<>'paid-order' OR EXISTS(SELECT 1 FROM bartide_enhanced_orders o WHERE o.id=q.source_reference AND o.tenant_id=q.tenant_id AND o.status NOT IN('cancelled','canceled','payment_review','paid_needs_review') AND (tide_json(o.payload_json)->>'payment_status') IN('paid','paid_in_person')))"), ct,
            ("@m", member), ("@v", version), ("@p", period), ("@now", Now()));

    private static async Task<Business> Tenant(DbConnection db, DbTransaction tx, string key, bool slug, CancellationToken ct)
    {
        var rows = await Rows(db, tx, "SELECT id,slug,name,COALESCE(user_id,''),status,vertical FROM bartide_customers WHERE " + (slug ? "slug" : "id") + "=@key", r => new[] { r.GetString(0), r.GetString(1), r.GetString(2), r.GetString(3), r.GetString(4), r.GetString(5) }, ct, ("@key", key));
        if (rows.Count != 1 || rows[0][4] != "active" || rows[0][5] != "bartide") throw Missing();
        var row = rows[0]; return new(row[0], row[1], row[2], row[3]);
    }
    private static void Manager(Business b, AuthUser user) { if (!user.IsPlatformOwner && b.Owner != user.UserId) throw Forbidden(); }
    private static Task<string?> OwnMember(DbConnection db, DbTransaction tx, string tenant, AuthUser user, CancellationToken ct) => Value(db, tx, "SELECT id FROM tide_loyalty_members WHERE tenant_id=@t AND user_id=@u", ct, ("@t", tenant), ("@u", user.UserId));
    private static async Task<string> RequireOwnMember(DbConnection db, DbTransaction tx, string tenant, AuthUser user, CancellationToken ct) => await OwnMember(db, tx, tenant, user, ct) ?? throw new RewardsException("Join this business's rewards program first.", 409, "join_required");
    private static async Task Member(DbConnection db, DbTransaction tx, string tenant, string member, CancellationToken ct)
    { if (await Number(db, tx, "SELECT COUNT(*) FROM tide_loyalty_members WHERE tenant_id=@t AND id=@m", ct, ("@t", tenant), ("@m", member)) != 1) throw Missing(); }
    private static Task<long> PointsBalance(DbConnection db, DbTransaction tx, string tenant, string member, CancellationToken ct) => Number(db, tx, "SELECT COALESCE(SUM(delta),0) FROM tide_loyalty_ledger WHERE tenant_id=@t AND member_id=@m", ct, ("@t", tenant), ("@m", member));
    private Task<int> CustomerLedger(DbConnection db, DbTransaction tx, string id, string tenant, string member, string key, int delta, string kind, string reason, string actor, CancellationToken ct) =>
        Run(db, tx, "INSERT INTO tide_loyalty_ledger(id,tenant_id,member_id,event_key,delta,kind,reason,state,actor,created_at) VALUES(@id,@t,@m,@key,@delta,@kind,@reason,'recorded',@actor,@now)", ct,
            ("@id", id), ("@t", tenant), ("@m", member), ("@key", key), ("@delta", delta), ("@kind", kind), ("@reason", reason), ("@actor", actor), ("@now", Now()));

    private const string RuleSelect = "SELECT r.id,v.id,v.version,v.title,v.reward,v.kind,v.threshold,v.item_id,v.timezone_id,v.active FROM tide_reward_rules r JOIN tide_reward_rule_versions v ON v.id=r.current_version_id AND v.rule_id=r.id ";
    private static RewardRuleCard ReadRule(DbDataReader r) => new(r.GetString(0), r.GetString(1), r.ReadInt32(2), r.GetString(3), r.GetString(4), r.GetString(5), r.ReadInt32(6), r.IsDBNull(7) ? null : r.GetString(7), r.GetString(8), r.ReadInt32(9) == 1);
    private static Task<List<RewardRuleCard>> Rules(DbConnection db, DbTransaction tx, string tenant, CancellationToken ct, bool activeOnly = false) => Rows(db, tx, RuleSelect + "WHERE r.tenant_id=@t" + (activeOnly ? " AND v.active=1" : "") + " ORDER BY v.title", ReadRule, ct, ("@t", tenant));
    private static async Task<RewardRuleCard> CurrentRule(DbConnection db, DbTransaction tx, string tenant, string rule, CancellationToken ct) => (await Rows(db, tx, RuleSelect + "WHERE r.tenant_id=@t AND r.id=@r", ReadRule, ct, ("@t", tenant), ("@r", rule))).SingleOrDefault() ?? throw Missing();
    // Member identity/name is selected for the owner projection; the customer DTO
    // discards those fields and the query requires the authenticated member ID.
    private const string IssuedSelect = "SELECT i.id,v.rule_id,v.title,v.reward,v.kind,i.state,i.earned_at,i.points_cost,i.version_id,i.period,i.member_id,m.name FROM tide_reward_issued i JOIN tide_reward_rule_versions v ON v.id=i.version_id";
    private static CustomerReward ReadIssued(DbDataReader r) => new(r.GetString(0), r.GetString(1), r.GetString(2), r.GetString(3), r.GetString(4), r.GetString(5), r.GetString(6), r.ReadInt32(7));
    private static Task<List<CustomerReward>> Issued(DbConnection db, DbTransaction tx, string tenant, string member, CancellationToken ct) => Rows(db, tx,
        IssuedSelect + " JOIN tide_loyalty_members m ON m.id=i.member_id AND m.tenant_id=i.tenant_id WHERE i.tenant_id=@t AND i.member_id=@m ORDER BY CASE i.state WHEN 'requested' THEN 0 WHEN 'available' THEN 1 ELSE 2 END,i.earned_at DESC LIMIT 500", ReadIssued, ct, ("@t", tenant), ("@m", member));
    private static Task<List<EmployeeRewardEntry>> EmployeeEntries(DbConnection db, DbTransaction tx, string tenant, string? member, CancellationToken ct) => Rows(db, tx,
        "SELECT id,member_id,delta,reason,actor,created_at FROM tide_employee_reward_ledger WHERE tenant_id=@t" + (member is null ? "" : " AND member_id=@m") + " ORDER BY created_at DESC LIMIT 200",
        r => new EmployeeRewardEntry(r.GetString(0), r.GetString(1), r.ReadInt32(2), r.GetString(3), r.GetString(4), r.GetString(5)), ct, ("@t", tenant), ("@m", member));
    private static DateTimeOffset Local(DateTimeOffset time, string zone)
    {
        try { return TimeZoneInfo.ConvertTime(time, TimeZoneInfo.FindSystemTimeZoneById(zone)); }
        catch (Exception e) when (e is TimeZoneNotFoundException or InvalidTimeZoneException or ArgumentException) { throw new RewardsException("Choose a valid business timezone, such as America/New_York."); }
    }
    private static string Text(string? text, string label, int max, bool multiline = false)
    {
        var value = text?.Trim() ?? "";
        if (value.Length == 0 || value.Length > max || value.Any(c => char.IsControl(c) && !(multiline && c is '\r' or '\n' or '\t')))
            throw new RewardsException($"{label} is required and must use at most {max} characters.");
        return value;
    }
    private static void Required(object? value) { if (value is null) throw new RewardsException("Complete the form and try again."); }
    private static RewardsException Forbidden() => new("You do not have access to these rewards.", 403, "rewards_forbidden");
    private static RewardsException Missing() => new("Rewards record not found.", 404, "not_found");
    private static RewardsException Stale() => new("This rule changed. Refresh before saving again.", 409, "stale_rule");
    private static DbCommand Command(DbConnection db, DbTransaction tx, string sql, params (string Name, object? Value)[] args)
    { var command = db.CreateCommand(); command.Transaction = tx; command.CommandText = sql; foreach (var arg in args) command.Parameters.AddWithValue(arg.Name, arg.Value ?? DBNull.Value); return command; }
    private static async Task<int> Run(DbConnection db, DbTransaction tx, string sql, CancellationToken ct, params (string Name, object? Value)[] args)
    { await using var cmd = Command(db, tx, sql, args); return await cmd.ExecuteNonQueryAsync(ct); }
    private static async Task<long> Number(DbConnection db, DbTransaction tx, string sql, CancellationToken ct, params (string Name, object? Value)[] args)
    { await using var cmd = Command(db, tx, sql, args); return Convert.ToInt64(await cmd.ExecuteScalarAsync(ct)); }
    private static async Task<string?> Value(DbConnection db, DbTransaction tx, string sql, CancellationToken ct, params (string Name, object? Value)[] args)
    { await using var cmd = Command(db, tx, sql, args); return await cmd.ExecuteScalarAsync(ct) as string; }
    private static async Task<List<T>> Rows<T>(DbConnection db, DbTransaction tx, string sql, Func<DbDataReader, T> read, CancellationToken ct, params (string Name, object? Value)[] args)
    { await using var cmd = Command(db, tx, sql, args); await using var reader = await cmd.ExecuteReaderAsync(ct); var result = new List<T>(); while (await reader.ReadAsync(ct)) result.Add(read(reader)); return result; }
}
