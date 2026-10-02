using System.IO;
using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace BlenderRenderHeadless;

public partial class MainWindow
{
    private RenderJob? _reviewJob;
    private Point? _dragStart;
    private RenderJob? _singleRenderJob;
    private DateTime _nextSharedPreviewPoll;
    private bool _sharedPreviewPollBusy;

    private async void RefreshSharedPreviews()
    {
        if (_sharedPreviewPollBusy || DateTime.UtcNow < _nextSharedPreviewPoll) return;
        _sharedPreviewPollBusy = true;
        _nextSharedPreviewPoll = DateTime.UtcNow.AddSeconds(10);
        var jobs = _queue.Where(j => j.Distributed && j.TileSize == 0).ToArray();
        try
        {
            var previews = await System.Threading.Tasks.Task.Run(() => jobs.Select(job => (Job: job, Path: LatestSharedPreview(job))).ToArray());
            foreach (var preview in previews)
                if (preview.Path != null && _queue.Contains(preview.Job)) preview.Job.ThumbnailPath = preview.Path;
        }
        finally { _sharedPreviewPollBusy = false; }
    }

    internal static string? LatestSharedPreview(RenderJob job)
    {
        try
        {
            var folder = Path.Combine(job.CoordinationFolder, "_claims", SafeJobId(job.JobId));
            if (!Directory.Exists(folder)) return null;
            foreach (var marker in new DirectoryInfo(folder).EnumerateFiles("*.done").OrderByDescending(f => f.LastWriteTimeUtc).Take(8))
            {
                try
                {
                    using var document = System.Text.Json.JsonDocument.Parse(File.ReadAllText(marker.FullName));
                    if (!document.RootElement.TryGetProperty("output", out var output)) continue;
                    var path = output.GetString();
                    if (path != null && new[] { ".png", ".jpg", ".jpeg", ".bmp", ".tif", ".tiff" }.Contains(Path.GetExtension(path).ToLowerInvariant()) && File.Exists(path) && new FileInfo(path).Length > 0) return path;
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Text.Json.JsonException) { }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException) { }
        return null;
    }

    private void IgnoreJob_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is not RenderJob job || job.IsOpening || job.IsBatchReserved || job.Status == "Rendering") return;
        job.IsIgnored = !job.IsIgnored;
        if (job.IsIgnored) _queue.Move(_queue.IndexOf(job), _queue.Count - 1);
        RefreshActiveRender(); UpdateQueueState();
    }

    private void RenderOnlyJob_Click(object sender, RoutedEventArgs e)
    {
        if (_queueRunning || _blenderExe == null || _watchCleanupRunning || (sender as FrameworkElement)?.Tag is not RenderJob job || job.Status != "Waiting") return;
        // Explicitly rendering one row must not arm the remaining queue afterwards.
        AutoStartCheckBox.IsChecked = false;
        job.IsIgnored = false;
        _singleRenderJob = job;
        RenderQueueButton_Click(sender, e);
    }
    private void SetEditActions(bool editing)
    {
        CreateEditedJobButton.Visibility = DiscardEditsButton.Visibility = editing ? Visibility.Visible : Visibility.Collapsed;
        EditSettingsButton.Visibility = editing ? Visibility.Collapsed : Visibility.Visible;
    }

    private void OpenCameraDraft(CameraSetup camera)
    {
        _reviewJob = null;
        SetEditActions(false);
        EditSettingsButton.IsEnabled = CreateEditedJobButton.IsEnabled = false;
        CameraSettingsContent.IsEnabled = true;
        QueueReviewBadge.Visibility = Visibility.Collapsed;
        KeyframeRangeCheckBox.IsEnabled = true;
        SetFocusedCamera(camera);
    }

    private void QueueSelection_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (!ReferenceEquals(e.OriginalSource, QueueItems)) return;
        ReviewJob(QueueItems.SelectedItem as RenderJob);
    }

    private void ReviewJob(RenderJob? job)
    {
        _reviewJob = job;
        SetEditActions(false);
        CameraSettingsContent.IsEnabled = false;
        CameraSettingsContent.IsHitTestVisible = true;
        EditSettingsButton.IsEnabled = job != null;
        CreateEditedJobButton.IsEnabled = false;
        QueueReviewBadge.Visibility = Visibility.Visible;
        SetFocusedCamera(job == null ? null : CameraSetup.FromRenderJob(job));
    }

    private void EditSettings_Click(object sender, RoutedEventArgs e)
    {
        if (_reviewJob == null) return;
        SetEditActions(true);
        var draft = CameraSetup.FromRenderJob(_reviewJob);
        draft.DefaultStartFrame = int.Parse(draft.StartFrame);
        draft.DefaultEndFrame = int.Parse(draft.EndFrame);
        var loaded = _blendFile == _reviewJob.BlendFile ? _cameras.FirstOrDefault(c => c.CameraName == draft.CameraName) : null;
        draft.KeyframeStart = loaded?.KeyframeStart;
        draft.KeyframeEnd = loaded?.KeyframeEnd;
        KeyframeRangeCheckBox.IsEnabled = draft.KeyframeStart.HasValue && draft.KeyframeEnd.HasValue;
        KeyframeRangeCheckBox.ToolTip = KeyframeRangeCheckBox.IsEnabled ? "Use the inspected camera keyframes" : "Load this .blend to inspect camera keyframes";
        draft.SettingChanged = CameraSettingChanged;
        SetFocusedCamera(draft);
        CameraSettingsContent.IsEnabled = true;
        CreateEditedJobButton.IsEnabled = true;
        EditSettingsButton.IsEnabled = false;
        QueueReviewBadge.Visibility = Visibility.Collapsed;
        StatusText.Text = "Editing a new-job copy · original job and .blend stay unchanged";
    }

    private void DiscardEdits_Click(object sender, RoutedEventArgs e) => ReviewJob(_reviewJob);

    private void RestartJob_Click(object sender, RoutedEventArgs e)
    {
        e.Handled = true;
        if (sender is not FrameworkElement { Tag: RenderJob source } || source.Status != "Cancelled" || _queueRunning) return;
        if (!string.IsNullOrWhiteSpace(source.CloudManifestPath))
        {
            if (MessageBox.Show(this, "Resubmit this camera job to all machines watching its original NAS folder?\n\nA fresh cloud job will be published. The original stays cancelled. Existing output and overwrite settings are retained; watchers' filters and Auto start settings still apply.",
                "Restart cloud job everywhere", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) return;
            try
            {
                var path = CloudRestart.Publish(source);
                QueueIncomingJob(File.ReadAllText(path), "Cloud restart", path);
                StatusText.Text = "Cloud restart published · available to all eligible watchers";
            }
            catch (Exception ex) { MessageBox.Show(this, ex.Message, "Could not restart cloud job"); }
            return;
        }
        if (MessageBox.Show(this,
            "Add a fresh copy of this cancelled job to this PC's queue?\n\nThe original remains cancelled on all machines. Output paths and overwrite settings are retained. Use Render queue to start the new copy.",
            "Restart on this PC", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
        var draft = CameraSetup.FromRenderJob(source);
        if (Validate(draft) is { } error) { MessageBox.Show(this, error, "Check settings"); return; }
        var copy = CreateEditedCopy(source, draft);
        _queue.Add(copy);
        UpdateQueueState();
        QueueItems.SelectedItem = copy;
        StatusText.Text = "Restart copy queued on this PC · original remains cancelled";
    }

    private readonly SemaphoreSlim _previewSerial = new(1, 1);
    private async void GeneratePreview_Click(object sender, RoutedEventArgs e)
    {
        e.Handled = true;
        if (sender is not FrameworkElement { Tag: RenderJob job } || job.PreviewBusy) return;
        if (_blenderExe == null) { MessageBox.Show(this, "Choose Blender first.", "Preview"); return; }
        var previousPreview = job.ThumbnailPath;
        job.PreviewBusy = true;
        await _previewSerial.WaitAsync();
        try
        {
            var folder = Path.Combine(Path.GetTempPath(), "BlenderRenderLauncher", "previews", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(folder);
            var path = Path.Combine(folder, "preview.png");
            var packet = System.Text.Json.JsonSerializer.Serialize(new { camera = job.CameraName, frame = job.StartFrame, width = job.Width, height = job.Height, output = path });
            await RunCaptureAsync(_blenderExe, ["--background", "--factory-startup", "--disable-autoexec", job.BlendFile, "--python", ExtractScript("camera_preview.py"), "--", packet], TimeSpan.FromMinutes(3));
            if (!File.Exists(path) || new FileInfo(path).Length == 0) throw new IOException("Blender did not produce a preview.");
            // Do not overwrite a newer render preview that arrived while this was generating.
            if (job.ThumbnailPath == previousPreview) job.ThumbnailPath = path;
        }
        catch (Exception ex) { AppendLog("Preview failed: " + ex.Message); MessageBox.Show(this, ex.Message, "Could not generate preview"); }
        finally { job.PreviewBusy = false; _previewSerial.Release(); }
    }

    private void CreateEditedJob_Click(object sender, RoutedEventArgs e)
    {
        if (_reviewJob == null || _selectedCamera == null || !CreateEditedJobButton.IsEnabled) return;
        if (Validate(_selectedCamera) is { } error)
        {
            MessageBox.Show(this, error, "Check settings");
            return;
        }
        var job = CreateEditedCopy(_reviewJob, _selectedCamera);
        _queue.Add(job);
        UpdateQueueState();
        QueueItems.SelectedItem = job;
        StatusText.Text = "New job added locally · original job unchanged";
    }

    internal static RenderJob CreateEditedCopy(RenderJob source, CameraSetup draft)
    {
        var settings = RenderJob.From(draft, source.BlendFile);
        // A distinct claim namespace is essential: never inherit completion/cancellation
        // markers or batch membership from the original submitted job.
        var needsClaims = draft.RenderMode == "FINAL" && source.TileSize > 0 || draft.RenderMode == "PLAYBLAST" && source.RequiresViewport;
        return new RenderJob
        {
            BlendFile = settings.BlendFile, CameraName = settings.CameraName,
            ThumbnailPath = settings.ThumbnailPath, StartFrame = settings.StartFrame,
            EndFrame = settings.EndFrame, FrameStep = settings.FrameStep,
            OutputPath = settings.OutputPath, RenderMode = settings.RenderMode,
            Engine = settings.Engine, Width = settings.Width, Height = settings.Height,
            Scale = settings.Scale, FrameRate = settings.FrameRate, Format = settings.Format,
            Overwrite = settings.Overwrite, Placeholders = settings.Placeholders,
            IgnoreCompositor = settings.IgnoreCompositor, TransparentBackground = settings.TransparentBackground,
            ViewportShading = settings.ViewportShading, ShowOverlays = settings.ShowOverlays,
            ViewportOverlaySettingsJson = settings.ViewportOverlaySettingsJson,
            UsesCompositorOutput = settings.UsesCompositorOutput, CompositorOutputNode = settings.CompositorOutputNode,
            TileSize = draft.RenderMode == "FINAL" ? source.TileSize : 0, TileOverlap = source.TileOverlap,
            RequiresViewport = draft.RenderMode == "PLAYBLAST" && source.RequiresViewport,
            Distributed = needsClaims, JobId = Guid.NewGuid().ToString("N"),
            CoordinationFolder = needsClaims ? Path.Combine(Path.GetTempPath(), "BlenderRenderLauncher", "edited-jobs") : ""
        };
    }

    private void RefreshActiveRender()
    {
        if (ActiveRenderPanel != null)
            ActiveRenderPanel.DataContext = _queue.FirstOrDefault(job => job.Status == "Rendering" || job.IsOpening);
        var next = _queue.FirstOrDefault(job => job.Status == "Waiting" && !job.IsIgnored && !job.IsOpening);
        foreach (var job in _queue) job.IsNext = ReferenceEquals(job, next);
    }

    private void QueueJobChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(RenderJob.Status) or nameof(RenderJob.IsOpening)) RefreshActiveRender();
    }

    private void WorkerReady(RenderJob job, string line)
    {
        if (line != "BRH_SCENE_READY") return;
        job.IsOpening = false;
        StatusText.Text = $"Rendering {job.CameraName} · {job.FrameSummary}";
    }

    private void OpenBlendFile_Click(object sender, RoutedEventArgs e)
    {
        e.Handled = true;
        if (sender is not FrameworkElement { Tag: RenderJob job }) return;
        try
        {
            if (!File.Exists(job.BlendFile)) throw new FileNotFoundException("Blender file not found", job.BlendFile);
            var info = new ProcessStartInfo { FileName = _blenderExe ?? job.BlendFile, UseShellExecute = true };
            if (_blenderExe != null) info.ArgumentList.Add(job.BlendFile);
            Process.Start(info);
        }
        catch (Exception ex) { MessageBox.Show(this, ex.Message, "Could not open Blender file"); }
    }

    private void QueueMoveUp_Click(object sender, RoutedEventArgs e) { MoveQueueJob(sender, -1); e.Handled = true; }
    private void QueueMoveDown_Click(object sender, RoutedEventArgs e) { MoveQueueJob(sender, 1); e.Handled = true; }
    private void MoveQueueJob(object sender, int direction)
    {
        if (sender is not FrameworkElement { Tag: RenderJob job } || !job.CanReorder) return;
        var from = _queue.IndexOf(job);
        for (int to = from + direction; to >= 0 && to < _queue.Count; to += direction)
            if (_queue[to].CanReorder) { _queue.Move(from, to); break; }
    }

    private void QueueReorder_Start(object sender, MouseButtonEventArgs e) => _dragStart = e.GetPosition(QueueItems);
    private void QueueReorder_Over(object sender, DragEventArgs e)
    {
        if (!e.Data.GetDataPresent("BlenderRenderJob")) return;
        e.Effects = e.Data.GetData("BlenderRenderJob") is RenderJob { CanReorder: true } ? DragDropEffects.Move : DragDropEffects.None;
        e.Handled = true;
    }

    private void QueueReorder_Move(object sender, MouseEventArgs e)
    {
        if (e.LeftButton != MouseButtonState.Pressed) { _dragStart = null; return; }
        if (_dragStart is not { } start || sender is not FrameworkElement { Tag: RenderJob job } || !job.CanReorder) return;
        var point = e.GetPosition(QueueItems);
        if (Math.Abs(point.X - start.X) < SystemParameters.MinimumHorizontalDragDistance && Math.Abs(point.Y - start.Y) < SystemParameters.MinimumVerticalDragDistance) return;
        _dragStart = null;
        DragDrop.DoDragDrop((DependencyObject)sender, new DataObject("BlenderRenderJob", job), DragDropEffects.Move);
    }

    private void QueueReorder_Drop(object sender, DragEventArgs e)
    {
        if (!e.Data.GetDataPresent("BlenderRenderJob")) return;
        e.Handled = true;
        if (e.Data.GetData("BlenderRenderJob") is not RenderJob source || !_queue.Contains(source)) return;
        var row = ItemsControl.ContainerFromElement(QueueItems, e.OriginalSource as DependencyObject) as ListBoxItem;
        if (row?.DataContext is RenderJob target && source.CanReorder && target.CanReorder)
            _queue.Move(_queue.IndexOf(source), _queue.IndexOf(target));
    }
}
