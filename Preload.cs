using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Windows;

namespace BlenderRenderHeadless;

public partial class MainWindow
{
    private sealed class PreloadedScene
    {
        public required RenderJob Job;
        public required Process Process;
        public required string Control;
        public DateTime Modified;
        public volatile bool Ready;
    }
    private PreloadedScene? _preloaded;
    private readonly Dictionary<Process, (string Control, string Script, string[] Args)> _preloadReleases = new();
    private readonly WorkerResourceProbe _preloadProbe = new();
    private bool _preloadChecking;
    private DateTime _nextPreloadCheck;
    private bool _renderOnlyOne;
    private readonly HashSet<RenderJob> _attemptedThisRun = new();

    private void AllowPreload_Changed(object sender, RoutedEventArgs e)
    {
        if (AllowPreloadCheckBox == null) return;
        if (AllowPreloadCheckBox.IsChecked != true) StopPreload();
        if (!_suppressWatchChange) SaveCurrentSettings();
    }

    private void StopPreload()
    {
        var pending = _preloaded; _preloaded = null;
        if (pending == null) return;
        pending.Job.PreloadLabel = "";
        try { if (!pending.Process.HasExited) pending.Process.Kill(true); } catch { }
        pending.Process.Dispose();
        try { File.Delete(pending.Control); } catch { }
    }

    private async void PollPreload()
    {
        if (_preloadChecking || DateTime.UtcNow < _nextPreloadCheck) return;
        _preloadChecking = true; _nextPreloadCheck = DateTime.UtcNow.AddSeconds(10);
        try
        {
            var next = _queue.FirstOrDefault(j => j.Status == "Waiting" && !_attemptedThisRun.Contains(j) && !j.IsIgnored && !j.IsOpening && !j.IsBatchReserved && !ReferenceEquals(j, _activeRenderJob));
            if (!_queueRunning || _renderOnlyOne || _cancelRequested || AllowPreloadCheckBox.IsChecked != true || next == null || next.TileSize > 0)
            { StopPreload(); return; }
            if (_preloaded != null && (!ReferenceEquals(next, _preloaded.Job) || _preloaded.Process.HasExited)) StopPreload();
            var resources = await _preloadProbe.Read();
            if (resources == null || resources.UnderPressure(_workerTuning)) { StopPreload(); return; }
            if (_preloaded != null) return;
            // Budget at least another current worker's RAM, or 4x the next file size.
            var estimate = Math.Max(2, new FileInfo(next.BlendFile).Length * 4.0 / 1073741824);
            if (_renderProcess is { HasExited: false }) estimate = Math.Max(estimate, _renderProcess.PrivateMemorySize64 / 1073741824.0);
            if (!resources.HasHeadroom(estimate, _workerTuning) || !_queueRunning || _cancelRequested || AllowPreloadCheckBox.IsChecked != true || next.Status != "Waiting" || next.IsIgnored || next.IsOpening || next.IsBatchReserved || _blenderExe == null || !_queue.Contains(next)) return;
            var control = Path.Combine(Path.GetTempPath(), "BRH-preload-" + Guid.NewGuid().ToString("N") + ".json");
            var args = new List<string>();
            if (!next.RequiresViewport) args.Add("--background");
            args.AddRange(new[] { next.BlendFile, "--python", ExtractScript("preload.py"), "--", control });
            var process = new Process { StartInfo = MakeStartInfo(_blenderExe, args, true) };
            var pending = new PreloadedScene { Job = next, Process = process, Control = control, Modified = File.GetLastWriteTimeUtc(next.BlendFile) };
            process.OutputDataReceived += (_, e) => { if (e.Data == "BRH_PRELOAD_READY") pending.Ready = true; };
            process.ErrorDataReceived += (_, _) => { };
            _preloaded = pending;
            process.Start(); process.BeginOutputReadLine(); process.BeginErrorReadLine();
            next.PreloadLabel = "Preloading…";
            AppendLog($"Preloading {next.BlendFileName} — no job claimed.");
        }
        catch (Exception ex) { StopPreload(); AppendLog("Preload skipped: " + ex.Message); }
        finally
        {
            if (_preloaded?.Ready == true) _preloaded.Job.PreloadLabel = "Preloaded";
            _preloadChecking = false;
        }
    }

    private Process PrepareWorker(string exe, IEnumerable<string> arguments, bool hiddenWindow = false)
    {
        var args = arguments.ToArray();
        var pending = _preloaded;
        var scriptIndex = Array.IndexOf(args, "--python");
        if (pending != null && pending.Ready && !pending.Process.HasExited && args.Contains(pending.Job.BlendFile) &&
            args.Contains("--background") == !pending.Job.RequiresViewport && scriptIndex >= 0 &&
            File.GetLastWriteTimeUtc(pending.Job.BlendFile) == pending.Modified)
        {
            _preloaded = null; pending.Job.PreloadLabel = "";
            _preloadReleases[pending.Process] = (pending.Control, args[scriptIndex + 1], args.Skip(Array.IndexOf(args, "--") + 1).ToArray());
            return pending.Process;
        }
        StopPreload();
        return new Process { StartInfo = MakeStartInfo(exe, args, hiddenWindow) };
    }

    private void StartPreparedWorker(Process process)
    {
        if (_preloadReleases.Remove(process, out var release))
        {
            var temp = release.Control + ".tmp";
            File.WriteAllText(temp, JsonSerializer.Serialize(new { script = release.Script, args = release.Args }));
            File.Move(temp, release.Control);
            AppendLog("Using preloaded scene; attempting normal job claims now.");
        }
        else { process.Start(); process.BeginOutputReadLine(); process.BeginErrorReadLine(); }
    }
}
