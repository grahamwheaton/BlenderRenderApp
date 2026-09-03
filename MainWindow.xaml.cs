using Microsoft.Win32;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace BlenderRenderHeadless;

public partial class MainWindow : Window
{
    private readonly ObservableCollection<CameraSetup> _cameras = [];
    private readonly ObservableCollection<RenderJob> _queue = [];
    private string? _blendFile;
    private string? _blenderExe;
    private Process? _renderProcess;
    private bool _queueRunning;
    private bool _cancelRequested;
    private bool _syncingCameraSettings;
    private const string Marker = "BRH_JSON:";
    private static readonly Regex FramePattern = new(@"BRH_FRAME_DONE:(\d+)", RegexOptions.Compiled);

    public MainWindow()
    {
        InitializeComponent();
        CameraItems.ItemsSource = _cameras;
        QueueItems.ItemsSource = _queue;
        _blenderExe = FindBlender();
        StatusText.Text = _blenderExe is null ? "Set the location of blender.exe" : $"Ready · {Path.GetFileName(Path.GetDirectoryName(_blenderExe))}";
    }

    private static string? FindBlender()
    {
        var candidates = new List<string>();
        foreach (var root in new[] { Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86) })
        {
            var foundation = Path.Combine(root, "Blender Foundation");
            if (!Directory.Exists(foundation)) continue;
            var direct = Path.Combine(foundation, "blender.exe");
            if (File.Exists(direct)) candidates.Add(direct);
            try { foreach (var folder in Directory.GetDirectories(foundation)) { var exe = Path.Combine(folder, "blender.exe"); if (File.Exists(exe)) candidates.Add(exe); } }
            catch (UnauthorizedAccessException) { }
        }
        return candidates.OrderByDescending(File.GetLastWriteTimeUtc).FirstOrDefault();
    }

    private static bool TryGetBlendFile(IDataObject data, out string path)
    {
        path = string.Empty;
        if (!data.GetDataPresent(DataFormats.FileDrop) || data.GetData(DataFormats.FileDrop) is not string[] files) return false;
        path = files.FirstOrDefault(p => File.Exists(p) && Path.GetExtension(p).Equals(".blend", StringComparison.OrdinalIgnoreCase)) ?? string.Empty;
        return path.Length > 0;
    }

    private void Window_DragOver(object sender, DragEventArgs e) => UpdateDropEffect(e);
    private void DropZone_DragOver(object sender, DragEventArgs e) => UpdateDropEffect(e);
    private void DropZone_DragEnter(object sender, DragEventArgs e) { UpdateDropEffect(e); if (e.Effects == DragDropEffects.Copy) { DropZone.BorderBrush = (Brush)FindResource("Accent"); DropZone.BorderThickness = new Thickness(2); } }
    private void DropZone_DragLeave(object sender, DragEventArgs e) => ResetDropZone();
    private static void UpdateDropEffect(DragEventArgs e) { e.Effects = TryGetBlendFile(e.Data, out _) ? DragDropEffects.Copy : DragDropEffects.None; e.Handled = true; }
    private void ResetDropZone() { DropZone.BorderBrush = new SolidColorBrush(Color.FromRgb(0x48, 0x51, 0x5D)); DropZone.BorderThickness = new Thickness(1); }
    private async void Window_Drop(object sender, DragEventArgs e) { if (TryGetBlendFile(e.Data, out var path)) await LoadBlendAsync(path); }
    private async void DropZone_Drop(object sender, DragEventArgs e) { ResetDropZone(); e.Handled = true; if (TryGetBlendFile(e.Data, out var path)) await LoadBlendAsync(path); }
    private void DropZone_Click(object sender, MouseButtonEventArgs e) => ChooseBlend();

    private async void ChooseBlend()
    {
        var dialog = new OpenFileDialog { Filter = "Blender files (*.blend)|*.blend", Title = "Choose a Blender file" };
        if (dialog.ShowDialog(this) == true) await LoadBlendAsync(dialog.FileName);
    }

    private void BlenderButton_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog { Filter = "Blender (blender.exe)|blender.exe", Title = "Find blender.exe" };
        if (dialog.ShowDialog(this) == true) { _blenderExe = dialog.FileName; StatusText.Text = "Blender selected · drop or choose a file"; }
    }

    private async Task LoadBlendAsync(string path)
    {
        if (_queueRunning) return;
        _blendFile = path;
        DropTitleText.Text = Path.GetFileName(path); DropSubtitleText.Text = path; DropSubtitleText.ToolTip = path;
        _cameras.Clear(); SelectAllCheckBox.IsChecked = false; AddQueueButton.IsEnabled = false; CameraCountText.Text = "Inspecting…";
        if (_blenderExe is null) { StatusText.Text = "Choose blender.exe to inspect this file"; return; }
        StatusText.Text = "Reading cameras and generating previews…"; SetLog("Inspecting the Blender file and rendering camera thumbnails…");
        try
        {
            var script = Path.Combine(AppContext.BaseDirectory, "Scripts", "inspect_scene.py");
            var thumbnailDirectory = Path.Combine(Path.GetTempPath(), "BlenderRenderLauncher", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(thumbnailDirectory);
            var result = await RunCaptureAsync(_blenderExe, ["--background", "--factory-startup", path, "--python", script, "--", thumbnailDirectory], TimeSpan.FromMinutes(3));
            var line = result.Split('\n').LastOrDefault(x => x.StartsWith(Marker, StringComparison.Ordinal));
            if (line is null) throw new InvalidOperationException("Blender did not return scene information.\n\n" + Tail(result, 1500));
            var scene = JsonSerializer.Deserialize<SceneInfo>(line[Marker.Length..]) ?? throw new InvalidOperationException("Could not read Blender's response.");
            foreach (var name in scene.cameras)
            {
                var cameraResolution = scene.camera_settings.GetValueOrDefault(name) ??
                    new CameraResolutionInfo(false, scene.resolution_x, scene.resolution_y, scene.resolution_percentage);
                var cameraSetup = new CameraSetup
                {
                    CameraName = name, IsChecked = name == scene.active_camera, IsActive = name == scene.active_camera,
                    ThumbnailPath = scene.thumbnails.GetValueOrDefault(name),
                    UsesPerCameraResolution = cameraResolution.uses_per_camera_resolution,
                    StartFrame = scene.frame_start.ToString(CultureInfo.InvariantCulture), EndFrame = scene.frame_end.ToString(CultureInfo.InvariantCulture),
                    DefaultStartFrame = scene.frame_start, DefaultEndFrame = scene.frame_end,
                    KeyframeStart = scene.camera_keyframes.GetValueOrDefault(name)?.start,
                    KeyframeEnd = scene.camera_keyframes.GetValueOrDefault(name)?.end,
                    OutputPath = CameraOutputPath(scene.output_path, name), Engine = "KEEP", RenderMode = "FINAL",
                    Width = cameraResolution.resolution_x.ToString(CultureInfo.InvariantCulture), Height = cameraResolution.resolution_y.ToString(CultureInfo.InvariantCulture),
                    Scale = cameraResolution.resolution_percentage.ToString(CultureInfo.InvariantCulture),
                    FrameRate = scene.frame_rate.ToString("0.###", CultureInfo.InvariantCulture), Format = scene.file_format,
                    Overwrite = scene.use_overwrite, Placeholders = scene.use_placeholder, IgnoreCompositor = !scene.use_compositing
                };
                cameraSetup.SettingChanged = CameraSettingChanged;
                _cameras.Add(cameraSetup);
            }
            ApplyFrameRangeMode();
            CameraCountText.Text = $"{scene.cameras.Count} camera{(scene.cameras.Count == 1 ? "" : "s")} · active camera checked";
            AddQueueButton.IsEnabled = scene.cameras.Count > 0;
            StatusText.Text = scene.cameras.Count > 0 ? "Choose cameras and settings, then add them to the queue" : "No cameras found";
            SetLog(scene.cameras.Count > 0 ? $"Scene ready · {scene.render_engine} · {scene.resolution_x}×{scene.resolution_y} · {scene.file_format}" : "This file has no camera objects.");
        }
        catch (Exception ex) { CameraCountText.Text = "Inspection failed"; StatusText.Text = "Could not inspect file"; SetLog(ex.Message); }
    }

    private static string CameraOutputPath(string savedPath, string camera)
    {
        var trimmed = savedPath.TrimEnd('/', '\\');
        var directory = savedPath.EndsWith('/') || savedPath.EndsWith('\\') ? trimmed : Path.GetDirectoryName(savedPath) ?? trimmed;
        return Path.Combine(directory, Sanitize(camera)) + Path.DirectorySeparatorChar;
    }
    private static string Sanitize(string value) => string.Concat(value.Select(c => Path.GetInvalidFileNameChars().Contains(c) ? '_' : c));

    private void CameraOutputBrowse_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as Button)?.Tag is not CameraSetup camera) return;
        var dialog = new OpenFolderDialog { Title = $"Choose output folder for {camera.CameraName}", Multiselect = false };
        if (dialog.ShowDialog(this) == true) camera.OutputPath = dialog.FolderName + Path.DirectorySeparatorChar;
    }

    private void OutputToken_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (sender is not ComboBox combo || combo.Tag is not CameraSetup camera || combo.SelectedItem is not ComboBoxItem item || item.Tag is not string token) return;
        camera.OutputPath += token;
        combo.SelectedIndex = 0;
    }

    private void FrameRangeMode_Changed(object sender, RoutedEventArgs e)
    {
        if (sender == StillsOnlyCheckBox && StillsOnlyCheckBox.IsChecked == true) KeyframeRangeCheckBox.IsChecked = false;
        if (sender == KeyframeRangeCheckBox && KeyframeRangeCheckBox.IsChecked == true) StillsOnlyCheckBox.IsChecked = false;
        ApplyFrameRangeMode();
    }

    private void StillFrameText_Changed(object sender, TextChangedEventArgs e)
    {
        if (StillsOnlyCheckBox?.IsChecked == true) ApplyFrameRangeMode();
    }

    private void SelectAll_Changed(object sender, RoutedEventArgs e)
    {
        var isChecked = SelectAllCheckBox?.IsChecked == true;
        foreach (var camera in _cameras) camera.IsChecked = isChecked;
    }

    private void CameraSettingChanged(CameraSetup source, string propertyName)
    {
        if (_syncingCameraSettings || (Keyboard.Modifiers & ModifierKeys.Alt) == 0) return;
        _syncingCameraSettings = true;
        try
        {
            foreach (var camera in _cameras.Where(camera => camera != source && camera.IsChecked))
                camera.CopySettingFrom(source, propertyName);
        }
        finally { _syncingCameraSettings = false; }
    }

    private void ApplyFrameRangeMode()
    {
        if (StillsOnlyCheckBox?.IsChecked == true && int.TryParse(StillFrameText?.Text, out var stillFrame))
        {
            foreach (var camera in _cameras) { camera.StartFrame = stillFrame.ToString(CultureInfo.InvariantCulture); camera.EndFrame = camera.StartFrame; }
            return;
        }
        foreach (var camera in _cameras)
        {
            if (KeyframeRangeCheckBox?.IsChecked == true && camera.KeyframeStart.HasValue && camera.KeyframeEnd.HasValue)
            {
                camera.StartFrame = camera.KeyframeStart.Value.ToString(CultureInfo.InvariantCulture);
                camera.EndFrame = camera.KeyframeEnd.Value.ToString(CultureInfo.InvariantCulture);
            }
            else
            {
                camera.StartFrame = camera.DefaultStartFrame.ToString(CultureInfo.InvariantCulture);
                camera.EndFrame = camera.DefaultEndFrame.ToString(CultureInfo.InvariantCulture);
            }
        }
    }

    private void AddQueueButton_Click(object sender, RoutedEventArgs e)
    {
        var selected = _cameras.Where(c => c.IsChecked).ToList();
        if (selected.Count == 0) { MessageBox.Show(this, "Tick at least one camera first.", "No cameras selected", MessageBoxButton.OK, MessageBoxImage.Information); return; }
        var errors = selected.Select(Validate).Where(x => x is not null).ToList();
        if (errors.Count > 0) { MessageBox.Show(this, string.Join("\n", errors), "Check camera settings", MessageBoxButton.OK, MessageBoxImage.Warning); return; }
        foreach (var camera in selected) _queue.Add(RenderJob.From(camera, _blendFile!));
        UpdateQueueState();
        StatusText.Text = $"Added {selected.Count} job{(selected.Count == 1 ? "" : "s")} · {_queue.Count} in queue";
    }

    private static string? Validate(CameraSetup c)
    {
        if (!int.TryParse(c.StartFrame, out var start) || !int.TryParse(c.EndFrame, out var end) || end < start) return $"{c.CameraName}: invalid frame range.";
        if (!int.TryParse(c.Width, out var width) || width < 1 || !int.TryParse(c.Height, out var height) || height < 1) return $"{c.CameraName}: width and height must be positive whole numbers.";
        if (!int.TryParse(c.Scale, out var scale) || scale is < 1 or > 32767) return $"{c.CameraName}: scale must be a positive whole number (maximum 32767).";
        if (!double.TryParse(c.FrameRate, NumberStyles.Float, CultureInfo.InvariantCulture, out var frameRate) || frameRate <= 0 || frameRate > 32767) return $"{c.CameraName}: frame rate must be a positive number (maximum 32767).";
        if (string.IsNullOrWhiteSpace(c.OutputPath)) return $"{c.CameraName}: output path is empty.";
        return null;
    }

    private void RemoveQueueItem_Click(object sender, RoutedEventArgs e) { if ((sender as Button)?.Tag is RenderJob job && job.CanRemove) { _queue.Remove(job); UpdateQueueState(); } }
    private void ClearQueueButton_Click(object sender, RoutedEventArgs e) { foreach (var job in _queue.Where(j => j.Status is "Complete" or "Failed" or "Cancelled").ToList()) _queue.Remove(job); UpdateQueueState(); }
    private void UpdateQueueState() { EmptyQueueText.Visibility = _queue.Count == 0 ? Visibility.Visible : Visibility.Collapsed; RenderQueueButton.IsEnabled = _queue.Any(j => j.Status == "Waiting") || _queueRunning; }

    private async void RenderQueueButton_Click(object sender, RoutedEventArgs e)
    {
        if (_queueRunning) { _cancelRequested = true; try { _renderProcess?.Kill(true); } catch { } return; }
        if (_blenderExe is null) return;
        _queueRunning = true; _cancelRequested = false; SetUiRunning(true); SetLog("Starting render queue…");
        foreach (var job in _queue.Where(j => j.Status == "Waiting").ToList())
        {
            if (_cancelRequested) { job.Status = "Cancelled"; break; }
            job.Begin(); StatusText.Text = $"Rendering {job.CameraName} · {job.FrameSummary}";
            AppendLog($"\n[{job.CameraName}] {job.ModeSummary} · {job.FrameSummary}");
            try
            {
                var script = Path.Combine(AppContext.BaseDirectory, "Scripts", "render_scene.py");
                var args = new[] { "--background", job.BlendFile, "--python", script, "--", job.CameraName, job.StartFrame, job.EndFrame, job.OutputPath, job.Engine, job.Width, job.Height, job.Scale, job.FrameRate, job.Format, job.RenderMode, job.Overwrite ? "1" : "0", job.Placeholders ? "1" : "0", job.IgnoreCompositor ? "1" : "0" };
                var code = await RunStreamingAsync(_blenderExe, args, job);
                job.Status = _cancelRequested ? "Cancelled" : code == 0 ? "Complete" : "Failed";
                if (code == 0 && !_cancelRequested) job.Finish();
            }
            catch (Exception ex) { job.Status = _cancelRequested ? "Cancelled" : "Failed"; AppendLog(ex.Message); }
        }
        _queueRunning = false; _renderProcess = null; SetUiRunning(false); UpdateQueueState();
        StatusText.Text = _cancelRequested ? "Queue cancelled" : _queue.Any(j => j.Status == "Failed") ? "Queue finished with errors" : "Queue finished";
        AppendLog(_cancelRequested ? "\nQueue cancelled." : "\nQueue complete.");
    }

    private void SetUiRunning(bool running)
    {
        DropZone.IsEnabled = CameraItems.IsEnabled = AddQueueButton.IsEnabled = BlenderButton.IsEnabled = !running;
        foreach (var job in _queue) job.CanRemove = !running;
        RenderQueueButton.Content = running ? "Cancel queue" : "Render queue";
    }

    private static ProcessStartInfo MakeStartInfo(string exe, IEnumerable<string> args) { var psi = new ProcessStartInfo(exe) { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true }; foreach (var arg in args) psi.ArgumentList.Add(arg); return psi; }
    private static async Task<string> RunCaptureAsync(string exe, IEnumerable<string> args, TimeSpan timeout)
    {
        using var process = new Process { StartInfo = MakeStartInfo(exe, args) }; process.Start();
        var stdout = process.StandardOutput.ReadToEndAsync(); var stderr = process.StandardError.ReadToEndAsync(); using var source = new CancellationTokenSource(timeout);
        try { await process.WaitForExitAsync(source.Token); } catch (OperationCanceledException) { try { process.Kill(true); } catch { } throw new TimeoutException($"Blender took longer than {timeout.TotalSeconds:0} seconds to inspect this file."); }
        var result = (await stdout) + "\n" + (await stderr); if (process.ExitCode != 0) throw new InvalidOperationException("Blender could not open the file.\n\n" + Tail(result, 1800)); return result;
    }
    private async Task<int> RunStreamingAsync(string exe, IEnumerable<string> args, RenderJob job)
    {
        _renderProcess = new Process { StartInfo = MakeStartInfo(exe, args), EnableRaisingEvents = true };
        _renderProcess.OutputDataReceived += (_, e) =>
        {
            if (string.IsNullOrWhiteSpace(e.Data)) return;
            Dispatcher.Invoke(() =>
            {
                AppendLog(e.Data);
                var match = FramePattern.Match(e.Data);
                if (match.Success && int.TryParse(match.Groups[1].Value, out var frame)) job.ReportFrame(frame);
            });
        };
        _renderProcess.ErrorDataReceived += (_, e) => { if (!string.IsNullOrWhiteSpace(e.Data)) Dispatcher.Invoke(() => AppendLog(e.Data)); };
        _renderProcess.Start(); _renderProcess.BeginOutputReadLine(); _renderProcess.BeginErrorReadLine(); await _renderProcess.WaitForExitAsync(); return _renderProcess.ExitCode;
    }
    private void SetLog(string text) { LogBox.Text = text; EmptyLogText.Visibility = string.IsNullOrEmpty(text) ? Visibility.Visible : Visibility.Collapsed; LogBox.ScrollToEnd(); }
    private void AppendLog(string text) { EmptyLogText.Visibility = Visibility.Collapsed; LogBox.AppendText(text + Environment.NewLine); LogBox.ScrollToEnd(); }
    private static string Tail(string value, int length) => value.Length <= length ? value : value[^length..];
    private sealed record SceneInfo(List<string> cameras, string? active_camera, int frame_start, int frame_end, string output_path, string render_engine, int resolution_x, int resolution_y, int resolution_percentage, string file_format, double frame_rate, bool use_overwrite, bool use_placeholder, bool use_compositing, Dictionary<string, string> thumbnails, Dictionary<string, CameraResolutionInfo> camera_settings, Dictionary<string, CameraKeyframeInfo?> camera_keyframes);
    private sealed record CameraResolutionInfo(bool uses_per_camera_resolution, int resolution_x, int resolution_y, int resolution_percentage);
    private sealed record CameraKeyframeInfo(int start, int end);
}

