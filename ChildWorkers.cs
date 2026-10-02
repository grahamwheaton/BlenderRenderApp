using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;

namespace BlenderRenderHeadless;

public sealed record WorkerResources(double CpuPercent, double GpuPercent, double AvailableRamGb, double FreeVramGb, double UsedVramGb, double TotalRamGb = 0, double TotalVramGb = 0)
{
    public bool HasHeadroom(double workerRamGb, WorkerTuning? settings = null) => BlockReason(workerRamGb, settings ?? new()) == null;
    public string? BlockReason(double workerRamGb, WorkerTuning settings, double workerVramGb = 1)
    {
        if (CpuPercent < 0 || CpuPercent > settings.MaxCpuPercent) return $"CPU {CpuPercent:0}% (limit {settings.MaxCpuPercent:0}%)";
        if (GpuPercent < 0 || GpuPercent > settings.MaxGpuPercent) return $"GPU {GpuPercent:0}% (limit {settings.MaxGpuPercent:0}%)";
        var ram = RamReserve(settings) + Math.Max(0, workerRamGb) * 1.15;
        if (AvailableRamGb < ram) return $"free RAM {AvailableRamGb:0.0}/{ram:0.0} GB required";
        var vram = VramReserve(settings) + Math.Max(0.5, workerVramGb) * 1.15;
        if (FreeVramGb < vram) return $"free VRAM {FreeVramGb:0.0}/{vram:0.0} GB required";
        return null;
    }
    public double RamReserve(WorkerTuning settings) => Math.Max(settings.MinimumRamGb, TotalRamGb * settings.MemoryReservePercent / 100);
    public double VramReserve(WorkerTuning settings) => Math.Max(settings.MinimumVramGb, (TotalVramGb > 0 ? TotalVramGb : FreeVramGb + UsedVramGb) * settings.MemoryReservePercent / 100);
    public bool UnderPressure(WorkerTuning settings) => AvailableRamGb < RamReserve(settings) || FreeVramGb < VramReserve(settings);
}

public static class ChildWorkerPolicy
{
    public static bool Eligible(RenderJob job) => job.Distributed && job.TileSize == 0 && (job.RenderMode == "PLAYBLAST" || job.Engine is "BLENDER_EEVEE" or "BLENDER_EEVEE_NEXT")
        && job.TotalFrameCount >= 24 && job.Format != "FFMPEG" && !string.IsNullOrWhiteSpace(job.CoordinationFolder);
    public static double RequiredRemaining(WorkerTuning settings, double startupSeconds) => Math.Max(settings.MinimumRemainingSeconds, startupSeconds * settings.StartupMultiplier);
    public static bool WorthTrial(int localFrames, int remaining, double elapsedSeconds, WorkerTuning? settings = null, double startupSeconds = 5)
    {
        settings ??= new();
        return localFrames >= 3 && remaining >= 12 && elapsedSeconds >= settings.WarmupSeconds && remaining * elapsedSeconds / localFrames >= RequiredRemaining(settings, startupSeconds);
    }
    public static bool Improved(double baselineRate, int trialFrames, double trialSeconds, WorkerTuning? settings = null) => trialSeconds > 0 && trialFrames / trialSeconds >= baselineRate * (1 + (settings ?? new()).MinimumGainPercent / 100);
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
            info.ArgumentList.Add("--query-gpu=utilization.gpu,memory.free,memory.used,memory.total"); info.ArgumentList.Add("--format=csv,noheader,nounits");
            using var p = Process.Start(info)!; var output = p.StandardOutput.ReadToEndAsync(); var errors = p.StandardError.ReadToEndAsync();
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(3));
            try { await p.WaitForExitAsync(timeout.Token); }
            catch (OperationCanceledException) { try { p.Kill(true); } catch { } await p.WaitForExitAsync(); await output; await errors; return null; }
            var text = await output; await errors; if (p.ExitCode != 0) return null;
            var rows = text.Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(line => line.Split(',').Select(v => double.Parse(v.Trim(), CultureInfo.InvariantCulture)).ToArray()).ToArray();
            if (rows.Length == 0 || rows.Any(r => r.Length != 4)) return null;
            // Use a coherent worst-free-memory GPU sample, not free from one
            // card plus used from another. Still gate utilization across cards.
            var gpu = rows.OrderBy(r => r[1]).First();
            return new(cpu, rows.Max(r => r[0]), memory.AvailablePhysical / 1073741824.0, gpu[1] / 1024, gpu[2] / 1024, memory.TotalPhysical / 1073741824.0, gpu[3] / 1024);
        }
        catch { return null; }
    }
}

