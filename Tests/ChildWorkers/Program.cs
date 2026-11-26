using BlenderRenderHeadless;
using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Text.RegularExpressions;

if(args.Contains("--completion-features")) { CompletionFeatureTests.Run(); return; }
if(args.Contains("--queue-ui")) { QueueUiTests.Run(); return; }
if(args.Contains("--repair")) { RepairTests.Run(); return; }
if(args.Contains("--recovery")) { RecoveryTests.Run(); return; }
if(args.Contains("--retry-cleanup")) { RetryCleanupTests.Run(); return; }
if(args.Contains("--claim-status")) { ClaimStatusTests.Run(); return; }
if(args.Contains("--camera-batch-ui")) { CameraBatchUiTests.Run(); return; }
if(args.Contains("--python") && args.Any(a=>Path.GetFileName(a)=="camera_batch.py"))
{
    using var packets=JsonDocument.Parse(File.ReadAllText(args[^1]));
    foreach(var p in packets.RootElement.EnumerateArray())
    {
        var id=p.GetProperty("jobId").GetString();
        Console.WriteLine($"BRH_BATCH_STATUS:{id}|Rendering");
        Console.WriteLine($"BRH_BATCH_LOG:{id}|BRH_FRAME_DONE:7|C:\\fixture\\{id}.png|1|1");
        Console.WriteLine($"BRH_BATCH_STATUS:{id}|Complete");
    }
    return;
}
if(args.Contains("--fake-failure")) { Console.WriteLine("worker started"); Console.Error.WriteLine("TEST missing texture diagnostic"); Environment.Exit(7); return; }
if(args.Contains("--bundled-ffmpeg")) { await BundledFfmpegTests.Run(); return; }

if(args.FirstOrDefault()=="--fake-worker")
{
    var testFolder=args[1]; var total=int.Parse(args[2]);
    Directory.CreateDirectory(testFolder);
    bool Stop()=>Environment.GetEnvironmentVariable("BRH_CHILD_STOP_FILE") is {} stopFile && File.Exists(stopFile);
    for(var frame=1;frame<=total;frame++)
    {
        if(Stop()) return;
        var done=Path.Combine(testFolder,frame+".done"); var claim=Path.Combine(testFolder,frame+".claim");
        if(File.Exists(done)) continue;
        FileStream handle;
        try {handle=new FileStream(claim,FileMode.CreateNew,FileAccess.Write,FileShare.None);}catch(IOException){continue;}
        using(handle)
        {
            if(!File.Exists(done))
            {
                await Task.Delay(80); File.WriteAllText(done,Environment.ProcessId.ToString());
                Console.WriteLine($"BRH_LOCAL_FRAME_DONE:{frame}");
                Console.WriteLine($"BRH_FRAME_DONE:{frame}||{Directory.GetFiles(testFolder,"*.done").Length}|{total}");
            }
        }
        File.Delete(claim);
    }
    while(Directory.GetFiles(testFolder,"*.done").Length<total && !Stop()) await Task.Delay(50);
    return;
}

