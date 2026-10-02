using System.IO;
using System.Windows;

namespace BlenderRenderHeadless;

public static class AutomaticRetryPolicy
{
    public const int MaximumRetries = 3;
    public const int DelaySeconds = 30;
    public static bool Eligible(RenderJob job, bool enabled, bool cancelled, int retries) =>
        enabled && !cancelled && retries < MaximumRetries && job.Status == "Failed" && !job.IsGloballyCancelled && !job.HasGlobalCancellationMarker();
}

public partial class MainWindow
{
    private bool _watchCleanupRunning;
    private void ForceOverwrite_Changed(object sender, RoutedEventArgs e)
    {
        if (IsInitialized && !_suppressWatchChange && ForceOverwriteCheckBox != null) SaveCurrentSettings();
    }
    private void AutoRetry_Changed(object sender, RoutedEventArgs e)
    {
        if (IsInitialized && !_suppressWatchChange && AutoRetryCheckBox != null) SaveCurrentSettings();
    }
    private async Task<bool> WaitForAutomaticRetry(RenderJob job, int retries)
    {
        if (!AutomaticRetryPolicy.Eligible(job, AutoRetryCheckBox.IsChecked == true, _cancelRequested, retries)) return false;
        job.Status = "Retry pending";
        for (var remaining = AutomaticRetryPolicy.DelaySeconds; remaining > 0; remaining--)
        {
            if (_cancelRequested || job.HasGlobalCancellationMarker()) { job.Status = "Cancelled"; return false; }
            if (AutoRetryCheckBox.IsChecked != true) { job.Status = "Failed"; job.WorkerStatus = "Auto retry disabled"; return false; }
            job.WorkerStatus = $"Auto retry {retries + 1}/{AutomaticRetryPolicy.MaximumRetries} in {remaining}s";
            await Task.Delay(1000);
        }
        if (_cancelRequested || job.HasGlobalCancellationMarker()) { job.Status = "Cancelled"; return false; }
        if (AutoRetryCheckBox.IsChecked != true) { job.Status = "Failed"; return false; }
        job.Status = "Waiting";
        return true;
    }
    private async void DeleteWatchFolder_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            if (_watchCleanupRunning || _queueRunning || _exportSerial.CurrentCount == 0) throw new IOException("Stop this PC's render queue and wait for exports before clearing the watch folder.");
            var root = WatchCleanup.Validate(_cloudQueueFolder ?? "");
            if (MessageBox.Show(this, $"Clear ALL files and subfolders inside:\n{root}\n\nThis affects EVERY watching machine. Stop rendering and sending jobs on all PCs first. Queued jobs on other PCs must also be stopped.\n\nContents will be moved to a recovery folder beside CloudRender. Rendered images outside this folder are not touched. Continue?", "Delete watch folder — shared action", MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No) != MessageBoxResult.Yes) return;
            // Prevent local discovery/autostart while the directory is being emptied.
            WatchModeCheckBox.IsChecked = false;
            _autoStartAt = null;
            SetUiRunning(true);
            _watchCleanupRunning = true;
            RenderQueueButton.IsEnabled = false;
            WatchModeCheckBox.IsEnabled = false;
            try
            {
                var archive = await Task.Run(() => WatchCleanup.ArchiveContents(root));
                foreach (var job in _queue.Where(j => j.Distributed && string.Equals(j.CoordinationFolder.TrimEnd('\\'), root, StringComparison.OrdinalIgnoreCase) && j.Status is "Waiting" or "Failed" or "Retry pending")) job.Status = "Cancelled";
                _seenCloudFiles.Clear();
                AppendLog("Watch folder cleared; recoverable contents: " + archive);
                MessageBox.Show(this, "Watch folder cleared. Contents can be recovered from:\n" + archive + "\n\nWatch is now off on this PC.", "Watch folder cleared");
            }
            finally { _watchCleanupRunning = false; WatchModeCheckBox.IsEnabled = true; SetUiRunning(false); UpdateQueueState(); }
        }
        catch (Exception ex) { AppendLog("Watch cleanup failed: " + ex); MessageBox.Show(this, ex.Message + "\nAny already-moved files remain in the CloudRender-cleared recovery folder beside CloudRender.", "Watch folder not fully cleared"); }
    }
}