public class CameraSetup : NotifyBase
{
    public Action<CameraSetup, string>? SettingChanged { get; set; }
    public string CameraName { get; set; } = "";
    private bool _isChecked; public bool IsChecked { get => _isChecked; set => Set(ref _isChecked, value); }
    public bool IsActive { get; set; }
    public string? ThumbnailPath { get; set; }
    public bool UsesPerCameraResolution { get; set; }
    public Visibility PerCameraResolutionVisibility => UsesPerCameraResolution ? Visibility.Visible : Visibility.Collapsed;
    public Visibility ActiveVisibility => IsActive ? Visibility.Visible : Visibility.Collapsed;
    private string _startFrame = "1"; public string StartFrame { get => _startFrame; set => SetSetting(ref _startFrame, value); }
    private string _endFrame = "250"; public string EndFrame { get => _endFrame; set => SetSetting(ref _endFrame, value); }
    public int DefaultStartFrame { get; set; } = 1; public int DefaultEndFrame { get; set; } = 250; public int? KeyframeStart { get; set; } public int? KeyframeEnd { get; set; }
    private string _renderMode = "FINAL"; public string RenderMode { get => _renderMode; set => SetSetting(ref _renderMode, value); }
    private string _engine = "KEEP"; public string Engine { get => _engine; set => SetSetting(ref _engine, value); }
    private string _outputPath = ""; public string OutputPath { get => _outputPath; set => SetSetting(ref _outputPath, value); }
    private string _width = "1920"; public string Width { get => _width; set => SetSetting(ref _width, value); }
    private string _height = "1080"; public string Height { get => _height; set => SetSetting(ref _height, value); }
    private string _scale = "100"; public string Scale { get => _scale; set => SetSetting(ref _scale, value); }
    private string _frameRate = "24"; public string FrameRate { get => _frameRate; set => SetSetting(ref _frameRate, value); }
    private string _format = "PNG"; public string Format { get => _format; set => SetSetting(ref _format, value); }
    private bool _overwrite = true; public bool Overwrite { get => _overwrite; set => SetSetting(ref _overwrite, value); }
    private bool _placeholders; public bool Placeholders { get => _placeholders; set => SetSetting(ref _placeholders, value); }
    private bool _ignoreCompositor; public bool IgnoreCompositor { get => _ignoreCompositor; set => SetSetting(ref _ignoreCompositor, value); }