if(args.Contains("--controller"))
{
    Exception? error=null;
    var ui=new Thread(()=>
    {
        var app=new App(); app.InitializeComponent();
        app.Dispatcher.BeginInvoke(new Action(async()=>
        {
            try
            {
                var window=new MainWindow();
                var tuning=new WorkerTuning{MaxChildren=2,WarmupSeconds=3,CheckSeconds=1,HealthyChecks=1,TrialSeconds=5,MinimumGainPercent=0,MinimumRemainingSeconds=5};
                typeof(MainWindow).GetField("_workerTuning",System.Reflection.BindingFlags.Instance|System.Reflection.BindingFlags.NonPublic)!.SetValue(window,tuning);
                var folder=Path.Combine(Path.GetTempPath(),"BRH-controller-"+Guid.NewGuid().ToString("N"));
                var job=new RenderJob{Distributed=true,CoordinationFolder=folder,JobId="test",RenderMode="PLAYBLAST",StartFrame="1",EndFrame="600",FrameStep="1"};
                Func<Task<WorkerResources?>> resources=()=>Task.FromResult<WorkerResources?>(new(20,20,100,24,1));
                var run=(Task<int>)typeof(MainWindow).GetMethod("RunWithChildrenAsync",System.Reflection.BindingFlags.Instance|System.Reflection.BindingFlags.NonPublic)!.Invoke(window,new object[]{Environment.ProcessPath!,new[]{"--fake-worker",folder,"600"},job,resources})!;
                var exit=await run.WaitAsync(TimeSpan.FromSeconds(90));
                var log=((System.Windows.Controls.TextBox)window.FindName("LogBox")).Text;
                if(exit!=0 || !log.Contains("launched child 2/2") || log.Contains("launched child 3/")) throw new Exception("Controller failed max-child test:\n"+log);
                if(Directory.GetFiles(folder,"*.done").Length!=600 || Directory.GetFiles(folder,"*.claim").Length!=0) throw new Exception("Unfinished controller work");
                if(Directory.GetFiles(folder,"*.done").Select(File.ReadAllText).Distinct().Count()!=3) throw new Exception("Expected 3 actual worker processes");
                window.Close();
                Console.WriteLine("PASS full adaptive controller: primary + 2 children, all 600 frames, no remaining claims");
            }
            catch(Exception ex){error=ex;}
            finally{app.Shutdown();System.Windows.Threading.Dispatcher.CurrentDispatcher.BeginInvokeShutdown(System.Windows.Threading.DispatcherPriority.Background);}
        }));
        System.Windows.Threading.Dispatcher.Run();
    });
    ui.SetApartmentState(ApartmentState.STA);ui.Start();ui.Join();if(error!=null) throw error;return;
}

