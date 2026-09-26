using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace TideCasa.Blazor.Features.Collect;

public sealed class CollectFault(int status, string message) : Exception(message) { public int Status { get; } = status; }
public sealed record CollectActor(string Id, string Email, bool Preview = false);
public sealed record CollectMember(string UserId, string Role);
public sealed record CollectInvite(string Hash, string Email, string Role, DateTimeOffset Expires, string? CaseId = null);
public sealed record CollectAudit(string Id, DateTimeOffset At, string Actor, string Action, string? CaseId);
public sealed record CollectExpense(string Category, long MonthlyCents);
public sealed record CollectProfile(long IncomeCents, string Period, string Basis, List<CollectExpense> Expenses, DateTimeOffset At);
public sealed record CollectEvidence(string Id, string Kind, string FileName, string ContentType, long Bytes, string Sha256,
    string Scan, string Status, long? IncomeCents, string Period, string Basis, DateOnly PeriodStart, DateOnly PeriodEnd,
    DateTimeOffset UploadedAt, string? Reviewer = null, DateTimeOffset? ReviewedAt = null, DateTimeOffset? Expires = null,
    string? Replaces = null, string Method = "uploaded document", bool Synthetic = false);
public sealed record CollectFinding(string EvidenceId, string Status, string Reviewer, DateTimeOffset At, DateTimeOffset Expires);
public sealed record CollectOffer(string Id, int Version, string Party, long TotalCents, int Installments, DateOnly FirstDate,
    DateTimeOffset Created, DateTimeOffset Expires, string Terms, string State = "open");
public sealed record CollectInstallment(int Number, DateOnly Due, long Cents);
public sealed record CollectAgreement(string Id, string OfferId, string CreditorId, string CreditorName, string ClientName,
    string DebtorId, long TotalCents, List<CollectInstallment> Schedule, string Terms, string AcceptedBy, DateTimeOffset AcceptedAt, bool Synthetic);
public sealed record CollectPayment(string Id, string AgreementId, int Installment, string CreditorId, long Cents, string State,
    bool Simulated, int LastSequence = 0, List<string>? EventIds = null);
public sealed record CollectDispute(string Id, string Category, DateTimeOffset At, string State = "open");
public sealed record CollectInquiry(string Id, string CustomerId, string CustomerLabel, string CreditorId, DateTimeOffset At,
    CollectProfile? SharedProfile, string State = "received");
public sealed record CollectDirectoryEntry(string Id, string Name, string Slug, string Description, bool Synthetic);
public sealed class CollectPersonal
{
    public string OwnerId { get; set; } = "";
    public CollectProfile? Profile { get; set; }
    public List<CollectInquiry> Inquiries { get; set; } = [];
}
public sealed record CollectView(CollectClient Client, CollectActor Actor, CollectPersonal Personal, string Csrf, string Path, string? Notice = null);
public sealed class CollectCase
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Reference { get; set; } = "";
    public string CreditorId { get; set; } = "";
    public string CreditorName { get; set; } = "";
    public string? DebtorId { get; set; }
    public string DebtorLabel { get; set; } = "Customer";
    public long PrincipalCents { get; set; }
    public long InterestCents { get; set; }
    public long FeesCents { get; set; }
    public long CreditsCents { get; set; }
    public long BalanceCents => checked(PrincipalCents + InterestCents + FeesCents - CreditsCents);
    public long MinimumSettlementCents { get; set; }
    public bool AuthorityRecorded { get; set; }
    public string AuthorityReference { get; set; } = "";
    public string Notice { get; set; } = "";
    public DateOnly ItemizationDate { get; set; }
    public string NoticeStatus { get; set; } = "draft";
    public bool EmailOptOut { get; set; }
    public bool SupportRequested { get; set; }
    public List<CollectDispute> Disputes { get; set; } = [];
    public CollectProfile? Profile { get; set; }
    public List<CollectEvidence> Evidence { get; set; } = [];
    public List<CollectFinding> Findings { get; set; } = [];
    public List<CollectOffer> Offers { get; set; } = [];
    public CollectAgreement? Agreement { get; set; }
    public List<CollectPayment> Payments { get; set; } = [];
}
public sealed class CollectClient
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public int Revision { get; set; }
    public string Name { get; set; } = "Harbor Resolution";
    public string Slug { get; set; } = "harbor-resolution";
    public string Accent { get; set; } = "#245c4f";
    public string Kind { get; set; } = "collection-agency";
    public string State { get; set; } = "draft";
    public string Market { get; set; } = "Unconfigured";
    public bool Preview { get; set; }
    public DateTimeOffset? Expires { get; set; }
    public List<CollectMember> Members { get; set; } = [];
    public List<CollectInvite> Invites { get; set; } = [];
    public List<CollectCase> Cases { get; set; } = [];
    public List<CollectAudit> Audit { get; set; } = [];
    public List<string> Imports { get; set; } = [];
    public CollectPersonal Personal { get; set; } = new();
    public List<CollectInquiry> Inquiries { get; set; } = [];
}

