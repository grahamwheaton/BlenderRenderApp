using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Windows;

namespace BlenderRenderHeadless;

public partial class MainWindow
{
    private StreamWriter? _attemptLog;
    private void BeginAttemptLog(RenderJob job)
    {
        job.FailureDetail = "";
        try
        {
            var folder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "BlenderRenderLauncher", "logs");
            Directory.CreateDirectory(folder);
            job.LogPath = Path.Combine(folder, DateTime.UtcNow.ToString("yyyyMMdd-HHmmss") + "-" + Guid.NewGuid().ToString("N") + ".log");
            _attemptLog = new StreamWriter(job.LogPath) { AutoFlush = true };
            WriteAttemptLog($"Launcher {typeof(MainWindow).Assembly.GetName().Version} | Machine: {Environment.MachineName} | Blender: {_blenderExe}");
            WriteAttemptLog(JsonSerializer.Serialize(new { job.JobId, job.BlendFile, job.CameraName, job.StartFrame, job.EndFrame, job.FrameStep, job.OutputPath, job.Engine, job.RenderMode, job.Width, job.Height, job.Scale, job.Format, job.FrameRate, job.Overwrite, job.Distributed, job.CoordinationFolder, job.RequiresViewport, job.ViewportShading, job.ShowOverlays, job.TileSize }));
        }
        catch (Exception ex) { job.FailureDetail = "Could not save diagnostic log: " + ex.Message; }
    }
    private void WriteAttemptLog(string message)
    {
        try { _attemptLog?.WriteLine($"{DateTimeOffset.Now:O} {message}"); }
        catch (Exception ex) { if (_activeRenderJob != null) _activeRenderJob.FailureDetail = "Log write failed: " + ex.Message; }
    }
    private void EndAttemptLog(RenderJob job)
    {
        WriteAttemptLog("Final status: " + job.Status);
        try { _attemptLog?.Dispose(); } catch { }
        _attemptLog = null;
    }
    private void ViewJobLog_Click(object sender, RoutedEventArgs e)
    {
        e.Handled = true;
        if (sender is not FrameworkElement { Tag: RenderJob job }) return;
        try
        {
            if (!File.Exists(job.LogPath)) throw new IOException("No saved log is available. Logs are recorded for new attempts in this version.");
            var info = new ProcessStartInfo("notepad.exe") { UseShellExecute = false };
            info.ArgumentList.Add(job.LogPath); Process.Start(info);
        }
        catch (Exception ex) { MessageBox.Show(this, ex.Message, "Render log"); }
    }
    private void RetryJob_Click(object sender, RoutedEventArgs e)
    {
        e.Handled = true;
        if (_queueRunning || sender is not FrameworkElement { Tag: RenderJob job } || job.Status != "Failed") return;
        if (job.HasGlobalCancellationMarker()) { MessageBox.Show(this, "This job was cancelled everywhere. Retry will not undo that cancellation.", "Retry"); return; }
        if (!File.Exists(job.BlendFile)) { MessageBox.Show(this, "The Blender file is not accessible:\n" + job.BlendFile, "Retry"); return; }
        if (!job.Distributed && job.Overwrite && MessageBox.Show(this, "This local job has Overwrite enabled. Retrying may replace existing frames. Continue?", "Retry", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) return;
        job.Status = "Waiting"; job.FailureDetail = ""; job.WorkerStatus = "Retry queued on this PC";
        UpdateQueueState(); ResetAutoStartCountdown();
        StatusText.Text = "Retry queued — choose Render jobs, or wait for Auto start";
    }
    private void ResetWatch_Click(object sender, RoutedEventArgs e)
    {
        if (_watchCleanupRunning) return;
        try
        {
        if (string.IsNullOrWhiteSpace(_cloudQueueFolder) || !Directory.Exists(_cloudQueueFolder))
        { MessageBox.Show(this, "The shared watch folder is unavailable. Check your NAS connection first.", "Reset watch"); return; }
        var count = _queue.Count; ResetWatchDiscovery(); SaveCurrentSettings(); UpdateQueueState();
        StatusText.Text = $"Watch reset — {_queue.Count - count} active jobs restored";
        AppendLog(StatusText.Text);
        }
        catch (Exception ex) { AppendLog("Reset watch failed: " + ex); MessageBox.Show(this, ex.Message, "Reset watch"); }
    }
    private void ResetWatchDiscovery()
    {
        if (string.IsNullOrWhiteSpace(_cloudQueueFolder) || !Directory.Exists(_cloudQueueFolder)) throw new IOException("Shared watch folder is unavailable.");
        _dismissedJobIds.Clear();
        _receivedJobIds.Clear();
        foreach (var job in _queue.Where(j => !string.IsNullOrWhiteSpace(j.JobId))) _receivedJobIds.Add(job.JobId);
        _seenCloudFiles.Clear();
        // Do not replay legacy non-distributed submissions or pre-rendered captures.
        foreach (var file in Directory.EnumerateFiles(_cloudQueueFolder, "*.renderjob.json"))
            if (!IsDistributedJobFile(file)) _seenCloudFiles.Add(file);
        PollCloudJobs();
    }
}