void Check(bool yes,string label) { if(!yes) throw new Exception(label); Console.WriteLine("PASS "+label); }
RenderJob Job(string mode="PLAYBLAST", bool shared=true,int tiles=0) => new(){RenderMode=mode,Distributed=shared,TileSize=tiles,StartFrame="1",EndFrame="40",CoordinationFolder="test",Format="PNG"};
Check(ChildWorkerPolicy.Eligible(Job()),"shared playblast eligible");
Check(!ChildWorkerPolicy.Eligible(Job("FINAL")) && !ChildWorkerPolicy.Eligible(Job(shared:false)) && !ChildWorkerPolicy.Eligible(Job(tiles:2048)),"final/local/tiled excluded");
Check(new WorkerResources(30,40,20,16,8).HasHeadroom(4),"headroom accepted");
Check(!new WorkerResources(99,40,20,16,8).HasHeadroom(4) && !new WorkerResources(30,101,20,16,8).HasHeadroom(4) && !new WorkerResources(30,40,3,16,8).HasHeadroom(4) && !new WorkerResources(30,40,20,2,22).HasHeadroom(4),"CPU GPU RAM VRAM limits");
var big = new WorkerResources(85,100,100,8,16,128,24);
Check(big.BlockReason(8,new(),3)==null,"24GB GPU can launch beyond half capacity at full utilization");
Check(Math.Abs(big.VramReserve(new())-1.2)<0.01 && Math.Abs(big.RamReserve(new())-6.4)<0.01,"128GB/24GB hardware-scaled reserves");
Check(new WorkerResources(50,100,10,1.4,6.6,16,8).BlockReason(3,new(),1)!=null,"smaller GPU cannot overcommit next worker");
Check(ChildWorkerPolicy.Eligible(new(){Distributed=true,CoordinationFolder="test",RenderMode="FINAL",Engine="BLENDER_EEVEE",StartFrame="1",EndFrame="100"}) && ChildWorkerPolicy.Eligible(new(){Distributed=true,CoordinationFolder="test",RenderMode="FINAL",Engine="BLENDER_EEVEE_NEXT",StartFrame="1",EndFrame="100"}),"both Eevee engine IDs eligible");
Check(!ChildWorkerPolicy.Eligible(new(){Distributed=true,CoordinationFolder="test",RenderMode="FINAL",Engine="CYCLES",StartFrame="1",EndFrame="100"}),"Cycles remains excluded");
Check(!ChildWorkerPolicy.WorthTrial(2,100,30) && !ChildWorkerPolicy.WorthTrial(30,2,30) && ChildWorkerPolicy.WorthTrial(5,100,40),"warmup and remaining work gate");
Check(ChildWorkerPolicy.Improved(1,72,60) && !ChildWorkerPolicy.Improved(1,60,60),"measured throughput threshold");
var defaults = new WorkerTuning();
var upgraded = (defaults with{MaxChildren=5,MaxCpuPercent=60,MaxGpuPercent=65,MinimumRamGb=8,MinimumVramGb=4}).UpgradeLegacy();
Check(upgraded.MaxChildren==5 && upgraded.MaxCpuPercent==95 && upgraded.MaxGpuPercent==100 && upgraded.MinimumRamGb==2 && upgraded.MinimumVramGb==1,"legacy resource defaults migrate while child cap is preserved");
Check((defaults with{MaxCpuPercent=80,MinimumVramGb=3}).UpgradeLegacy().MaxCpuPercent==80 && (defaults with{MinimumVramGb=3}).UpgradeLegacy().MinimumVramGb==3,"custom resource limits are retained");
Check(defaults.Validate()==null && defaults.WarmupSeconds==10 && defaults.CheckSeconds==3,"short warmup defaults");
Check(ChildWorkerPolicy.WorthTrial(48,252,10,defaults,4),"300-frame fast playblast qualifies early");
Check(!ChildWorkerPolicy.WorthTrial(48,252,10,defaults,40),"slow startup must repay its cost");
Check(!ChildWorkerPolicy.WorthTrial(48,252,10,defaults with {WarmupSeconds=20},4),"custom warmup respected");
Check(!ChildWorkerPolicy.Improved(1,24,20,defaults with {MinimumGainPercent=30}),"custom gain threshold respected");
Check((defaults with {MaxChildren=9}).Validate()!=null && (defaults with {CheckSeconds=double.NaN}).Validate()!=null,"invalid settings rejected");
Check(JsonSerializer.Deserialize<WorkerTuning>(JsonSerializer.Serialize(defaults with{MaxChildren=4,CheckSeconds=2}))==defaults with{MaxChildren=4,CheckSeconds=2},"settings serialize and round trip");
if(args.Contains("--settings-ui"))
{
    Exception? uiError = null;
    var uiThread = new Thread(() =>
    {
        try
        {
            var app = new App(); app.InitializeComponent();
            var dialog = new WorkerSettingsWindow(new());
            var root = (System.Windows.FrameworkElement)dialog.Content;
            root.Measure(new System.Windows.Size(630,680)); root.Arrange(new System.Windows.Rect(0,0,630,680)); root.UpdateLayout();
            var bitmap = new System.Windows.Media.Imaging.RenderTargetBitmap(630,680,96,96,System.Windows.Media.PixelFormats.Pbgra32);
            var drawing = new System.Windows.Media.DrawingVisual();
            using(var context=drawing.RenderOpen()) { context.DrawRectangle(dialog.Background,null,new System.Windows.Rect(0,0,630,680)); context.DrawRectangle(new System.Windows.Media.VisualBrush(root),null,new System.Windows.Rect(0,0,630,680)); }
            bitmap.Render(drawing);
            var encoder = new System.Windows.Media.Imaging.PngBitmapEncoder(); encoder.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(bitmap));
            var screenshot=Path.Combine(Path.GetTempPath(),"BRH-worker-settings-631.png"); using(var stream=File.Create(screenshot)) encoder.Save(stream);
            Console.WriteLine("Settings preview: "+screenshot);
            app.Shutdown();
        }
        catch(Exception ex) {uiError=ex;}
    });
    uiThread.SetApartmentState(ApartmentState.STA); uiThread.Start(); uiThread.Join();
    if(uiError!=null) throw uiError;
    return;
}
if(args.Contains("--policy-only")) return;
var folder=Path.Combine(Path.GetTempPath(),"BRH-Children-QA-"+Guid.NewGuid().ToString("N")); Directory.CreateDirectory(folder);
var blender=Environment.GetEnvironmentVariable("BLENDER_EXE") ?? @"C:\Program Files\Blender Foundation\Blender 5.2\blender.exe";
var script=Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../../../Scripts/render_scene.py"));
var blend=Path.Combine(folder,"fixture.blend");
Process Start(IEnumerable<string> args,string? stop=null) {
 var info=new ProcessStartInfo(blender){UseShellExecute=false,CreateNoWindow=true,WindowStyle=ProcessWindowStyle.Hidden,RedirectStandardOutput=true,RedirectStandardError=true}; foreach(var arg in args) info.ArgumentList.Add(arg);
 if(stop!=null) info.Environment["BRH_CHILD_STOP_FILE"]=stop;
 return Process.Start(info)!;
}
async Task<(int Code,string Text)> Drain(Process p) {var a=p.StandardOutput.ReadToEndAsync();var b=p.StandardError.ReadToEndAsync();using var timeout=new CancellationTokenSource(TimeSpan.FromMinutes(3));try {await p.WaitForExitAsync(timeout.Token);}catch{p.Kill(true);throw;}var code=p.ExitCode;var text=await a+await b;p.Dispose();return(code,text);}
var setup=await Drain(Start(new[]{"--background","--factory-startup","--python-expr","import bpy; bpy.ops.wm.save_as_mainfile(filepath="+JsonSerializer.Serialize(blend)+")"}));Check(setup.Code==0,"fixture created");
string[] Args(string id)=>args.Contains("--viewport")
 ? new[]{"--factory-startup","--disable-autoexec",blend,"--python",Path.Combine(Path.GetDirectoryName(script)!,"viewport_playblast.py"),"--","Camera","1","40","1",Path.Combine(folder,id,"frame_####"),"128","128","100","24","PNG","SOLID","0","{}","1",id,folder}
 : new[]{"--background","--factory-startup","--disable-autoexec",blend,"--python",script,"--","Camera","1","40","1",Path.Combine(folder,id,"frame_####"),args.Contains("--eevee") ? "BLENDER_EEVEE" : "KEEP","128","128","100","24","PNG",args.Contains("--eevee") ? "FINAL" : "PLAYBLAST","1","0","1","0","SOLID","1",id,folder,"0",""};