public static class CollectRules
{
    public static string Hash(string value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)));
    public static string Token() => Convert.ToHexString(RandomNumberGenerator.GetBytes(32)).ToLowerInvariant();
    public static string? Role(CollectClient client, CollectActor actor) => client.Preview == actor.Preview ? client.Members.SingleOrDefault(x => x.UserId == actor.Id)?.Role : null;
    public static bool Staff(CollectClient client, CollectActor actor) => Role(client, actor) is "admin" or "reviewer" or "negotiator";
    public static void Require(bool condition, string message, int status = 400) { if (!condition) throw new CollectFault(status, message); }
    public static void Demand(CollectClient client, CollectActor actor, params string[] roles) => Require(roles.Contains(Role(client, actor)), "This workspace is unavailable.", 404);
    public static CollectCase Case(CollectClient client, CollectActor actor, string id)
    {
        var item = client.Cases.SingleOrDefault(x => x.Id == id);
        Require(item != null && client.Preview == actor.Preview && (Staff(client, actor) || item.DebtorId == actor.Id), "This account is unavailable.", 404);
        return item!;
    }
    public static void BoundCase(CollectClient client, CollectActor actor, CollectCase item) => Require(client.Preview == actor.Preview && client.Cases.Contains(item)
        && (Staff(client, actor) || item.DebtorId == actor.Id), "This account is unavailable.", 404);
    public static void Debtor(CollectClient client, CollectActor actor, CollectCase item) { BoundCase(client, actor, item); Require(item.DebtorId == actor.Id, "This account is unavailable.", 404); }
    public static void Audit(CollectClient client, CollectActor actor, string action, DateTimeOffset now, string? caseId = null)
    {
        Require(client.Audit.Count < 10000, "This pilot workspace has reached its activity limit.", 409);
        client.Audit.Add(new(Guid.NewGuid().ToString("N"), now, actor.Id, action, caseId));
    }
    public static long Money(string text)
    {
        Require(decimal.TryParse(text, NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var value)
            && value >= 0 && value <= 10000000 && value * 100 == decimal.Truncate(value * 100), "Enter an amount with no more than two decimal places.");
        return (long)(value * 100);
    }
    public static decimal Monthly(long cents, string period) => period switch
    {
        "weekly" => cents * 52m / 12m, "biweekly" => cents * 26m / 12m,
        "semimonthly" => cents * 2m, "monthly" => cents, _ => throw new CollectFault(400, "Choose a valid pay period.")
    };
    public static (decimal? Difference, decimal? Percent, string Reason) Compare(CollectProfile? profile, CollectEvidence? evidence, DateTimeOffset now)
    {
        if (profile == null || evidence?.IncomeCents == null) return (null, null, "Missing evidence");
        if (evidence.Status != "document-reviewed" || evidence.Scan != "clean" || evidence.Expires is null || evidence.Expires <= now) return (null, null, "Evidence needs review");
        if (profile.Basis != evidence.Basis) return (null, null, "Gross/net basis differs");
        var reported = Monthly(profile.IncomeCents, profile.Period);
        var observed = Monthly(evidence.IncomeCents.Value, evidence.Period);
        var difference = observed - reported;
        return (difference, reported == 0 ? null : decimal.Round(difference / reported * 100m, 2), reported == 0 ? "No percentage: reported income is zero" : "Reviewed minus reported; monthly equivalent");
    }
    public static void SetProfile(CollectClient client, CollectActor actor, CollectCase item, CollectProfile profile, DateTimeOffset now)
    {
        Debtor(client, actor, item);
        ValidateProfile(profile);
        item.Profile = profile with { At = now }; Audit(client, actor, "profile.updated", now, item.Id);
    }
    public static void ValidateProfile(CollectProfile profile)
    {
        Require(profile.IncomeCents is >= 0 and <= 1000000000 && profile.Basis is "net" or "gross", "Check the income and gross/net basis.");
        _ = Monthly(profile.IncomeCents, profile.Period);
        Require(profile.Expenses.Count <= 12 && profile.Expenses.All(x => x.Category is "housing" or "utilities" or "food" or "transport" or "health" or "other" && x.MonthlyCents is >= 0 and <= 1000000000)
            && profile.Expenses.Select(x => x.Category).Distinct().Count() == profile.Expenses.Count, "Check the monthly expenses.");
    }
    public static void AddEvidence(CollectClient client, CollectActor actor, CollectCase item, CollectEvidence evidence, DateTimeOffset now)
    {
        Debtor(client, actor, item);
        Require(item.Evidence.Count < 12 && evidence.Status == "uploaded" && evidence.Scan is "quarantined" or "clean"
            && evidence.Bytes is > 0 and <= 4194304 && evidence.Kind is "paystub" or "bill" or "budget"
            && evidence.Basis is "net" or "gross" && evidence.IncomeCents is null or >= 0
            && evidence.PeriodEnd >= evidence.PeriodStart && evidence.PeriodEnd <= DateOnly.FromDateTime(now.UtcDateTime)
            && !item.Evidence.Any(x => x.Id == evidence.Id) && (evidence.Replaces == null || item.Evidence.Any(x => x.Id == evidence.Replaces)), "Check the document details or document limit.");
        _ = Monthly(0, evidence.Period);
        item.Evidence.Add(evidence); Audit(client, actor, "evidence.uploaded", now, item.Id);
    }
    public static void Review(CollectClient client, CollectActor actor, CollectCase item, string id, string status, DateTimeOffset now)
    {
        BoundCase(client, actor, item);
        Demand(client, actor, "admin", "reviewer");
        var index = item.Evidence.FindIndex(x => x.Id == id); Require(index >= 0, "Document unavailable.", 404);
        var evidence = item.Evidence[index];
        Require(evidence.Scan == "clean" && status is "document-reviewed" or "clarification-needed", "A creditor may review a cleared document or request clarification. The app does not certify authenticity.");
        Require(evidence.Reviewer == null || evidence.Status == "clarification-needed", "This document has already been reviewed; upload a new version.", 409);
        item.Evidence[index] = evidence with { Status = status, Reviewer = actor.Id, ReviewedAt = now, Expires = now.AddDays(30) };
        item.Findings.Add(new(id, status, actor.Id, now, now.AddDays(30))); Audit(client, actor, "evidence.reviewed", now, item.Id);
    }
    public static void Negotiate(CollectClient client, CollectActor actor, CollectCase item, long amount, int installments, DateOnly first, string terms, DateTimeOffset now)
    {
        BoundCase(client, actor, item);
        Require(item.DebtorId == actor.Id || Role(client, actor) is "admin" or "negotiator", "Negotiation unavailable.", 404);
        ReadyToNegotiate(client, item);
        Require(amount > 0 && amount <= item.BalanceCents && installments is >= 1 and <= 60 && first >= DateOnly.FromDateTime(now.UtcDateTime)
            && first <= DateOnly.FromDateTime(now.AddYears(1).UtcDateTime) && amount >= installments && terms.Length <= 1200 && item.Offers.Count < 100, "Check the amount, dates and payment count.");
        for (var n = 0; n < item.Offers.Count; n++) if (item.Offers[n].State == "open") item.Offers[n] = item.Offers[n] with { State = "superseded" };
        item.Offers.Add(new(Guid.NewGuid().ToString("N"), item.Offers.Count + 1, item.DebtorId == actor.Id ? "debtor" : "client", amount, installments, first, now, now.AddDays(14), terms));
        Audit(client, actor, "offer.proposed", now, item.Id);
    }
    public static void ReadyToNegotiate(CollectClient client, CollectCase item)
    {
        Require(client.State == "active" && item.AuthorityRecorded && item.NoticeStatus == "issued" && !item.Disputes.Any(x => x.State == "open") && item.Agreement == null,
            "Negotiation is paused until account review, notices and any dispute are resolved.", 409);
    }
    public static void Accept(CollectClient client, CollectActor actor, CollectCase item, string offerId, DateTimeOffset now)
    {
        BoundCase(client, actor, item);
        ReadyToNegotiate(client, item);
        var offer = item.Offers.LastOrDefault();
        Require(offer?.Id == offerId && offer.State == "open" && offer.Expires > now, "This offer is no longer current. Review the latest proposal.", 409);
        Require(offer!.Party == "client" ? item.DebtorId == actor.Id : Role(client, actor) is "admin" or "negotiator", "Only the other party can accept this proposal.", 403);
        Require(offer.TotalCents >= item.MinimumSettlementCents && item.DebtorId != null, "Creditor approval is required for this amount.", 409);
        var each = offer.TotalCents / offer.Installments; var remainder = offer.TotalCents % offer.Installments;
        var schedule = Enumerable.Range(1, offer.Installments).Select(n => new CollectInstallment(n, offer.FirstDate.AddMonths(n - 1), each + (n <= remainder ? 1 : 0))).ToList();
        item.Agreement = new(Guid.NewGuid().ToString("N"), offer.Id, item.CreditorId, item.CreditorName, client.Name, item.DebtorId!, offer.TotalCents, schedule, offer.Terms, actor.Id, now, client.Preview);
        item.Offers[^1] = offer with { State = "accepted" }; Audit(client, actor, "agreement.accepted", now, item.Id);
    }
    public static void Withdraw(CollectClient client, CollectActor actor, CollectCase item, string offerId, DateTimeOffset now)
    {
        BoundCase(client, actor, item);
        var offer = item.Offers.LastOrDefault();
        Require(offer?.Id == offerId && offer.State == "open", "This offer is no longer current.", 409);
        Require(offer!.Party == "debtor" ? item.DebtorId == actor.Id : Role(client, actor) is "admin" or "negotiator", "Only the proposer can withdraw this offer.", 403);
        item.Offers[^1] = offer with { State = "withdrawn" }; Audit(client, actor, "offer.withdrawn", now, item.Id);
    }
    public static CollectPayment BeginPayment(CollectClient client, CollectActor actor, CollectCase item, int installment, DateTimeOffset now)
    {
        Debtor(client, actor, item);
        Require(client.Preview, "Live payments are unavailable until an eligible payment provider is configured.", 409);
        var agreement = item.Agreement; Require(agreement != null, "Accept an agreement first.", 409);
        Require(!item.Disputes.Any(x => x.State == "open"), "Payments are paused during dispute review.", 409);
        var due = agreement!.Schedule.SingleOrDefault(x => x.Number == installment); Require(due != null, "Payment unavailable.", 404);
        var existing = item.Payments.SingleOrDefault(x => x.Installment == installment);
        if (existing != null) return existing;
        var payment = new CollectPayment(Guid.NewGuid().ToString("N"), agreement.Id, installment, agreement.CreditorId, due!.Cents, "pending", true, EventIds: []);
        item.Payments.Add(payment); Audit(client, actor, "payment.simulation.started", now, item.Id); return payment;
    }
    // The simulator is deliberately separate from a real provider callback: no public payment webhook exists.
    public static void SimulateEvent(CollectClient client, CollectActor actor, CollectCase item, string paymentId, string eventId, int sequence, string state, DateTimeOffset now)
    {
        Debtor(client, actor, item); Require(client.Preview, "Simulation is unavailable for real accounts.", 404);
        var index = item.Payments.FindIndex(x => x.Id == paymentId); Require(index >= 0, "Payment unavailable.", 404);
        var payment = item.Payments[index];
        if (payment.EventIds!.Contains(eventId) || sequence <= payment.LastSequence) return;
        Require((payment.State, state) is ("pending", "succeeded") or ("pending", "failed") or ("succeeded", "settled") or ("succeeded", "returned") or ("settled", "returned") or ("settled", "refunded"), "That payment event is out of order.", 409);
        item.Payments[index] = payment with { State = state, LastSequence = sequence, EventIds = [.. payment.EventIds, eventId] };
        Audit(client, actor, "payment.simulation." + state, now, item.Id);
    }
    public static string Invite(CollectClient client, CollectActor actor, string email, string role, DateTimeOffset now, string? caseId = null)
    {
        Demand(client, actor, "admin");
        Require(email.Length <= 254 && System.Net.Mail.MailAddress.TryCreate(email, out var parsed) && parsed.Address == email && role is "reviewer" or "negotiator" or "debtor" && client.Invites.Count < 100,
            "Check the email address and role.");
        Require(role != "debtor" || client.Cases.Any(x => x.Id == caseId && x.DebtorId == null), "Account unavailable.");
        var token = Token(); client.Invites.Add(new(Hash(token), email.ToLowerInvariant(), role, now.AddDays(7), caseId)); Audit(client, actor, "invitation.created", now, caseId); return token;
    }
    public static void Claim(CollectClient client, CollectActor actor, string token, DateTimeOffset now)
    {
        var invite = client.Invites.SingleOrDefault(x => x.Hash == Hash(token));
        Require(client.Preview == actor.Preview && invite != null && invite.Expires > now && invite.Email.Equals(actor.Email, StringComparison.OrdinalIgnoreCase), "The invitation is unavailable or does not match this verified account.", 404);
        if (invite!.Role == "debtor") { var item = client.Cases.Single(x => x.Id == invite.CaseId); Require(item.DebtorId == null, "The invitation is unavailable.", 404); item.DebtorId = actor.Id; }
        else { Require(!client.Members.Any(x => x.UserId == actor.Id), "Membership already exists.", 409); client.Members.Add(new(actor.Id, invite.Role)); }
        client.Invites.Remove(invite); Audit(client, actor, "invitation.claimed", now, invite.CaseId);
    }
    public static CollectClient Fixture(string id, DateTimeOffset now)
    {
        var c = new CollectClient { Id = id, Preview = true, State = "active", Market = "Fictional US / USD", Expires = now.AddHours(24),
            Members = [new(id + ":admin", "admin"), new(id + ":reviewer", "reviewer"), new(id + ":negotiator", "negotiator")] };
        c.Cases.Add(new() { Reference = "HR-2048", CreditorId = "sample-creditor-1", CreditorName = "Northstar Credit (fictional)", DebtorId = id + ":debtor", DebtorLabel = "Alex Morgan (fictional)", PrincipalCents = 240000, InterestCents = 12500, FeesCents = 0, CreditsCents = 12500,
            MinimumSettlementCents = 120000, AuthorityRecorded = true, AuthorityReference = "DEMO-AUTH-001", NoticeStatus = "issued", ItemizationDate = DateOnly.FromDateTime(now.AddDays(-30).UtcDateTime),
            Notice = "SAMPLE ONLY — Account itemization and validation notice. This fictional account is provided for learning. Real notices require creditor, account, consumer, dates, dispute instructions and jurisdiction review.",
            Profile = new(180000, "biweekly", "net", [new("housing", 140000), new("utilities", 24000), new("food", 45000), new("transport", 28000), new("health", 15000)], now) });
        c.Cases.Add(new() { Reference = "HR-2051", CreditorId = "sample-creditor-2", CreditorName = "Cedar Services (fictional)", DebtorId = id + ":other-debtor", DebtorLabel = "Jamie Chen (fictional)", PrincipalCents = 89000, MinimumSettlementCents = 60000, AuthorityRecorded = true, AuthorityReference = "DEMO-AUTH-002", NoticeStatus = "issued", ItemizationDate = DateOnly.FromDateTime(now.AddDays(-30).UtcDateTime), Notice = "SAMPLE ONLY — Account awaiting financial information." });
        c.Personal = new() { OwnerId = id + ":debtor", Profile = c.Cases[0].Profile };
        return c;
    }

    public static readonly CollectDirectoryEntry[] Directory = [
        new("sample-creditor-1", "Northstar Credit", "northstar-credit", "Fictional creditor office · handled by Harbor Resolution", true),
        new("sample-creditor-2", "Cedar Services", "cedar-services", "Fictional services creditor · handled by Harbor Resolution", true)
    ];

    public static void Inquire(CollectClient client, CollectActor actor, string creditorId, bool shareProfile, DateTimeOffset now)
    {
        Require(actor.Id == client.Personal.OwnerId && client.Preview == actor.Preview, "This profile is unavailable.", 404);
        Require(Directory.Any(x => x.Id == creditorId) && client.Preview, "This creditor is unavailable.", 404);
        Require(client.Personal.Inquiries.Count < 10 && !client.Personal.Inquiries.Any(x => x.CreditorId == creditorId && x.State == "received"), "An inquiry is already pending or the pilot inquiry limit was reached.", 409);
        Require(!shareProfile || client.Personal.Profile != null, "Create a profile before choosing to share it.");
        var inquiry = new CollectInquiry(Guid.NewGuid().ToString("N"), actor.Id, "Alex Morgan (fictional)", creditorId, now,
            shareProfile ? JsonSerializer.Deserialize<CollectProfile>(JsonSerializer.Serialize(client.Personal.Profile)) : null);
        client.Personal.Inquiries.Add(inquiry); client.Inquiries.Add(inquiry);
        Audit(client, actor, shareProfile ? "inquiry.profile_shared" : "inquiry.sent_without_profile", now);
    }

    public static void Import(CollectClient client, CollectActor actor, string csv, DateTimeOffset now)
    {
        Demand(client, actor, "admin");
        Require(csv.Length <= 16384 && !csv.Contains('"'), "Use the provided simple CSV format, without quoted fields (maximum 16 KB).");
        var key = Hash(csv.Trim().Replace("\r\n", "\n"));
        if (client.Imports.Contains(key)) return;
        var lines = csv.Trim().Split('\n');
        Require(lines.Length is >= 2 and <= 26 && lines[0].Trim() == "reference,creditor_id,creditor_name,principal,interest,fees,credits,minimum", "Use the sample CSV header and at most 25 accounts.");
        var additions = new List<CollectCase>();
        foreach (var line in lines.Skip(1))
        {
            var values = line.Trim().Split(',').Select(x => x.Trim()).ToArray();
            Require(values.Length == 8 && values.Take(3).All(x => x.Length is > 0 and <= 100), "Check the CSV account fields.");
            Require(!client.Cases.Concat(additions).Any(x => x.Reference == values[0]), "An account with this reference already exists.", 409);
            var item = new CollectCase { Reference = values[0], CreditorId = values[1], CreditorName = values[2], PrincipalCents = Money(values[3]), InterestCents = Money(values[4]), FeesCents = Money(values[5]), CreditsCents = Money(values[6]), MinimumSettlementCents = Money(values[7]), ItemizationDate = DateOnly.FromDateTime(now.UtcDateTime) };
            Require(item.BalanceCents > 0 && item.MinimumSettlementCents <= item.BalanceCents, "Check the balance and authorized minimum.");
            additions.Add(item);
        }
        Require(client.Cases.Count + additions.Count <= 100, "This pilot supports up to 100 accounts per workspace.");
        client.Cases.AddRange(additions); client.Imports.Add(key); Audit(client, actor, "accounts.imported", now);
    }
}
