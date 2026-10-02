using BlenderRenderHeadless;
using System.IO;
using System.Text.Json;
using System.Reflection;

static class RetryCleanupTests
{
    public static void Run()
    {
        void Check(bool ok,string label) { if(!ok) throw new Exception(label); }
        var job=new RenderJob{Status="Failed"};
        Check(AutomaticRetryPolicy.Eligible(job,true,false,0),"retry enabled");
        Check(!AutomaticRetryPolicy.Eligible(job,false,false,0),"disabled");
        Check(!AutomaticRetryPolicy.Eligible(job,true,true,0),"cancelled");
        Check(!AutomaticRetryPolicy.Eligible(job,true,false,3),"bounded retry limit");
        job.Status="Complete"; Check(!AutomaticRetryPolicy.Eligible(job,true,false,0),"no retry success");
        var settings=typeof(MainWindow).GetNestedType("AppSettings",BindingFlags.NonPublic)!;
        var legacy=JsonSerializer.Deserialize("{}",settings)!;
        Check((bool)settings.GetProperty("AutoRetry")!.GetValue(legacy)!,"legacy settings default enabled");
        var optedOut=JsonSerializer.Deserialize("{\"AutoRetry\":false}",settings)!;
        Check(!(bool)settings.GetProperty("AutoRetry")!.GetValue(optedOut)!,"explicit opt-out preserved");
        var root=Path.Combine(Path.GetTempPath(),"BRH-cleanup-"+Guid.NewGuid().ToString("N"));
        var watch=Path.Combine(root,"CloudRender"); Directory.CreateDirectory(watch);
        var outside=Path.Combine(root,"render.png"); File.WriteAllText(outside,"good frame");
        var claims=Path.Combine(watch,"_claims","job"); Directory.CreateDirectory(claims);
        File.WriteAllText(Path.Combine(watch,"job.renderjob.json"),"test manifest");
        var claim=Path.Combine(claims,"1.claim"); File.WriteAllText(claim,"active");
        bool blocked=false;try{WatchCleanup.ArchiveContents(watch);}catch(IOException){blocked=true;}
        Check(blocked && File.Exists(claim),"active claims refused without moves"); File.Delete(claim);
        var lockPath=Path.Combine(claims,"export-MP4.lock");
        using(var handle=new FileStream(lockPath,FileMode.Create,FileAccess.ReadWrite,FileShare.None))
        { blocked=false;try{WatchCleanup.ArchiveContents(watch);}catch(IOException){blocked=true;} Check(blocked,"active export refused"); }
        File.WriteAllText(Path.Combine(claims,"1.done"),"receipt");
        var backup=WatchCleanup.ArchiveContents(watch);
        Check(Directory.Exists(watch) && !Directory.EnumerateFileSystemEntries(watch).Any(),"watch directory empty and preserved");
        Check(File.Exists(Path.Combine(backup,"_claims","job","1.done")) && File.Exists(Path.Combine(backup,"job.renderjob.json")),"files and subfolders recoverable");
        Check(File.ReadAllText(outside)=="good frame","outside render untouched");
        blocked=false;try{WatchCleanup.Validate(root);}catch(IOException){blocked=true;}Check(blocked,"unintended folder refused");
        Console.WriteLine("PASS automatic retry policy, defaults, opt-out, active claim/export refusal, recoverable cleanup, exact scope. Fixtures: "+root);
    }
}
