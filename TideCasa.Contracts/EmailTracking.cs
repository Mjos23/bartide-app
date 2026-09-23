namespace TideCasa.Contracts;

public sealed record EmailCampaign(string Id, string Name, bool IsTest, string LinkToken, string CreatedAt,
    int Visits, int DemoClicks, int PurchaseClicks, int ContactClicks, int SampleClicks);
public sealed record EmailPathway(string Path, int Visits);
public sealed record EmailCampaignReport(IReadOnlyList<EmailCampaign> Campaigns, IReadOnlyList<EmailPathway> Pathways, string? SelectedId);
public sealed record CreateEmailCampaign(string RequestKey, string Name, bool IsTest);
public sealed record StartEmailVisit(string CampaignToken);
public sealed record EmailVisit(string SessionId);
public sealed record RecordEmailClick(string SessionId, string Path);
public sealed record EmailClickReceipt(bool Recorded);

public static class EmailCampaignDefaults
{
    public const string TestToken = "7ae11d6ce57d4c1fb881ab6f84259bd0";
    public const string Subject = "Finally, a mobile app that puts your bar on everyone’s phone";
    public static string Link(string token) => "https://bar.tide.casa/?campaign=" + token;
    public static readonly IReadOnlyDictionary<string, string> Paths = new Dictionary<string, string>
    {
        ["home"] = "Home", ["demo"] = "Book a demo", ["purchase"] = "Purchase page",
        ["contact"] = "Contact", ["sample"] = "Sample app", ["pricing"] = "Pricing"
    };
}