    private void SetSetting<T>(ref T field, T value, [CallerMemberName] string propertyName = "")
    {
        if (Set(ref field, value, propertyName)) SettingChanged?.Invoke(this, propertyName);
    }

    public void CopySettingFrom(CameraSetup source, string propertyName)
    {
        switch (propertyName)
        {
            case nameof(StartFrame): StartFrame = source.StartFrame; break;
            case nameof(EndFrame): EndFrame = source.EndFrame; break;
            case nameof(RenderMode): RenderMode = source.RenderMode; break;
            case nameof(Engine): Engine = source.Engine; break;
            case nameof(OutputPath): OutputPath = source.OutputPath; break;
            case nameof(Width): Width = source.Width; break;
            case nameof(Height): Height = source.Height; break;
            case nameof(Scale): Scale = source.Scale; break;
            case nameof(FrameRate): FrameRate = source.FrameRate; break;
            case nameof(Format): Format = source.Format; break;
            case nameof(Overwrite): Overwrite = source.Overwrite; break;
            case nameof(Placeholders): Placeholders = source.Placeholders; break;
            case nameof(IgnoreCompositor): IgnoreCompositor = source.IgnoreCompositor; break;
        }
    }
}

public class RenderJob : NotifyBase
{
    public string BlendFile { get; init; } = ""; public string CameraName { get; init; } = ""; public string StartFrame { get; init; } = ""; public string EndFrame { get; init; } = ""; public string OutputPath { get; init; } = "";
    public string RenderMode { get; init; } = "FINAL"; public string Engine { get; init; } = "KEEP"; public string Width { get; init; } = ""; public string Height { get; init; } = ""; public string Scale { get; init; } = ""; public string FrameRate { get; init; } = "24"; public string Format { get; init; } = "PNG";
    public bool Overwrite { get; init; } public bool Placeholders { get; init; } public bool IgnoreCompositor { get; init; }
    private string _status = "Waiting"; public string Status { get => _status; set { if (Set(ref _status, value)) OnPropertyChanged(nameof(StatusBrush)); } }
    private bool _canRemove = true; public bool CanRemove { get => _canRemove; set => Set(ref _canRemove, value); }
    private double _progress; public double Progress { get => _progress; private set => Set(ref _progress, value); }
    private string _estimate = "Waiting"; public string Estimate { get => _estimate; private set => Set(ref _estimate, value); }
    private DateTime _startedAt; private int _lastReportedFrame = int.MinValue;
    public string FrameSummary => $"Frames {StartFrame}–{EndFrame}"; public string ModeSummary => RenderMode == "PLAYBLAST" ? "Playblast" : "Final";
    public Brush StatusBrush => Status switch { "Complete" => Brushes.LightGreen, "Failed" => Brushes.Salmon, "Rendering" => Brushes.Orange, "Cancelled" => Brushes.Gray, _ => Brushes.LightGray };
    public void Begin() { _startedAt = DateTime.Now; _lastReportedFrame = int.MinValue; Progress = 0; Estimate = "Estimating…"; Status = "Rendering"; }
    public void ReportFrame(int frame)
    {
        if (frame <= _lastReportedFrame || !int.TryParse(StartFrame, out var start) || !int.TryParse(EndFrame, out var end)) return;
        _lastReportedFrame = frame;
        var total = Math.Max(1, end - start + 1);
        var completed = Math.Clamp(frame - start + 1, 0, total);
        Progress = 100.0 * completed / total;
        if (completed < 1) { Estimate = "Estimating…"; return; }
        var elapsed = DateTime.Now - _startedAt;
        var remaining = TimeSpan.FromTicks((long)(elapsed.Ticks / (double)completed * (total - completed)));
        var finish = DateTime.Now + remaining;
        var duration = remaining.TotalHours >= 1 ? $"{remaining.TotalHours:0.0}h" : remaining.TotalMinutes >= 1 ? $"{remaining.TotalMinutes:0}m" : $"{Math.Max(1, remaining.TotalSeconds):0}s";
        Estimate = $"Est. {finish:H:mm} · {duration}";
    }
    public void Finish() { Progress = 100; Estimate = $"Finished {DateTime.Now:H:mm}"; }
    public static RenderJob From(CameraSetup c, string blend) => new() { BlendFile = blend, CameraName = c.CameraName, StartFrame = c.StartFrame, EndFrame = c.EndFrame, OutputPath = ResolveTokens(c.OutputPath, c.CameraName, blend), RenderMode = c.RenderMode, Engine = c.Engine, Width = c.Width, Height = c.Height, Scale = c.Scale, FrameRate = c.FrameRate, Format = c.Format, Overwrite = c.Overwrite, Placeholders = c.Placeholders, IgnoreCompositor = c.IgnoreCompositor };
    private static string ResolveTokens(string template, string cameraName, string blendFile) => template
        .Replace("{camera_name}", Sanitize(cameraName), StringComparison.OrdinalIgnoreCase)
        .Replace("{blend_name}", Sanitize(Path.GetFileNameWithoutExtension(blendFile)), StringComparison.OrdinalIgnoreCase);
    private static string Sanitize(string value) => string.Concat(value.Select(c => Path.GetInvalidFileNameChars().Contains(c) ? '_' : c));
}

public abstract class NotifyBase : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;
    protected bool Set<T>(ref T field, T value, [CallerMemberName] string? name = null) { if (EqualityComparer<T>.Default.Equals(field, value)) return false; field = value; OnPropertyChanged(name); return true; }
    protected void OnPropertyChanged([CallerMemberName] string? name = null) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