public partial class MainWindow
{
    private WorkerTuning _workerTuning = new();
    private readonly List<Process> _childProcesses = new();
    private sealed class ChildState
    {
        public Process Process = null!;
        public Task Exit = Task.CompletedTask;
        public string StopPath = "";
        public int Number, StartCount;
        public double StartedAt, FirstFrameAt, Baseline;
        public bool Retiring;
    }
    private void StopRenderWorkers()
    {
        StopPreload();
        foreach (var process in _childProcesses.Append(_renderProcess).OfType<Process>().ToArray())
            try { if (!process.HasExited) process.Kill(true); } catch { }
    }
    private void AllowChildren_Changed(object sender, RoutedEventArgs e)
    {
        if (!_suppressWatchChange && AllowChildrenCheckBox != null) SaveCurrentSettings();
    }
    private void SettingsButton_Click(object sender, RoutedEventArgs e)
    {
        e.Handled = true;
        var dialog = new WorkerSettingsWindow(_workerTuning) { Owner = this };
        if (dialog.ShowDialog() != true) return;
        var previous = _workerTuning;
        _workerTuning = dialog.Settings;
        try { SaveCurrentSettings(); }
        catch (Exception ex) { _workerTuning = previous; MessageBox.Show(this, ex.Message, "Could not save settings"); }
    }

