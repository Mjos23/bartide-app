using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.FileProviders;
using TideCasa.Blazor.Features.Collect;
using Npgsql;

internal static class Program
{
    private static readonly DateTimeOffset Now = DateTimeOffset.Parse("2026-09-26T12:00:00+00:00");
    private static readonly List<CheckResult> Results = [];
    private static string EvidenceRoot = "";
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };
    private static string? PgConnection;
    public static async Task<int> Main()
    {
        var project = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../.."));
        EvidenceRoot = Path.Combine(project, ".evidence", DateTime.UtcNow.ToString("yyyyMMdd-HHmmss") + "-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(EvidenceRoot);
        PgConnection = Environment.GetEnvironmentVariable("CollectCheckPostgres");
        if (PgConnection != null)
        {
            var settings = new NpgsqlConnectionStringBuilder(PgConnection);
            if (settings.Host is not ("127.0.0.1" or "localhost" or "::1") || !settings.Database!.StartsWith("collect_check_", StringComparison.Ordinal))
                throw new InvalidOperationException("PostgreSQL verification requires a loopback, uniquely named collect_check_ database.");
        }
        await Batch1();
        Batch2();
        Batch3();
        Batch4();
        await Batch5();
        Batch6();
        await Batch7();
        Batch8();
        var summary = new
        {
            completed = true, passed = Results.All(x => x.Passed), checks = Results.Count,
            failures = Results.Count(x => !x.Passed), fixedClock = Now,
            persistence = PgConnection == null ? "encrypted local file" : "isolated loopback PostgreSQL",
            scope = "Core and persistence checks. Linked exact production files; no HTTP, browser, legal, live provider, or real-data launch claim.",
            sourceSha256 = new Dictionary<string,string>
            {
                ["CollectModel.cs"] = HashFile(Path.Combine(project,"../TideCasa.Blazor/Features/Collect/CollectModel.cs")),
                ["CollectRepository.cs"] = HashFile(Path.Combine(project,"../TideCasa.Blazor/Features/Collect/CollectRepository.cs"))
            },
            results = Results
        };
        await File.WriteAllTextAsync(Path.Combine(EvidenceRoot,"summary.json"), JsonSerializer.Serialize(summary,Json));
        Console.WriteLine($"SUMMARY {Results.Count(x=>x.Passed)}/{Results.Count} passed; {summary.failures} failed; evidence={EvidenceRoot}");
        return summary.passed ? 0 : 1;
    }
    private static string HashFile(string p) => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(p)));
    private static void Test(int batch, string label, Action body) => TestAsync(batch,label,()=>{body();return Task.CompletedTask;}).GetAwaiter().GetResult();
    private static async Task TestAsync(int batch,string label,Func<Task> body)
    {
        var watch=Stopwatch.StartNew();
        try { await body(); Results.Add(new(batch,label,true,watch.ElapsedMilliseconds,null)); Console.WriteLine($"PASS {batch:000} {label}"); }
        catch(Exception error) { Results.Add(new(batch,label,false,watch.ElapsedMilliseconds,error.GetType().Name+": "+error.Message)); Console.WriteLine($"FAIL {batch:000} {label}: {error.GetType().Name}: {error.Message}"); }
    }
    private static void Check(bool condition,string detail="Requirement was not satisfied") { if(!condition) throw new InvalidOperationException(detail); }
    private static void Denied(Action action,int? status=null)
    {
        try { action(); }
        catch(CollectFault e) { if(status!=null) Check(e.Status==status,$"Expected status {status}, got {e.Status}"); return; }
        throw new InvalidOperationException("Expected rejection; operation succeeded.");
    }
    private static async Task DeniedAsync(Func<Task> action,int? status=null)
    {
        try { await action(); }
        catch(CollectFault e) { if(status!=null) Check(e.Status==status,$"Expected status {status}, got {e.Status}"); return; }
        throw new InvalidOperationException("Expected rejection; operation succeeded.");
    }
    private static CollectClient Fixture() => CollectRules.Fixture(Guid.NewGuid().ToString("N"),Now);
    private static CollectActor Actor(CollectClient c,string role="admin") => new(c.Id+":"+role,role+"@example.invalid",c.Preview);
    private static T Copy<T>(T value) => JsonSerializer.Deserialize<T>(JsonSerializer.Serialize(value))!;
    private static DateOnly First => DateOnly.FromDateTime(Now.UtcDateTime).AddDays(10);
    private static CollectEvidence Evidence(long? cents=180000,string period="biweekly",string basis="net",string status="uploaded",string scan="clean")
        => new(Guid.NewGuid().ToString("N"),"paystub","fictional-paystub.txt","text/plain",512,CollectRules.Hash("synthetic"),scan,status,cents,period,basis,
               DateOnly.FromDateTime(Now.UtcDateTime).AddDays(-14),DateOnly.FromDateTime(Now.UtcDateTime).AddDays(-1),Now,
               Expires:status=="document-reviewed"?Now.AddDays(30):null,Synthetic:true);
    private static CollectCase Agreed(CollectClient c,long amount=120001,int installments=3)
    {
        var item=c.Cases[0]; CollectRules.Negotiate(c,Actor(c,"debtor"),item,amount,installments,First,"Synthetic settlement terms",Now);
        CollectRules.Accept(c,Actor(c,"negotiator"),item,item.Offers[^1].Id,Now); return item;
    }
    private static (CollectRepository Repo,IDataProtectionProvider Keys,string Folder) Repo(string label,string? folder=null)
    {
        folder ??= Path.Combine(EvidenceRoot,label+"-"+Guid.NewGuid().ToString("N")[..8]); Directory.CreateDirectory(folder);
        var keys=DataProtectionProvider.Create(new DirectoryInfo(Path.Combine(folder,"keys")),b=>b.SetApplicationName("Collect.Checks"));
        var values=new Dictionary<string,string?> {["Collect:DevelopmentPath"]=Path.Combine(folder,"data")};
        if(PgConnection!=null) values["ConnectionStrings:Application"]=PgConnection;
        var cfg=new ConfigurationBuilder().AddInMemoryCollection(values).Build();
        return (new CollectRepository(cfg,new TestEnvironment(folder),keys),keys,folder);
    }
    private static async Task Batch1()
    {
        Test(1,"Tenant A staff cannot read tenant B case",()=>{
            var a=Fixture();var b=Fixture();Denied(()=>CollectRules.Case(b,Actor(a),b.Cases[0].Id),404);});
        Test(1,"Debtor cannot read another debtor case",()=>{
            var c=Fixture();Denied(()=>CollectRules.Case(c,Actor(c,"debtor"),c.Cases[1].Id),404);});
        Test(1,"Restaurant role grants no financial staff access",()=>{
            var c=Fixture();c.Members.Add(new(c.Id+":manager","manager")); Check(!CollectRules.Staff(c,Actor(c,"manager")));
            Denied(()=>CollectRules.Case(c,Actor(c,"manager"),c.Cases[0].Id),404);});
        Test(1,"Preview identity cannot read real client",()=>{
            var c=Fixture();c.Preview=false;Denied(()=>CollectRules.Case(c,new(c.Id+":debtor","debtor@example.invalid",true),c.Cases[0].Id),404);});
        Test(1,"Reviewer cannot negotiate",()=>{
            var c=Fixture();Denied(()=>CollectRules.Negotiate(c,Actor(c,"reviewer"),c.Cases[0],120000,2,First,"",Now),404);});
        Test(1,"Preview identity cannot propose against real client",()=>{
            var c=Fixture();c.Preview=false;Denied(()=>CollectRules.Negotiate(c,new(c.Id+":debtor","debtor@example.invalid",true),c.Cases[0],120000,2,First,"",Now));});
        Test(1,"A client reviewer cannot review an injected B client case",()=>{
            var a=Fixture();var b=Fixture();b.Cases[0].Evidence.Add(Evidence());
            Denied(()=>CollectRules.Review(a,Actor(a,"reviewer"),b.Cases[0],b.Cases[0].Evidence[0].Id,"document-reviewed",Now));});
        Test(1,"Debtor profile mutation requires case membership in supplied tenant",()=>{
            var a=Fixture();var b=Fixture();var stranger=b.Cases[0];stranger.DebtorId=Actor(a,"debtor").Id;
            Denied(()=>CollectRules.SetProfile(a,Actor(a,"debtor"),stranger,stranger.Profile!,Now));});
        await TestAsync(1,"Persistent revision survives repository restart",async()=>{
            var p=Repo("restart");var c=Fixture();
            using(p.Repo){await p.Repo.Create(c,Now);c.Name="Fictional persist check";await p.Repo.Save(c,0,Now);}
            var restarted=Repo("restart",p.Folder);
            using(restarted.Repo){var read=await restarted.Repo.Read(c.Id,Now);Check(read?.Revision==1&&read.Name=="Fictional persist check");}
        });
        await TestAsync(1,"Stale revision is rejected without changing stored value",async()=>{
            var p=Repo("revision");using var repo=p.Repo;var c=Fixture();await repo.Create(c,Now);
            var a=(await repo.Read(c.Id,Now))!;var b=(await repo.Read(c.Id,Now))!;a.Name="Accepted winner";await repo.Save(a,0,Now);
            b.Name="Stale loser";await DeniedAsync(()=>repo.Save(b,0,Now),409);Check((await repo.Read(c.Id,Now))!.Name=="Accepted winner");
        });
        await TestAsync(1,"Workspace expiration blocks read and save",async()=>{
            var p=Repo("expiry");using var repo=p.Repo;var c=Fixture();await repo.Create(c,Now);
            Check(await repo.Read(c.Id,Now.AddDays(2))==null);await DeniedAsync(()=>repo.Save(c,0,Now.AddDays(2)),409);
        });
        await TestAsync(1,"Storage refuses nonpreview client creation",async()=>{
            var p=Repo("real");using var repo=p.Repo;var c=Fixture();c.Preview=false;await DeniedAsync(()=>repo.Create(c,Now),409);
        });
        await TestAsync(1,"Storage rejects path and nonidentifier input",async()=>{
            var p=Repo("id");using var repo=p.Repo;await DeniedAsync(async()=>{await repo.Read("../outside",Now);},404);
        });
        await TestAsync(1,"Persisted document is encrypted, with tenant-bound purpose",async()=>{
            var p=Repo("cipher");using var repo=p.Repo;var c=Fixture();await repo.Create(c,Now);
            if(PgConnection==null){
                var data=Path.Combine(p.Folder,"data");var file=Path.Combine(data,c.Id+".protected");var raw=await File.ReadAllTextAsync(file);
                Check(!raw.Contains(c.Name)&&!raw.Contains(c.Cases[0].DebtorId!)&&!raw.Contains("IncomeCents"),"Plaintext appeared in persisted payload");
                var other=Guid.NewGuid().ToString("N");await File.WriteAllTextAsync(Path.Combine(data,other+".protected"),raw);
                try{await repo.Read(other,Now);throw new InvalidOperationException("Ciphertext transplantation was accepted");}catch(CryptographicException){}
            } else {
                await using var connection=new NpgsqlConnection(PgConnection);await connection.OpenAsync();
                await using var cmd=new NpgsqlCommand("SELECT ciphertext FROM tide_collect.preview_workspaces WHERE id=@id",connection);cmd.Parameters.AddWithValue("id",c.Id);
                var raw=(string)(await cmd.ExecuteScalarAsync())!;Check(!raw.Contains(c.Name)&&!raw.Contains("IncomeCents"));
            }
        });
    }
    private static void Batch2()
    {
        const string csv="reference,creditor_id,creditor_name,principal,interest,fees,credits,minimum\nTEST-1,creditor-test,Fictional Creditor,100.01,1.00,2.00,3.00,50.00";
        Test(2,"Account import preserves exact itemization and distinct creditor",()=>{
            var c=Fixture();CollectRules.Import(c,Actor(c),csv,Now);var item=c.Cases[^1];
            Check(item.BalanceCents==10001&&item.MinimumSettlementCents==5000&&item.CreditorId=="creditor-test"&&item.CreditorId!=c.Id);
            Check(item.DebtorId==null&&!item.AuthorityRecorded&&item.NoticeStatus=="draft");});
        Test(2,"Import retry is idempotent",()=>{
            var c=Fixture();CollectRules.Import(c,Actor(c),csv,Now);var count=c.Cases.Count;var audit=c.Audit.Count;
            CollectRules.Import(c,Actor(c),csv.Replace("\n","\r\n")+"\r\n",Now);Check(c.Cases.Count==count&&c.Audit.Count==audit&&c.Imports.Count==1);});
        Test(2,"Import is admin-only",()=>{
            var c=Fixture();Denied(()=>CollectRules.Import(c,Actor(c,"negotiator"),csv,Now),404);});
        Test(2,"Invalid second import row adds no partial records",()=>{
            var c=Fixture();var count=c.Cases.Count;Denied(()=>CollectRules.Import(c,Actor(c),csv+"\nBAD,creditor-test,Example,abc,0,0,0,10",Now));
            Check(c.Cases.Count==count&&c.Imports.Count==0);});
        Test(2,"Duplicate reference and quoted CSV rejected",()=>{
            var c=Fixture();CollectRules.Import(c,Actor(c),csv,Now);Denied(()=>CollectRules.Import(c,Actor(c),csv.Replace("100.01","100.02"),Now),409);
            Denied(()=>CollectRules.Import(c,Actor(c),csv.Replace("Fictional Creditor","\"Fictional Creditor\""),Now));});
        Test(2,"Invitation is hashed and bound to exact verified email",()=>{
            var c=Fixture();var token=CollectRules.Invite(c,Actor(c),"reviewer2@example.invalid","reviewer",Now);
            Check(c.Invites[0].Hash!=token&&c.Invites[0].Hash==CollectRules.Hash(token));
            Denied(()=>CollectRules.Claim(c,new("outsider","wrong@example.invalid",true),token,Now),404);
            CollectRules.Claim(c,new("new-reviewer","REVIEWER2@example.invalid",true),token,Now);
            Check(c.Members.Any(x=>x.UserId=="new-reviewer"&&x.Role=="reviewer")&&c.Invites.Count==0);
            Denied(()=>CollectRules.Claim(c,new("new-reviewer","reviewer2@example.invalid",true),token,Now),404);});
        Test(2,"Invitation expiry and crosspreview claim rejected",()=>{
            var c=Fixture();var token=CollectRules.Invite(c,Actor(c),"new@example.invalid","reviewer",Now);
            Denied(()=>CollectRules.Claim(c,new("new","new@example.invalid",true),token,Now.AddDays(8)),404);
            Denied(()=>CollectRules.Claim(c,new("new","new@example.invalid",false),token,Now),404);});
        Test(2,"Reviewer cannot issue invitations and admin invitation role is forbidden",()=>{
            var c=Fixture();Denied(()=>CollectRules.Invite(c,Actor(c,"reviewer"),"x@example.invalid","reviewer",Now),404);
            Denied(()=>CollectRules.Invite(c,Actor(c),"x@example.invalid","admin",Now));});
    }
    private static void Batch3()
    {
        Test(3,"Debtor invitation claims only its designated unclaimed account",()=>{
            var c=Fixture();var item=c.Cases[1];item.DebtorId=null;var token=CollectRules.Invite(c,Actor(c),"claim@example.invalid","debtor",Now,item.Id);
            CollectRules.Claim(c,new("new-debtor","claim@example.invalid",true),token,Now);
            Check(item.DebtorId=="new-debtor");Denied(()=>CollectRules.Case(c,new("new-debtor","claim@example.invalid",true),c.Cases[0].Id),404);});
        Test(3,"Missing income does not hide debt account or itemization",()=>{
            var c=Fixture();c.Cases[0].Profile=null;var item=CollectRules.Case(c,Actor(c,"debtor"),c.Cases[0].Id);Check(item.BalanceCents==240000&&item.NoticeStatus=="issued");});
        Test(3,"Unissued notice or missing authority prevents negotiations",()=>{
            var c=Fixture();var item=c.Cases[0];item.NoticeStatus="draft";Denied(()=>CollectRules.Negotiate(c,Actor(c,"debtor"),item,120000,2,First,"",Now),409);
            item.NoticeStatus="issued";item.AuthorityRecorded=false;Denied(()=>CollectRules.Negotiate(c,Actor(c,"debtor"),item,120000,2,First,"",Now),409);});
        Test(3,"Expense profile validates uniqueness, basis, and pay period",()=>{
            var c=Fixture();var item=c.Cases[0];var actor=Actor(c,"debtor");
            Denied(()=>CollectRules.SetProfile(c,actor,item,new(100,"invalid","net",[],Now),Now));
            Denied(()=>CollectRules.SetProfile(c,actor,item,new(100,"monthly","unknown",[],Now),Now));
            Denied(()=>CollectRules.SetProfile(c,actor,item,new(100,"monthly","net",[new("food",100),new("food",200)],Now),Now));
            Denied(()=>CollectRules.SetProfile(c,actor,item,new(100,"monthly","net",[new("food",-1)],Now),Now));});
    }
    private static void Batch4()
    {
        Test(4,"Money parser accepts cents and rejects silent rounding or exponent notation",()=>{
            Check(CollectRules.Money("123.45")==12345&&CollectRules.Money("0")==0);
            foreach(var invalid in new[]{"1.001","-1","1e3","1,000.00","NaN"}) Denied(()=>CollectRules.Money(invalid));});
        Test(4,"Income periods normalize using explicit annual frequencies",()=>{
            Check(CollectRules.Monthly(1200,"weekly")==5200&&CollectRules.Monthly(1200,"biweekly")==2600&&CollectRules.Monthly(1200,"semimonthly")==2400&&CollectRules.Monthly(1200,"monthly")==1200);});
        Test(4,"Variance direction is reviewed minus reported, denominator reported",()=>{
            var p=new CollectProfile(400000,"monthly","net",[],Now);var ev=Evidence(180000,"semimonthly","net","document-reviewed");
            var comparison=CollectRules.Compare(p,ev,Now);Check(comparison.Difference==-40000m&&comparison.Percent==-10m);});
        Test(4,"Gross/net mismatch, missing, stale and unreviewed evidence remain uncomparable",()=>{
            var p=new CollectProfile(100,"monthly","net",[],Now);var ev=Evidence(100,"monthly","gross","document-reviewed");
            Check(CollectRules.Compare(p,ev,Now).Difference==null);
            Check(CollectRules.Compare(p,null,Now).Percent==null&&CollectRules.Compare(null,ev,Now).Percent==null);
            Check(CollectRules.Compare(p,ev with{Basis="net",Expires=Now},Now).Difference==null);
            Check(CollectRules.Compare(p,ev with{Basis="net",Status="uploaded"},Now).Difference==null);});
        Test(4,"Zero denominator yields missing percentage, not perfect match",()=>{
            var p=new CollectProfile(0,"monthly","net",[],Now);var x=CollectRules.Compare(p,Evidence(500,"monthly","net","document-reviewed"),Now);
            Check(x.Difference==500&&x.Percent==null&&x.Reason.Contains("zero"));});
        Test(4,"Reviewed evidence without expiry is not accepted as current",()=>{
            var p=new CollectProfile(100,"monthly","net",[],Now);Check(CollectRules.Compare(p,Evidence(100,"monthly","net","document-reviewed") with{Expires=null},Now).Difference==null);});
        Test(4,"Review requires clean scan and rejects source-verified claim",()=>{
            var c=Fixture();var item=c.Cases[0];var ev=Evidence();CollectRules.AddEvidence(c,Actor(c,"debtor"),item,ev,Now);
            Denied(()=>CollectRules.Review(c,Actor(c,"reviewer"),item,ev.Id,"source-verified",Now));
            item.Evidence[0]=ev with{Scan="quarantined"};Denied(()=>CollectRules.Review(c,Actor(c,"reviewer"),item,ev.Id,"document-reviewed",Now));
            item.Evidence[0]=ev;CollectRules.Review(c,Actor(c,"reviewer"),item,ev.Id,"document-reviewed",Now);
            Check(item.Evidence[0].Reviewer==Actor(c,"reviewer").Id&&item.Evidence[0].Expires==Now.AddDays(30)&&item.Findings.Count==1);});
        Test(4,"Review and evidence replacement preserve prior versions",()=>{
            var c=Fixture();var item=c.Cases[0];var ev=Evidence();CollectRules.AddEvidence(c,Actor(c,"debtor"),item,ev,Now);
            CollectRules.Review(c,Actor(c,"reviewer"),item,ev.Id,"document-reviewed",Now);
            Denied(()=>CollectRules.Review(c,Actor(c,"reviewer"),item,ev.Id,"document-reviewed",Now),409);
            CollectRules.AddEvidence(c,Actor(c,"debtor"),item,Evidence(170000) with{Replaces=ev.Id},Now);
            Check(item.Evidence.Count==2&&item.Evidence[0].IncomeCents==180000&&item.Evidence[0].Status=="document-reviewed");});
        Test(4,"Customer budget can be recorded for creditor review without implying source verification",()=>{
            var c=Fixture();var item=c.Cases[0];var budget=Evidence(null) with{Kind="budget"};
            CollectRules.AddEvidence(c,Actor(c,"debtor"),item,budget,Now);
            CollectRules.Review(c,Actor(c,"reviewer"),item,budget.Id,"document-reviewed",Now);
            Check(item.Evidence[0].Kind=="budget"&&item.Evidence[0].Status=="document-reviewed");
            Check(CollectRules.Compare(item.Profile,item.Evidence[0],Now).Percent==null);});
        Test(4,"Evidence upload rejects invalid size, future period, unknown replacement and foreign debtor",()=>{
            var c=Fixture();var item=c.Cases[0];var ev=Evidence();var actor=Actor(c,"debtor");
            Denied(()=>CollectRules.AddEvidence(c,actor,item,ev with{Bytes=4194305},Now));
            Denied(()=>CollectRules.AddEvidence(c,actor,item,ev with{PeriodEnd=First},Now));
            Denied(()=>CollectRules.AddEvidence(c,actor,item,ev with{Replaces="missing"},Now));
            Denied(()=>CollectRules.AddEvidence(c,Actor(c,"other-debtor"),item,ev,Now),404);});
    }
    private static async Task Batch5()
    {
        Test(5,"Counteroffer supersedes prior version; only current offer accepts",()=>{
            var c=Fixture();var item=c.Cases[0];CollectRules.Negotiate(c,Actor(c,"debtor"),item,120000,3,First,"original",Now);var old=item.Offers[0].Id;
            CollectRules.Negotiate(c,Actor(c,"negotiator"),item,150000,3,First,"counter",Now);Check(item.Offers[0].State=="superseded"&&item.Offers[1].Version==2);
            Denied(()=>CollectRules.Accept(c,Actor(c,"negotiator"),item,old,Now),409);CollectRules.Accept(c,Actor(c,"debtor"),item,item.Offers[1].Id,Now);Check(item.Agreement?.TotalCents==150000);});
        Test(5,"Proposer cannot accept own proposal; expired offer cannot accept",()=>{
            var c=Fixture();var item=c.Cases[0];CollectRules.Negotiate(c,Actor(c,"debtor"),item,120000,3,First,"",Now);var offer=item.Offers[0];
            Denied(()=>CollectRules.Accept(c,Actor(c,"debtor"),item,offer.Id,Now),403);
            Denied(()=>CollectRules.Accept(c,Actor(c,"negotiator"),item,offer.Id,Now.AddDays(14)),409);});
        Test(5,"Below-authority settlement remains unaccepted",()=>{
            var c=Fixture();var item=c.Cases[0];CollectRules.Negotiate(c,Actor(c,"debtor"),item,119999,3,First,"",Now);
            Denied(()=>CollectRules.Accept(c,Actor(c,"negotiator"),item,item.Offers[^1].Id,Now),409);Check(item.Agreement==null);});
        Test(5,"Installments sum exactly, including remainder cents and calendar month end",()=>{
            var c=Fixture();var item=c.Cases[0];var first=new DateOnly(2027,1,31);
            CollectRules.Negotiate(c,Actor(c,"debtor"),item,120001,3,first,"Final fixed terms",Now);CollectRules.Accept(c,Actor(c,"negotiator"),item,item.Offers[^1].Id,Now);
            var a=item.Agreement!;Check(a.Schedule.Select(x=>x.Cents).SequenceEqual(new long[]{40001,40000,40000})&&a.Schedule.Sum(x=>x.Cents)==a.TotalCents);
            Check(a.Schedule[1].Due==new DateOnly(2027,2,28)&&a.Schedule[2].Due==new DateOnly(2027,3,31));});
        Test(5,"Accepted agreement freezes identity, terms and amount after later changes",()=>{
            var c=Fixture();var item=Agreed(c);var before=JsonSerializer.Serialize(item.Agreement);
            CollectRules.SetProfile(c,Actor(c,"debtor"),item,new(999999,"monthly","gross",[new("food",123)],Now),Now.AddMinutes(1));
            c.Name="Changed future client name";item.CreditorName="Changed future creditor";item.CreditorId="other-payee";
            Check(JsonSerializer.Serialize(item.Agreement)==before);});
        Test(5,"Open dispute halts proposals and acceptance",()=>{
            var c=Fixture();var item=c.Cases[0];CollectRules.Negotiate(c,Actor(c,"debtor"),item,120000,3,First,"",Now);item.Disputes.Add(new("dispute","balance",Now));
            Denied(()=>CollectRules.Negotiate(c,Actor(c,"negotiator"),item,130000,3,First,"",Now),409);
            Denied(()=>CollectRules.Accept(c,Actor(c,"negotiator"),item,item.Offers[^1].Id,Now),409);});
        Test(5,"Withdrawn offer is immutable and cannot be accepted",()=>{
            var c=Fixture();var item=c.Cases[0];CollectRules.Negotiate(c,Actor(c,"debtor"),item,120000,3,First,"",Now);var id=item.Offers[^1].Id;
            Denied(()=>CollectRules.Withdraw(c,Actor(c,"negotiator"),item,id,Now),403);
            CollectRules.Withdraw(c,Actor(c,"debtor"),item,id,Now);Denied(()=>CollectRules.Accept(c,Actor(c,"negotiator"),item,id,Now),409);});
        Test(5,"Preview debtor cannot accept real client's proposal",()=>{
            var c=Fixture();var item=c.Cases[0];CollectRules.Negotiate(c,Actor(c,"negotiator"),item,120000,3,First,"",Now);
            c.Preview=false;Denied(()=>CollectRules.Accept(c,new(c.Id+":debtor","debtor@example.invalid",true),item,item.Offers[^1].Id,Now));});
        await TestAsync(5,"Concurrent saves allow exactly one revision winner",async()=>{
            var p=Repo("concurrency");using var repo=p.Repo;var c=Fixture();await repo.Create(c,Now);
            var first=(await repo.Read(c.Id,Now))!;var second=(await repo.Read(c.Id,Now))!;
            CollectRules.Negotiate(first,Actor(first,"debtor"),first.Cases[0],120000,3,First,"winner A",Now);
            CollectRules.Negotiate(second,Actor(second,"debtor"),second.Cases[0],130000,3,First,"winner B",Now);
            async Task<bool> Save(CollectClient value){try{await repo.Save(value,0,Now);return true;}catch(CollectFault e)when(e.Status==409){return false;}}
            var results=await Task.WhenAll(Save(first),Save(second));Check(results.Count(x=>x)==1);
            var stored=(await repo.Read(c.Id,Now))!;Check(stored.Revision==1&&stored.Cases[0].Offers.Count==1);
        });
    }
    private static void Batch6()
    {
        Test(6,"Payment uses accepted creditor and server installment cents",()=>{
            var c=Fixture();var item=Agreed(c);var agreed=item.Agreement!;item.CreditorId="tampered-future-id";item.PrincipalCents=1;
            var payment=CollectRules.BeginPayment(c,Actor(c,"debtor"),item,1,Now);
            Check(payment.CreditorId==agreed.CreditorId&&payment.Cents==40001&&payment.AgreementId==agreed.Id&&payment.Simulated&&payment.State=="pending");});
        Test(6,"Payment creation and event replay are idempotent",()=>{
            var c=Fixture();var item=Agreed(c);var p=CollectRules.BeginPayment(c,Actor(c,"debtor"),item,1,Now);var duplicate=CollectRules.BeginPayment(c,Actor(c,"debtor"),item,1,Now);
            Check(p.Id==duplicate.Id&&item.Payments.Count==1);CollectRules.SimulateEvent(c,Actor(c,"debtor"),item,p.Id,"evt1",1,"succeeded",Now);
            var audit=c.Audit.Count;CollectRules.SimulateEvent(c,Actor(c,"debtor"),item,p.Id,"evt1",2,"settled",Now);
            Check(item.Payments[0].State=="succeeded"&&c.Audit.Count==audit&&item.Payments[0].EventIds!.Count==1);});
        Test(6,"Out-of-order event is rejected; stale sequence cannot roll payment back",()=>{
            var c=Fixture();var item=Agreed(c);var p=CollectRules.BeginPayment(c,Actor(c,"debtor"),item,1,Now);
            Denied(()=>CollectRules.SimulateEvent(c,Actor(c,"debtor"),item,p.Id,"early",2,"settled",Now),409);
            CollectRules.SimulateEvent(c,Actor(c,"debtor"),item,p.Id,"ok",3,"succeeded",Now);
            CollectRules.SimulateEvent(c,Actor(c,"debtor"),item,p.Id,"late",1,"failed",Now);Check(item.Payments[0].State=="succeeded");});
        Test(6,"Successful payment settles, then return reverses final state",()=>{
            var c=Fixture();var item=Agreed(c);var p=CollectRules.BeginPayment(c,Actor(c,"debtor"),item,1,Now);
            CollectRules.SimulateEvent(c,Actor(c,"debtor"),item,p.Id,"a",1,"succeeded",Now);CollectRules.SimulateEvent(c,Actor(c,"debtor"),item,p.Id,"b",2,"settled",Now);
            CollectRules.SimulateEvent(c,Actor(c,"debtor"),item,p.Id,"c",3,"returned",Now);Check(item.Payments[0].State=="returned");
            Denied(()=>CollectRules.SimulateEvent(c,Actor(c,"debtor"),item,p.Id,"d",4,"settled",Now),409);});
        Test(6,"Settled payment can refund and failed payment is not paid",()=>{
            var c=Fixture();var item=Agreed(c);var a=Actor(c,"debtor");var p=CollectRules.BeginPayment(c,a,item,1,Now);
            CollectRules.SimulateEvent(c,a,item,p.Id,"a",1,"succeeded",Now);CollectRules.SimulateEvent(c,a,item,p.Id,"b",2,"settled",Now);CollectRules.SimulateEvent(c,a,item,p.Id,"c",3,"refunded",Now);
            var failed=CollectRules.BeginPayment(c,a,item,2,Now);CollectRules.SimulateEvent(c,a,item,failed.Id,"f",1,"failed",Now);
            Check(item.Payments[0].State=="refunded"&&item.Payments[1].State=="failed");});
        Test(6,"Live provider unavailable and unrelated user cannot simulate",()=>{
            var c=Fixture();var item=Agreed(c);Denied(()=>CollectRules.BeginPayment(c,Actor(c,"other-debtor"),item,1,Now),404);
            c.Preview=false;Denied(()=>CollectRules.BeginPayment(c,Actor(c,"debtor"),item,1,Now),409);});
        Test(6,"Open dispute prevents starting payment",()=>{
            var c=Fixture();var item=Agreed(c);item.Disputes.Add(new("d","account",Now));Denied(()=>CollectRules.BeginPayment(c,Actor(c,"debtor"),item,1,Now),409);});
    }
    private static async Task Batch7()
    {
        Test(7,"Profile inquiry without consent shares no financial fields",()=>{
            var c=Fixture();CollectRules.Inquire(c,Actor(c,"debtor"),"sample-creditor-1",false,Now);
            Check(c.Inquiries.Count==1&&c.Inquiries[0].SharedProfile==null&&c.Personal.Inquiries[0].SharedProfile==null);});
        Test(7,"Consented inquiry contains exact snapshot and never updates automatically",()=>{
            var c=Fixture();var original=JsonSerializer.Serialize(c.Personal.Profile);CollectRules.Inquire(c,Actor(c,"debtor"),"sample-creditor-1",true,Now);
            Check(JsonSerializer.Serialize(c.Inquiries[0].SharedProfile)==original);
            c.Personal.Profile!.Expenses.Add(new("other",200));c.Personal.Profile=c.Personal.Profile with{IncomeCents=777777};
            Check(JsonSerializer.Serialize(c.Inquiries[0].SharedProfile)==original&&JsonSerializer.Serialize(c.Personal.Inquiries[0].SharedProfile)==original);});
        Test(7,"Inquiry is owner-only, requires profile to share, and deduplicates pending creditor",()=>{
            var c=Fixture();Denied(()=>CollectRules.Inquire(c,Actor(c,"other-debtor"),"sample-creditor-1",true,Now),404);
            c.Personal.Profile=null;Denied(()=>CollectRules.Inquire(c,Actor(c,"debtor"),"sample-creditor-1",true,Now));
            CollectRules.Inquire(c,Actor(c,"debtor"),"sample-creditor-1",false,Now);Denied(()=>CollectRules.Inquire(c,Actor(c,"debtor"),"sample-creditor-1",false,Now),409);});
        await TestAsync(7,"Synthetic end-to-end workflow survives encrypted restart",async()=>{
            var p=Repo("workflow");var c=Fixture();var actor=Actor(c,"debtor");var item=c.Cases[0];
            using(p.Repo){
                await p.Repo.Create(c,Now);CollectRules.SetProfile(c,actor,item,new(170000,"biweekly","net",[new("housing",140000)],Now),Now);
                var evidence=Evidence(160000);CollectRules.AddEvidence(c,actor,item,evidence,Now);CollectRules.Review(c,Actor(c,"reviewer"),item,evidence.Id,"document-reviewed",Now);
                CollectRules.Negotiate(c,actor,item,120001,3,First,"Synthetic settlement",Now);CollectRules.Accept(c,Actor(c,"negotiator"),item,item.Offers[^1].Id,Now);
                var payment=CollectRules.BeginPayment(c,actor,item,1,Now);CollectRules.SimulateEvent(c,actor,item,payment.Id,"received",1,"succeeded",Now);
                CollectRules.SimulateEvent(c,actor,item,payment.Id,"settled",2,"settled",Now);CollectRules.Inquire(c,actor,"sample-creditor-2",true,Now);await p.Repo.Save(c,0,Now);
            }
            var restarted=Repo("workflow",p.Folder);using(restarted.Repo){var stored=(await restarted.Repo.Read(c.Id,Now))!;
                Check(stored.Cases[0].Agreement?.TotalCents==120001&&stored.Cases[0].Payments[0].State=="settled"&&stored.Inquiries[0].SharedProfile!=null);
                Check(stored.Audit.Any(x=>x.Action=="evidence.reviewed")&&stored.Audit.Any(x=>x.Action=="agreement.accepted")); }
        });
        await TestAsync(7,"Delete removes only specified workspace",async()=>{
            var p=Repo("deletion");using var repo=p.Repo;var a=Fixture();var b=Fixture();await repo.Create(a,Now);await repo.Create(b,Now);await repo.Delete(a.Id);
            Check(await repo.Read(a.Id,Now)==null&&await repo.Read(b.Id,Now)!=null);});
    }
    private static void Batch8()
    {
        Test(8,"Analytics preserves missing-data count and signed normalized differences",()=>{
            var p=new CollectProfile(400000,"monthly","net",[],Now);
            var rows=new[]{CollectRules.Compare(p,Evidence(180000,"semimonthly","net","document-reviewed"),Now),
                CollectRules.Compare(p,Evidence(220000,"semimonthly","net","document-reviewed"),Now),CollectRules.Compare(p,null,Now)};
            Check(rows.Length==3&&rows.Count(x=>x.Difference!=null)==2&&rows.Count(x=>x.Difference==null)==1);
            Check(rows[0].Difference==-40000&&rows[1].Difference==40000&&rows[0].Percent==-10&&rows[1].Percent==10);});
        Test(8,"Synthetic 10000-row benchmark has exact expected comparable count",()=>{
            const int count=10000;var p=new CollectProfile(400000,"monthly","net",[],Now);var ev=Evidence(180000,"semimonthly","net","document-reviewed");var measured=Stopwatch.StartNew();
            decimal sum=0;int compared=0,missing=0;
            for(int i=0;i<count;i++){var r=CollectRules.Compare(p,i%4==0?null:ev,Now);if(r.Difference is decimal d){sum+=d;compared++;}else missing++;}
            measured.Stop();Check(compared==7500&&missing==2500&&sum==-300000000m);
            File.WriteAllText(Path.Combine(EvidenceRoot,"synthetic-benchmark.json"),JsonSerializer.Serialize(new{rows=count,compared,missing,totalDifferenceCents=sum,elapsedMilliseconds=measured.Elapsed.TotalMilliseconds,scope="In-memory deterministic comparison; not database or production load testing"},Json));
        });
    }
    private sealed record CheckResult(int Batch,string Check,bool Passed,long Milliseconds,string? Failure);
    private sealed class TestEnvironment(string path):IWebHostEnvironment
    {
        public string ApplicationName {get;set;}="Collect.Checks";
        public IFileProvider WebRootFileProvider {get;set;}=new NullFileProvider();
        public string WebRootPath {get;set;}=Path.Combine(path,"public");
        public string EnvironmentName {get;set;}="Development";
        public string ContentRootPath {get;set;}=path;
        public IFileProvider ContentRootFileProvider {get;set;}=new NullFileProvider();
    }
}
