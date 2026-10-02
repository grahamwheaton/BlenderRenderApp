using BlenderRenderHeadless;
using System.IO;
using System.Text.Json;
using System.Reflection;

static class ClaimStatusTests
{
    public static void Run()
    {
        void Check(bool test,string reason){if(!test)throw new Exception(reason);}
        var root=Path.Combine(Path.GetTempPath(),"BRH-claims-"+Guid.NewGuid().ToString("N"));
        var id=Guid.NewGuid().ToString(); var folder=Path.Combine(root,"_claims",id); Directory.CreateDirectory(folder);
        var job=new RenderJob{Distributed=true,CoordinationFolder=root,JobId=id,StartFrame="1",EndFrame="1"};
        var path=Path.Combine(folder,"1.claim");
        Check(SharedClaimStatus.Read(job)=="","unclaimed");
        File.WriteAllText(path,JsonSerializer.Serialize(new{machine="TEAM-PC"}));
        job.ClaimedBy=SharedClaimStatus.Read(job);
        Check(job.Status=="Waiting" && job.StatusDisplay=="Job claimed by TEAM-PC","claim display preserves queue status");
        File.SetLastWriteTimeUtc(path,DateTime.UtcNow.AddMinutes(-3)); Check(SharedClaimStatus.Read(job)=="","stale ignored");
        File.WriteAllText(path,"{"); Check(SharedClaimStatus.Read(job)=="","partial write ignored");
        job.Status="Complete"; Check(job.StatusDisplay=="Complete","completion replaces claim display");
        var type=typeof(MainWindow).GetNestedType("AppSettings",BindingFlags.NonPublic)!;
        Check(!(bool)type.GetProperty("ForceOverwrite")!.GetValue(JsonSerializer.Deserialize("{}",type))!,"default off for old settings");
        Check((bool)type.GetProperty("ForceOverwrite")!.GetValue(JsonSerializer.Deserialize("{\"ForceOverwrite\":true}",type))!,"explicit choice retained");
        Console.WriteLine("PASS shared still claim owner/status, stale/partial receipts, Force overwrite defaults and persistence.");
    }
}
