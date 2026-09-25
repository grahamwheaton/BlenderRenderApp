using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;

namespace BlenderRenderHeadless;

public sealed record WorkerResources(double CpuPercent, double GpuPercent, double AvailableRamGb, double FreeVramGb, double UsedVramGb)
{
    public bool HasHeadroom(double workerRamGb) => CpuPercent is >= 0 and < 60 && GpuPercent is >= 0 and < 65
        && AvailableRamGb >= Math.Max(8, workerRamGb * 2) && FreeVramGb >= Math.Max(4, UsedVramGb);
    public bool UnderPressure => AvailableRamGb < 4 || FreeVramGb < 1;
}

public static class ChildWorkerPolicy
{
    public static bool Eligible(RenderJob job) => job.Distributed && job.TileSize == 0 && job.RenderMode == "PLAYBLAST"
        && job.TotalFrameCount >= 24 && job.Format != "FFMPEG" && !string.IsNullOrWhiteSpace(job.CoordinationFolder);
    public static bool WorthTrial(int localFrames, int remaining, double elapsedSeconds) => localFrames >= 3 && remaining >= 12
        && elapsedSeconds >= 30 && remaining * elapsedSeconds / localFrames >= 60;
    public static bool Improved(double baselineRate, int trialFrames, double trialSeconds) => trialSeconds > 0 && trialFrames / trialSeconds >= baselineRate * 1.1;
}

internal sealed class WorkerResourceProbe
{
    [StructLayout(LayoutKind.Sequential)] private sealed class MemoryInfo
    {
        public uint Length = (uint)Marshal.SizeOf<MemoryInfo>(); public uint Load;
        public ulong TotalPhysical, AvailablePhysical, TotalPage, AvailablePage, TotalVirtual, AvailableVirtual, Extended;
    }
    [DllImport("kernel32.dll")] private static extern bool GlobalMemoryStatusEx([In, Out] MemoryInfo info);
    [DllImport("kernel32.dll")] private static extern bool GetSystemTimes(out long idle, out long kernel, out long user);
    private long _idle, _total;
    public async Task<WorkerResources?> Read()
    {
        if (!GetSystemTimes(out var idle, out var kernel, out var user)) return null;
        var total = kernel + user; var delta = total - _total;
        var cpu = _total == 0 || delta <= 0 ? 100 : 100.0 * (1 - (idle - _idle) / (double)delta);
        _idle = idle; _total = total;
        var memory = new MemoryInfo(); if (!GlobalMemoryStatusEx(memory)) return null;
        // Unknown GPU telemetry is not evidence that another GPU worker is safe.
        try
        {
            var info = new ProcessStartInfo("nvidia-smi.exe") { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
            info.ArgumentList.Add("--query-gpu=utilization.gpu,memory.free,memory.used"); info.ArgumentList.Add("--format=csv,noheader,nounits");
            using var p = Process.Start(info)!; var output = p.StandardOutput.ReadToEndAsync(); var errors = p.StandardError.ReadToEndAsync();
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(3));
            try { await p.WaitForExitAsync(timeout.Token); }
            catch (OperationCanceledException) { try { p.Kill(true); } catch { } await p.WaitForExitAsync(); await output; await errors; return null; }
            var text = await output; await errors; if (p.ExitCode != 0) return null;
            var rows = text.Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(line => line.Split(',').Select(v => double.Parse(v.Trim(), CultureInfo.InvariantCulture)).ToArray()).ToArray();
            if (rows.Length == 0 || rows.Any(r => r.Length != 3)) return null;
            return new(cpu, rows.Max(r => r[0]), memory.AvailablePhysical / 1073741824.0, rows.Min(r => r[1]) / 1024, rows.Max(r => r[2]) / 1024);
        }
        catch { return null; }
    }
}

public partial class MainWindow
{
    private readonly List<Process> _childProcesses = new();
    private void StopRenderWorkers()
    {
        foreach (var process in _childProcesses.Append(_renderProcess).OfType<Process>().ToArray())
            try { if (!process.HasExited) process.Kill(true); } catch { }
    }
    private void AllowChildren_Changed(object sender, RoutedEventArgs e)
    {
        if (!_suppressWatchChange && AllowChildrenCheckBox != null) SaveCurrentSettings();
    }