    private async Task<int> RunWithChildrenAsync(string exe, string[] args, RenderJob job, Func<Task<WorkerResources?>>? resourceReader = null)
    {
        // Immutable snapshot: changes in Settings apply only to the next job.
        var tuning = _workerTuning;
        var localFrames = new HashSet<int>();
        var children = new List<ChildState>();
        var clock = Stopwatch.StartNew();
        double? firstFrameAt = null;
        double windowAt = 0, startupSeconds = 0;
        int windowCount = 0, healthy = 0;
        bool stopAdding = false;
        string stopReason = "";
        ChildState? trial = null;
        var probe = new WorkerResourceProbe();
        resourceReader ??= probe.Read;

        void Status(string reason)
        {
            var count = 1 + children.Count(c => !c.Process.HasExited);
            job.WorkerStatus = $"{count} Blender{(count == 1 ? "" : "s")} · {reason}";
        }
        Process Launch(ChildState? child)
        {
            var info = MakeStartInfo(exe, args, job.RequiresViewport);
            if (child != null) info.Environment["BRH_CHILD_STOP_FILE"] = child.StopPath;
            else info.Environment.Remove("BRH_CHILD_STOP_FILE");
            var process = child == null ? PrepareWorker(exe, args, job.RequiresViewport) : new Process { StartInfo = info };
            var prefix = child == null ? "" : $"[Child {child.Number}] ";
            process.OutputDataReceived += (_, e) =>
            {
                if (string.IsNullOrWhiteSpace(e.Data)) return;
                Dispatcher.Invoke(() =>
                {
                    if (e.Data.StartsWith("BRH_LOCAL_FRAME_DONE:") && int.TryParse(e.Data.Split(':')[1], out var frame))
                    {
                        localFrames.Add(frame);
                        if (!firstFrameAt.HasValue)
                        {
                            firstFrameAt = clock.Elapsed.TotalSeconds;
                            startupSeconds = firstFrameAt.Value;
                            windowAt = firstFrameAt.Value;
                            windowCount = localFrames.Count;
                        }
                        if (child != null && child.FirstFrameAt == 0)
                        {
                            child.FirstFrameAt = clock.Elapsed.TotalSeconds;
                            child.StartCount = localFrames.Count;
                            startupSeconds = Math.Max(startupSeconds, child.FirstFrameAt - child.StartedAt);
                        }
                        return;
                    }
                    WorkerReady(job, e.Data);
                    AppendLog(prefix + e.Data);
                    var match = FramePattern.Match(e.Data);
                    if (match.Success && int.TryParse(match.Groups[1].Value, out var reportedFrame))
                    {
                        int? completed = int.TryParse(match.Groups[3].Value, out var c) ? c : null;
                        int? total = int.TryParse(match.Groups[4].Value, out var t) ? t : null;
                        job.ReportFrame(reportedFrame, match.Groups[2].Value, completed, total);
                    }
                });
            };
            process.ErrorDataReceived += (_, e) => { if (!string.IsNullOrWhiteSpace(e.Data)) Dispatcher.Invoke(() => AppendLog(prefix + e.Data)); };
            try { StartPreparedWorker(process); }
            catch { try { if (!process.HasExited) process.Kill(true); } catch { } process.Dispose(); throw; }
            return process;
        }
        void Retire(ChildState child, string reason)
        {
            if (child.Retiring || child.Process.HasExited) return;
            try { File.WriteAllText(child.StopPath, "Stop before claiming another frame"); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { AppendLog("Could not retire child: " + ex.Message); return; }
            child.Retiring = true;
            AppendLog($"Child {child.Number} stopping after current frame: {reason}");
        }

        var beforeWorker = await Task.Run(resourceReader);
        if (beforeWorker != null)
            AppendLog($"Turbo resources: {beforeWorker.TotalRamGb:0.0} GB RAM, {beforeWorker.TotalVramGb:0.0} GB VRAM; reserves {beforeWorker.RamReserve(tuning):0.0} GB RAM / {beforeWorker.VramReserve(tuning):0.0} GB VRAM. Limits CPU {tuning.MaxCpuPercent:0}% / GPU {tuning.MaxGpuPercent:0}%.");
        double workerVramEstimate = 0.5;
        if (_cancelRequested || job.IsGloballyCancelled) return 1;
        clock.Restart();
        using var main = Launch(null); _renderProcess = main;
        var mainExit = main.WaitForExitAsync();
        Status("warming up");
        try
        {
            // Prime CPU deltas immediately rather than losing one check interval.
            await Task.Run(resourceReader);
            while (await Task.WhenAny(mainExit, Task.Delay(TimeSpan.FromSeconds(tuning.CheckSeconds))) != mainExit)
            {
                if (_cancelRequested || job.IsGloballyCancelled || job.HasGlobalCancellationMarker()) { StopRenderWorkers(); break; }
                if (job.Progress >= 100 && await Task.Run(job.HasVerifiedSequenceCompletion))
                {
                    if (await Task.WhenAny(mainExit, Task.Delay(10000)) != mainExit) StopRenderWorkers();
                    await mainExit;
                    return 0;
                }
                var resources = await Task.Run(resourceReader);
                if (_cancelRequested || job.IsGloballyCancelled || job.HasGlobalCancellationMarker()) { StopRenderWorkers(); break; }
                if (main.HasExited) break;
                main.Refresh();
                var now = clock.Elapsed.TotalSeconds;
                var live = children.Where(c => !c.Process.HasExited).ToList();
                if (children.Any(c => c.Process.HasExited && c.Process.ExitCode != 0))
                { stopAdding = true; stopReason = "child failed; primary continues"; }
                if (resources != null && beforeWorker != null)
                    workerVramEstimate = Math.Max(workerVramEstimate, Math.Max(0, resources.UsedVramGb - beforeWorker.UsedVramGb) / (1 + live.Count));
                if (live.Count > 0 && (resources == null || resources.UnderPressure(tuning)))
                {
                    var newest = live.LastOrDefault(c => !c.Retiring);
                    if (newest != null) Retire(newest, "low RAM/VRAM or unavailable telemetry");
                    stopAdding = true; stopReason = "resource pressure";
                }
                if (trial != null)
                {
                    if (trial.Process.HasExited || trial.Retiring)
                    { trial = null; stopAdding = true; if (stopReason == "") stopReason = "trial worker stopped"; }
                    else if (trial.FirstFrameAt == 0)
                    {
                        if (now - trial.StartedAt > Math.Clamp(startupSeconds * 4, 30, 180))
                        { Retire(trial, "startup too slow"); stopAdding = true; stopReason = "child startup too slow"; }
                        Status($"child {trial.Number} starting ({now - trial.StartedAt:0}s)");
                        continue;
                    }
                    else if (now - trial.FirstFrameAt < tuning.TrialSeconds)
                    { Status($"testing child {trial.Number}: {now - trial.FirstFrameAt:0}/{tuning.TrialSeconds:0}s"); continue; }
                    else
                    {
                        var seconds = now - trial.FirstFrameAt;
                        var frames = localFrames.Count - trial.StartCount;
                        var improved = ChildWorkerPolicy.Improved(trial.Baseline, frames, seconds, tuning);
                        AppendLog($"Child {trial.Number} trial: {frames / seconds:0.00} frames/s vs {trial.Baseline:0.00} baseline.");
                        if (!improved)
                        { Retire(trial, $"less than {tuning.MinimumGainPercent:0}% gain"); stopAdding = true; stopReason = "no useful throughput gain"; }
                        trial = null; healthy = 0; windowAt = now; windowCount = localFrames.Count;
                        Status(improved ? "throughput gain confirmed; measuring next step" : stopReason);
                        continue;
                    }
                }
                if (live.Any(c => c.Retiring)) { Status("child stopping after current frame"); continue; }
                if (stopAdding) { Status(stopReason); continue; }
                if (live.Count >= tuning.MaxChildren) { Status($"child limit reached ({live.Count}/{tuning.MaxChildren})"); continue; }
                var block = resources?.BlockReason(main.PrivateMemorySize64 / 1073741824.0, tuning, workerVramEstimate);
                if (beforeWorker == null) block = "startup GPU memory baseline unavailable; no child";
                if (resources == null) block = "GPU telemetry unavailable; no child";
                healthy = block == null ? healthy + 1 : 0;
                if (block != null) { Status(block); continue; }
                if (!firstFrameAt.HasValue) { Status("waiting for first completed frame"); continue; }
                var elapsed = now - windowAt;
                var gained = localFrames.Count - windowCount;
                if (elapsed < tuning.WarmupSeconds || gained < 3)
                { Status($"warm-up {elapsed:0}/{tuning.WarmupSeconds:0}s · {gained}/3 timing frames"); continue; }
                if (healthy < tuning.HealthyChecks) { Status($"healthy resource checks {healthy}/{tuning.HealthyChecks}"); continue; }
                var remaining = Math.Max(0, job.TotalFrameCount - (int)Math.Round(job.Progress * job.TotalFrameCount / 100));
                var estimated = remaining * elapsed / gained;
                var needed = ChildWorkerPolicy.RequiredRemaining(tuning, startupSeconds);
                if (!ChildWorkerPolicy.WorthTrial(gained, remaining, elapsed, tuning, startupSeconds))
                { Status($"not enough work left: {remaining} frames, ~{estimated:0}s (need 12 frames and {needed:0}s)"); continue; }
                var next = new ChildState
                {
                    Number = children.Count + 1, Baseline = gained / elapsed, StartedAt = now,
                    StopPath = Path.Combine(Path.GetTempPath(), "brh-child-stop-" + Guid.NewGuid().ToString("N"))
                };
                try
                {
                    next.Process = Launch(next); next.Exit = next.Process.WaitForExitAsync();
                    children.Add(next); _childProcesses.Add(next.Process); trial = next; healthy = 0;
                    Status($"child {next.Number} starting");
                    AppendLog($"Allow Children: launched child {next.Number}/{tuning.MaxChildren}; baseline {next.Baseline:0.00} frames/s; startup estimate {startupSeconds:0.0}s; remaining ~{estimated:0}s.");
                    AppendLog($"Turbo memory: estimated child VRAM {workerVramEstimate:0.0} GB (+15% margin); {resources!.FreeVramGb:0.0} GB free of {resources.TotalVramGb:0.0} GB.");
                }
                catch (Exception ex) { stopAdding = true; stopReason = "child could not start"; Status(stopReason); AppendLog(ex.Message); }
            }
            await mainExit;
            return main.ExitCode;
        }
        finally
        {
            foreach (var child in children) Retire(child, "job ended");
            var allChildren = Task.WhenAll(children.Select(c => c.Exit));
            if (await Task.WhenAny(allChildren, Task.Delay(10000)) != allChildren)
                foreach (var child in children) try { if (!child.Process.HasExited) child.Process.Kill(true); } catch { }
            await allChildren;
            foreach (var child in children)
            {
                AppendLog($"Child {child.Number} exit code: {child.Process.ExitCode} (0x{unchecked((uint)child.Process.ExitCode):X8})");
                _childProcesses.Remove(child.Process); child.Process.Dispose();
                try { if (File.Exists(child.StopPath)) File.Delete(child.StopPath); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
            }
            if (!main.HasExited) { try { main.Kill(true); } catch { } await mainExit; }
            _renderProcess = null; job.WorkerStatus = "Local Blender workers stopped";
        }
    }
}