var p1=Start(Args("shared"));var p2=Start(Args("shared"));var results=await Task.WhenAll(Drain(p1),Drain(p2));
foreach(var result in results) {if(result.Code!=0) Console.WriteLine(result.Text);Check(result.Code==0,"worker successful");}
var owned=results.SelectMany(r=>Regex.Matches(r.Text,@"BRH_LOCAL_FRAME_DONE:(\d+)").Select(m=>int.Parse(m.Groups[1].Value))).ToList();
Check(owned.Count==40 && owned.Distinct().Count()==40,"two actual Blender workers: 40 frames exactly once");
var claims=Path.Combine(folder,"_claims","shared"); Check(Directory.GetFiles(claims,"*.done").Length==40 && Directory.GetFiles(claims,"*.claim").Length==0,"claims released and all outputs committed");
var stop=Path.Combine(folder,"stop-child");File.WriteAllText(stop,"stop");
var stopped=await Drain(Start(Args("retired"),stop));Check(stopped.Code==0 && stopped.Text.Contains("Child retired between frames") && !stopped.Text.Contains("BRH_LOCAL_FRAME_DONE"),"child stop signal prevents claiming frames");
Console.WriteLine("Test outputs: "+folder);
File.Delete(stop);
using(var retiring=Start(Args("retire-active"),stop)) {
 var errors=retiring.StandardError.ReadToEndAsync(); var count=0;
 using var timeout=new CancellationTokenSource(TimeSpan.FromMinutes(3));
 try {
  while(await retiring.StandardOutput.ReadLineAsync(timeout.Token) is {} line) {
   if(line.StartsWith("BRH_LOCAL_FRAME_DONE:")) {count++; if(count==1) File.WriteAllText(stop,"stop after current frame");}
  }
  await retiring.WaitForExitAsync(timeout.Token);
 } catch {retiring.Kill(true);throw;}
 Check(retiring.ExitCode==0 && count>0 && count<40,"active child retires without completing the whole range");
 Check(Directory.GetFiles(Path.Combine(folder,"_claims","retire-active"),"*.claim").Length==0,"active retirement releases its frame claim");
 await errors;
}