    private async Task<int> RunWithChildrenAsync(string exe, string[] args, RenderJob job)
    {
        var localFrames = new HashSet<int>();
        var childFrames = 0;
        var clock = Stopwatch.StartNew();
        double? firstFrameAt = null;
        var stopPath = Path.Combine(Path.GetTempPath(), "brh-child-stop-" + Guid.NewGuid().ToString("N"));
        Process? child = null; Task? childExit = null;
        bool triedChild = false, retiring = false;
        double baseline = 0, childStartedAt = 0, trialReadyAt = 0;
        int trialStartCount = 0, headroomSamples = 0;
        var probe = new WorkerResourceProbe();

        Process Launch(bool isChild)
        {
            var info = MakeStartInfo(exe, args, job.RequiresViewport);
            if (isChild) info.Environment["BRH_CHILD_STOP_FILE"] = stopPath;
            else info.Environment.Remove("BRH_CHILD_STOP_FILE");
            var process = new Process { StartInfo = info };
            process.OutputDataReceived += (_, e) =>
            {
                if (string.IsNullOrWhiteSpace(e.Data)) return;
                Dispatcher.Invoke(() =>
                {
                    if (e.Data.StartsWith("BRH_LOCAL_FRAME_DONE:") && int.TryParse(e.Data.Split(':')[1], out var ownedFrame))
                    {
                        localFrames.Add(ownedFrame); firstFrameAt ??= clock.Elapsed.TotalSeconds;
                        if (isChild) childFrames++;
                        return;
                    }
                    AppendLog((isChild ? "[Child] " : "") + e.Data);
                    var match = FramePattern.Match(e.Data);
                    if (match.Success && int.TryParse(match.Groups[1].Value, out var frame))
                    {
                        int? completed = int.TryParse(match.Groups[3].Value, out var c) ? c : null;
                        int? total = int.TryParse(match.Groups[4].Value, out var t) ? t : null;
                        job.ReportFrame(frame, match.Groups[2].Value, completed, total);
                    }
                });
            };
            process.ErrorDataReceived += (_, e) => { if (!string.IsNullOrWhiteSpace(e.Data)) Dispatcher.Invoke(() => AppendLog((isChild ? "[Child] " : "") + e.Data)); };
            try { process.Start(); process.BeginOutputReadLine(); process.BeginErrorReadLine(); }
            catch { try { if (!process.HasExited) process.Kill(true); } catch { } process.Dispose(); throw; }
            return process;
        }

        void Retire(string reason)
        {
            if (retiring || child == null || child.HasExited) return;
            try { File.WriteAllText(stopPath, "Stop before claiming another frame"); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { AppendLog("Could not request child retirement: " + ex.Message); return; }
            retiring = true; job.WorkerStatus = "Child stopping after current frame · " + reason;
            AppendLog(job.WorkerStatus);
        }

        using var main = Launch(false); _renderProcess = main;
        var mainExit = main.WaitForExitAsync();
        job.WorkerStatus = "1 Blender · measuring headroom";
        try
        {
            while (await Task.WhenAny(mainExit, Task.Delay(10000)) != mainExit)
            {
                if (_cancelRequested || job.IsGloballyCancelled || job.HasGlobalCancellationMarker()) { StopRenderWorkers(); break; }
                if (job.Progress >= 100 && await Task.Run(job.HasVerifiedSequenceCompletion))
                {
                    // Files and claims have been verified before stopping a
                    // worker whose post-render shutdown is stuck.
                    if (await Task.WhenAny(mainExit, Task.Delay(10000)) != mainExit) StopRenderWorkers();
                    await mainExit;
                    return 0;
                }
                var resources = await Task.Run(probe.Read);
                if (_cancelRequested || job.IsGloballyCancelled || job.HasGlobalCancellationMarker()) { StopRenderWorkers(); break; }
                if (main.HasExited) break;
                main.Refresh();
                var remaining = Math.Max(0, job.TotalFrameCount - (int)Math.Round(job.Progress * job.TotalFrameCount / 100));
                if (!triedChild)
                {
                    var elapsed = firstFrameAt.HasValue ? clock.Elapsed.TotalSeconds - firstFrameAt.Value : 0;
                    var hasRoom = resources?.HasHeadroom(main.PrivateMemorySize64 / 1073741824.0) == true;
                    headroomSamples = hasRoom ? headroomSamples + 1 : 0;
                    job.WorkerStatus = resources == null ? "1 Blender · GPU telemetry unavailable; no child" : hasRoom ? "1 Blender · measuring potential speed-up" : "1 Blender · keeping resource headroom";
                    if (headroomSamples < 3 || !ChildWorkerPolicy.WorthTrial(localFrames.Count - 1, remaining, elapsed)) continue;
                    triedChild = true; baseline = (localFrames.Count - 1) / elapsed;
                    try
                    {
                        child = Launch(true); _childProcesses.Add(child); childExit = child.WaitForExitAsync(); childStartedAt = clock.Elapsed.TotalSeconds;
                        job.WorkerStatus = "2 Blenders · testing throughput"; AppendLog($"Allow Children: launched one extra worker using the same frame claims. Baseline {baseline:0.00} frames/s.");
                    }
                    catch (Exception ex) { job.WorkerStatus = "1 Blender · child could not start"; AppendLog(ex.Message); }
                }
                else if (child != null)
                {
                    if (child.HasExited) { job.WorkerStatus = "1 Blender · child " + (child.ExitCode == 0 ? "finished" : "failed; primary continues"); continue; }
                    if (resources == null || resources.UnderPressure) Retire("resource pressure or unavailable telemetry");
                    if (childFrames == 0 && clock.Elapsed.TotalSeconds - childStartedAt > 120) Retire("startup too slow");
                    if (childFrames > 0 && trialReadyAt == 0) { trialReadyAt = clock.Elapsed.TotalSeconds; trialStartCount = localFrames.Count; }
                    if (!retiring && trialReadyAt > 0 && clock.Elapsed.TotalSeconds - trialReadyAt >= 60)
                    {
                        var improved = ChildWorkerPolicy.Improved(baseline, localFrames.Count - trialStartCount, clock.Elapsed.TotalSeconds - trialReadyAt);
                        if (!improved) Retire("less than 10% throughput gain");
                        else job.WorkerStatus = "2 Blenders · throughput gain confirmed";
                    }
                }
            }
            await mainExit;
            return main.ExitCode;
        }
        finally
        {
            if (child != null)
            {
                Retire("job ended");
                if (childExit != null && await Task.WhenAny(childExit, Task.Delay(10000)) != childExit)
                    try { child.Kill(true); } catch { }
                if (childExit != null) await childExit;
                _childProcesses.Remove(child); child.Dispose();
            }
            if (!main.HasExited) { try { main.Kill(true); } catch { } await mainExit; }
            _renderProcess = null;
            try { if (File.Exists(stopPath)) File.Delete(stopPath); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
            job.WorkerStatus = "Local Blender workers stopped";
        }
    }
}
