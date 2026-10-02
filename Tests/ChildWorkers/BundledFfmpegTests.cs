using BlenderRenderHeadless;
using System.Diagnostics;
using System.IO;

static class BundledFfmpegTests
{
    public static async Task Run()
    {
        var oldPath = Environment.GetEnvironmentVariable("PATH");
        Environment.SetEnvironmentVariable("PATH", "");
        try
        {
            var exe = Mp4Encoder.FindFfmpeg() ?? throw new Exception("Bundled encoder unavailable");
            if (!exe.Contains("BlenderRenderLauncher")) throw new Exception("Test used an external encoder");
            var root = Path.Combine(Path.GetTempPath(), "BRH-bundled-ffmpeg-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);
            var png = Path.Combine(root, "test.png");
            var info = new ProcessStartInfo(exe) { UseShellExecute=false, CreateNoWindow=true, RedirectStandardError=true };
            foreach(var arg in new[]{"-hide_banner","-loglevel","error","-f","lavfi","-i","color=c=orange:s=64x64","-frames:v","1",png}) info.ArgumentList.Add(arg);
            using var process = Process.Start(info)!;
            var errors = process.StandardError.ReadToEndAsync();
            await process.WaitForExitAsync();
            if(process.ExitCode!=0) throw new Exception(await errors);
            var output = Path.Combine(root,"test.mp4");
            await Mp4Encoder.Encode(exe,Enumerable.Repeat(png,24).ToList(),output,24,18,new Progress<int>(),CancellationToken.None);
            if(new FileInfo(output).Length<100) throw new Exception("No MP4 produced");
            if(BundledFfmpeg.Resolve()!=exe || !File.Exists(Path.Combine(Path.GetDirectoryName(exe)!,"LICENSE"))) throw new Exception("Cache/licence missing");
            Console.WriteLine("PASS: empty PATH, bundled extraction/cache/licence, actual H.264 MP4 encoding: " + output);
        }
        finally { Environment.SetEnvironmentVariable("PATH",oldPath); }
    }
}
