using BlenderRenderHeadless;
using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;

static class RepairTests
{
    public static void Run()
    {
        var root = Path.Combine(Path.GetTempPath(), "BRH-repair-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var outputs = Path.Combine(root, "outputs"); Directory.CreateDirectory(outputs);
        var id = Guid.NewGuid().ToString();
        var claims = Path.Combine(root, "_claims", id); Directory.CreateDirectory(claims);
        var blend = Path.Combine(root, "test.blend"); File.WriteAllText(blend, "fixture");
        var job = new RenderJob { JobId=id, Distributed=true, CoordinationFolder=root, StartFrame="1", EndFrame="5", FrameStep="2", OutputPath=outputs + Path.DirectorySeparatorChar, BlendFile=blend, Format="PNG", Status="Complete" };
        foreach(var frame in new[]{1,3,5})
        {
            var output=Path.Combine(outputs,$"frame,{frame}.png"); File.WriteAllText(output,"pixels"+frame);
            File.WriteAllText(Path.Combine(claims,frame+".done"), JsonSerializer.Serialize(new {output,machine=frame==3?"BAD-PC":"GOOD-PC",author="artist"}));
        }
        var manifest=new { jobId=id, blendFile=blend, startFrame=1, endFrame=5, frameStep=2, outputPath=job.OutputPath, distributed=true, overwrite=true, showOverlays=false, viewportOverlaySettings=new {show_floor=false}, senderMachine="original" };
        File.WriteAllText(Path.Combine(root,"original.renderjob.json"),JsonSerializer.Serialize(manifest));
        var records=SequenceRepair.Read(job);
        void Assert(bool value,string message) { if(!value) throw new Exception(message); }
        Assert(records.Count==3,"frame step");
        using(var lease=SequenceRepair.Lock(job))
        {
            Assert(SequenceRepair.Quarantine(job,records.Where(r=>r.Machine=="BAD-PC"))==1,"selected machine only");
            Assert(File.ReadAllText(records[0].Output)=="pixels1" && File.ReadAllText(records[2].Output)=="pixels5","good frames preserved");
            Assert(Directory.GetFiles(Path.Combine(outputs,"_partial-delete"),"*.png",SearchOption.AllDirectories).Length==1,"recoverable backup");
            var published=SequenceRepair.Publish(job,records);
            var payload=JsonNode.Parse(File.ReadAllText(published))!;
            Assert(payload["overwrite"]!.GetValue<bool>()==false && payload["frameStep"]!.GetValue<int>()==2,"repair skips good frames and retains step");
            Assert(payload["senderMachine"]!.GetValue<string>()=="original" && !payload["viewportOverlaySettings"]!["show_floor"]!.GetValue<bool>(),"sender and viewport preserved");
            var newClaims=Path.Combine(root,"_claims",payload["jobId"]!.GetValue<string>());
            Assert(Directory.GetFiles(newClaims,"*.done").Length==2,"only good receipts seeded");
        }
        bool refused=false; try{using var duplicate=SequenceRepair.Lock(job);}catch(IOException){refused=true;} Assert(refused,"duplicate repair refused");
        // CSV fallback handles quoted commas; then reject an out-of-folder receipt.
        File.Delete(Path.Combine(claims,"3.done"));
        File.WriteAllText(Path.Combine(outputs,$"render-report-camera-{id[..8]}.csv"),$"Frame,Rendered by,Machine,Completed,Output file\n3,artist,BAD-PC,,\"{records[1].Output}\"\n");
        Assert(SequenceRepair.Read(job).Count==3,"CSV fallback");
        File.Move(Path.Combine(outputs,$"render-report-camera-{id[..8]}.csv"), Path.Combine(root,$"render-report-camera-{id[..8]}.csv"));
        Assert(SequenceRepair.Read(job).Count==3,"watch-folder CSV fallback");
        var outside=Path.Combine(root,"outside.png"); File.WriteAllText(outside,"untouched");
        File.WriteAllText(Path.Combine(claims,"3.done"),JsonSerializer.Serialize(new {output=outside,machine="BAD-PC"}));
        refused=false; try{SequenceRepair.Read(job);}catch(IOException){refused=true;} Assert(refused && File.ReadAllText(outside)=="untouched","outside path refused");
        Console.WriteLine("PASS repair: selection, backup, preserved files/settings/attribution, CSV quoting, duplicate and path safety. Fixtures: " + root);
    }
}
