using BlenderRenderHeadless;

public static class CompletionFeatureTests
{
    public static void Run()
    {
        void Check(bool ok) { if (!ok) throw new Exception("Completion feature assertion failed"); }
        var state = new CompletionShutdownCountdown();
        var now = DateTime.UtcNow;
        Check(QueueRetention.Expired(now.AddHours(-49), now));
        Check(!QueueRetention.Expired(now.AddHours(-48), now));
        Check(!QueueRetention.Expired(now.AddHours(-1), now));
        Check(!state.Tick(now, false, true) && state.Deadline == null);
        Check(!state.Tick(now, true, true));
        Check(!state.Tick(now.AddMinutes(29), true, true));
        Check(state.Tick(now.AddMinutes(30), true, true));
        Check(!state.Tick(now.AddMinutes(31), true, false) && state.Deadline == null);
        Check(!state.Tick(now.AddMinutes(32), true, true));
        state.Reset(); Check(state.Deadline == null);
        Check(!state.Tick(now, false, true));
        var job = new RenderJob { CameraName = "Camera", BlendFile = @"C:\Project\Model\scene.blend", OutputPath = "//../Visuals/frame_" };
        Check(Mp4Encoder.DefaultDestination(job) == @"C:\Project\Visuals\Camera.mp4");
        job = new RenderJob { CameraName = "Camera", OutputPath = @"\\server\share\output\" };
        Check(Mp4Encoder.DefaultDestination(job) == @"\\server\share\output\Camera.mp4");
        Console.WriteLine("PASS shutdown countdown/reset/disabled and job-specific MP4 paths; no OS shutdown invoked");
    }
}
