using BlenderRenderHeadless;
using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Text.RegularExpressions;

void Check(bool yes,string label) { if(!yes) throw new Exception(label); Console.WriteLine("PASS "+label); }
RenderJob Job(string mode="PLAYBLAST", bool shared=true,int tiles=0) => new(){RenderMode=mode,Distributed=shared,TileSize=tiles,StartFrame="1",EndFrame="40",CoordinationFolder="test",Format="PNG"};
Check(ChildWorkerPolicy.Eligible(Job()),"shared playblast eligible");
Check(!ChildWorkerPolicy.Eligible(Job("FINAL")) && !ChildWorkerPolicy.Eligible(Job(shared:false)) && !ChildWorkerPolicy.Eligible(Job(tiles:2048)),"final/local/tiled excluded");
Check(new WorkerResources(30,40,20,16,8).HasHeadroom(4),"headroom accepted");
Check(!new WorkerResources(70,40,20,16,8).HasHeadroom(4) && !new WorkerResources(30,80,20,16,8).HasHeadroom(4) && !new WorkerResources(30,40,3,16,8).HasHeadroom(4) && !new WorkerResources(30,40,20,2,22).HasHeadroom(4),"CPU GPU RAM VRAM limits");
Check(!ChildWorkerPolicy.WorthTrial(2,100,30) && !ChildWorkerPolicy.WorthTrial(30,2,30) && ChildWorkerPolicy.WorthTrial(5,100,40),"warmup and remaining work gate");
Check(ChildWorkerPolicy.Improved(1,72,60) && !ChildWorkerPolicy.Improved(1,60,60),"measured throughput threshold");
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
 : new[]{"--background","--factory-startup","--disable-autoexec",blend,"--python",script,"--","Camera","1","40","1",Path.Combine(folder,id,"frame_####"),"KEEP","128","128","100","24","PNG","PLAYBLAST","1","0","1","0","SOLID","1",id,folder,"0",""};
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
