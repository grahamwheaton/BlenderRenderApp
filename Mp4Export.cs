using Microsoft.Win32;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace BlenderRenderHeadless;

public sealed class Mp4ExportWindow : Window
{
    private readonly RenderJob _job;
    private readonly ComboBox _quality = new() { ItemsSource = new[] { "High — CRF 18", "Medium — CRF 23", "Small file — CRF 28" }, SelectedIndex = 0, Height = 34 };
    private readonly TextBox _fps = new();
    private readonly TextBlock _message = new() { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 14, 0, 14) };
    private readonly Button _export = new() { Content = "Save MP4…" };
    private readonly Button _cancel = new() { Content = "Close", Margin = new Thickness(10, 0, 0, 0) };
    private readonly ProgressBar _progress = new() { Height = 7, Maximum = 100, Margin = new Thickness(0, 0, 0, 16) };
    private CancellationTokenSource? _cancellation;

    public Mp4ExportWindow(RenderJob job)
    {
        _job = job;
        Title = "Make MP4"; Width = 480; SizeToContent = SizeToContent.Height;
        ResizeMode = ResizeMode.NoResize; WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Background = new SolidColorBrush(Color.FromRgb(27, 33, 40));
        var panel = new StackPanel { Margin = new Thickness(22) }; Content = panel;
        panel.Children.Add(new TextBlock { Text = job.CameraName + " — Make MP4", FontSize = 18, Margin = new Thickness(0, 0, 0, 18) });
        panel.Children.Add(new TextBlock { Text = "Quality", Margin = new Thickness(0, 0, 0, 6) }); panel.Children.Add(_quality);
        panel.Children.Add(new TextBlock { Text = "Playback FPS (job FPS ÷ frame step)", Margin = new Thickness(0, 14, 0, 6) });
        double.TryParse(job.FrameRate, CultureInfo.InvariantCulture, out var fps);
        int.TryParse(job.FrameStep, out var step);
        _fps.Text = ((fps > 0 ? fps : 24) / Math.Max(1, step)).ToString("0.########", CultureInfo.InvariantCulture);
        panel.Children.Add(_fps);
        _message.Text = "H.264 MP4 at original resolution. Odd dimensions are padded by one pixel. No audio; transparency is not preserved. Original frames are never changed.";
        panel.Children.Add(_message); panel.Children.Add(_progress);
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        buttons.Children.Add(_export); buttons.Children.Add(_cancel); panel.Children.Add(buttons);
        _export.Click += Export;
        _cancel.Click += (_, _) => { if (_cancellation != null) _cancellation.Cancel(); else Close(); };
        Closing += (_, e) => { if (_cancellation != null) { e.Cancel = true; _cancellation.Cancel(); } };
    }

    private async void Export(object sender, RoutedEventArgs e)
    {
        if (!double.TryParse(_fps.Text, CultureInfo.InvariantCulture, out var fps) || !double.IsFinite(fps) || fps <= 0 || fps > 240)
        { _message.Text = "Enter a playback frame rate greater than 0 and no more than 240."; return; }
        var ffmpeg = Mp4Encoder.FindFfmpeg();
        if (ffmpeg == null)
        {
            var locate = new OpenFileDialog { Title = "Locate ffmpeg.exe (or place it beside the launcher)", Filter = "FFmpeg|ffmpeg.exe" };
            if (locate.ShowDialog(this) != true) return;
            ffmpeg = locate.FileName;
        }
        var save = new SaveFileDialog { Title = "Save MP4", Filter = "MP4 video|*.mp4", DefaultExt = ".mp4", AddExtension = true,
            FileName = string.Concat(_job.CameraName.Select(c => Path.GetInvalidFileNameChars().Contains(c) ? '_' : c)) + ".mp4", OverwritePrompt = true };
        if (save.ShowDialog(this) != true) return;
        if (!Path.GetExtension(save.FileName).Equals(".mp4", StringComparison.OrdinalIgnoreCase))
        { _message.Text = "Choose an output filename ending in .mp4."; return; }
        _cancellation = new CancellationTokenSource();
        _export.IsEnabled = _quality.IsEnabled = _fps.IsEnabled = false; _cancel.Content = "Cancel export";
        var token = _cancellation.Token;
        try
        {
            _message.Text = "Checking sequence…"; _progress.Value = 0;
            var files = await Task.Run(() => Mp4Encoder.ResolveFiles(_job, token), token);
            if (files == null)
            {
                var first = new OpenFileDialog { Title = $"Select frame {_job.StartFrame} of this sequence", Filter = "Images|*.png;*.jpg;*.jpeg;*.tif;*.tiff" };
                if (first.ShowDialog(this) != true) return;
                files = await Task.Run(() => Mp4Encoder.FromFirstFile(_job, first.FileName, token), token);
            }
            var crf = new[] { 18, 23, 28 }[_quality.SelectedIndex];
            var progress = new Progress<int>(frame => { _progress.Value = 100.0 * frame / files.Count; _message.Text = $"Encoding {frame} / {files.Count} frames…"; });
            await Task.Run(() => Mp4Encoder.Encode(ffmpeg, files, save.FileName, fps, crf, progress, token), token);
            _progress.Value = 100; _message.Text = "Saved: " + save.FileName;
        }
        catch (OperationCanceledException) { _message.Text = "Export cancelled. Original frames and any previous MP4 were kept."; }
        catch (Exception ex) { _message.Text = "Export failed: " + ex.Message; }
        finally { _cancellation.Dispose(); _cancellation = null; _export.IsEnabled = _quality.IsEnabled = _fps.IsEnabled = true; _cancel.Content = "Close"; }
    }
}

public static class Mp4Encoder
{
    public static string? FindFfmpeg()
    {
        foreach (var directory in new[] { AppContext.BaseDirectory }.Concat((Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator)))
        {
            try { var file = new FileInfo(Path.Combine(directory.Trim('"'), "ffmpeg.exe")); if (file.Exists) return file.ResolveLinkTarget(true)?.FullName ?? file.FullName; }
            catch { /* Ignore inaccessible PATH entries. */ }
        }
        return null;
    }

    private static IEnumerable<int> Frames(RenderJob job)
    {
        var start = int.Parse(job.StartFrame); var end = int.Parse(job.EndFrame); var step = int.Parse(job.FrameStep);
        if (step < 1 || end < start) throw new InvalidOperationException("Invalid frame range.");
        for (long frame = start; frame <= end; frame += step) yield return (int)frame;
    }

    public static List<string>? ResolveFiles(RenderJob job, CancellationToken token)
    {
        var files = new List<string>();
        var safeId = string.Concat(job.JobId.Where(c => char.IsLetterOrDigit(c) || c is '-' or '_' or '.'));
        foreach (var frame in Frames(job))
        {
            token.ThrowIfCancellationRequested();
            job.RenderedFiles.TryGetValue(frame, out var path);
            if (string.IsNullOrWhiteSpace(path) && job.Distributed && safeId.Length > 0)
            {
                var marker = Path.Combine(job.CoordinationFolder, "_claims", safeId, frame + ".done");
                if (File.Exists(marker)) { using var json = JsonDocument.Parse(File.ReadAllText(marker)); path = json.RootElement.GetProperty("output").GetString(); }
            }
            if (string.IsNullOrWhiteSpace(path)) return null;
            files.Add(path);
        }
        Validate(files, token); return files;
    }

    public static List<string> FromFirstFile(RenderJob job, string first, CancellationToken token)
    {
        var name = Path.GetFileNameWithoutExtension(first);
        var match = Regex.Match(name, int.Parse(job.StartFrame) < 0 ? @"-\d+(?!.*\d)" : @"\d+(?!.*\d)");
        if (!match.Success || int.Parse(match.Value) != int.Parse(job.StartFrame))
            throw new InvalidOperationException("Choose the first frame; its last number must match the job start frame.");
        var digits = match.Value.TrimStart('-').Length;
        var files = Frames(job).Select(frame => Path.Combine(Path.GetDirectoryName(first)!,
            name[..match.Index] + (frame < 0 ? "-" : "") + Math.Abs((long)frame).ToString("D" + digits) + name[(match.Index + match.Length)..] + Path.GetExtension(first))).ToList();
        Validate(files, token); return files;
    }

    private static void Validate(List<string> files, CancellationToken token)
    {
        foreach (var path in files)
        {
            token.ThrowIfCancellationRequested();
            if (!new[] { ".png", ".jpg", ".jpeg", ".tif", ".tiff" }.Contains(Path.GetExtension(path).ToLowerInvariant()))
                throw new InvalidOperationException("Only PNG, JPEG and TIFF sequences are supported. EXR needs colour-managed conversion first.");
            if (!File.Exists(path) || new FileInfo(path).Length == 0) throw new IOException("Missing or empty frame: " + path);
        }
        if (files.Count < 2) throw new InvalidOperationException("At least two frames are required.");
    }

    public static async Task Encode(string ffmpeg, IReadOnlyList<string> files, string destination, double fps, int crf, IProgress<int> progress, CancellationToken token, bool overwrite = true)
    {
        // Encode a contiguous stream of the exact frames, including stepped ranges,
        // without staging/copying the sequence or relying on filename glob ordering.
        var temporary = Path.Combine(Path.GetDirectoryName(destination)!, ".mp4-" + Guid.NewGuid().ToString("N") + ".mp4");
        var info = new ProcessStartInfo(ffmpeg) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var arg in new[] { "-hide_banner", "-loglevel", "error", "-nostats", "-progress", "pipe:1", "-f", "image2pipe", "-framerate", fps.ToString("R", CultureInfo.InvariantCulture), "-i", "pipe:0", "-an", "-vf", "pad=ceil(iw/2)*2:ceil(ih/2)*2", "-c:v", "libx264", "-preset", "medium", "-crf", crf.ToString(), "-pix_fmt", "yuv420p", "-movflags", "+faststart", "-n", temporary }) info.ArgumentList.Add(arg);
        // TIFF does not support concatenated image2pipe packets. Transcode one
        // frame at a time to PNG in memory, never creating staging image files.
        var isTiff = Path.GetExtension(files[0]).ToLowerInvariant() is ".tif" or ".tiff";
        var inputIndex = info.ArgumentList.IndexOf("-i");
        info.ArgumentList.Insert(inputIndex, "-c:v");
        info.ArgumentList.Insert(inputIndex + 1, isTiff || Path.GetExtension(files[0]).Equals(".png", StringComparison.OrdinalIgnoreCase) ? "png" : "mjpeg");
        using var process = new Process { StartInfo = info };
        try
        {
            process.Start();
            using var registration = token.Register(() => { try { process.Kill(true); } catch { } });
            var errors = process.StandardError.ReadToEndAsync();
            var updates = Task.Run(async () => { while (await process.StandardOutput.ReadLineAsync() is { } line) if (line.StartsWith("frame=") && int.TryParse(line[6..], out var frame)) progress.Report(frame); });
            Exception? inputError = null;
            try
            {
                foreach (var path in files)
                {
                    token.ThrowIfCancellationRequested(); await using var source = File.OpenRead(path);
                    if (isTiff)
                    {
                        var decoder = new TiffBitmapDecoder(source, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
                        var encoder = new PngBitmapEncoder(); encoder.Frames.Add(decoder.Frames[0]);
                        using var png = new MemoryStream(); encoder.Save(png); png.Position = 0;
                        await png.CopyToAsync(process.StandardInput.BaseStream, token);
                    }
                    else await source.CopyToAsync(process.StandardInput.BaseStream, token);
                }
            }
            catch (Exception ex) { inputError = ex; try { process.Kill(true); } catch { } }
            finally { process.StandardInput.Close(); }
            await process.WaitForExitAsync(); await updates;
            var error = await errors; token.ThrowIfCancellationRequested();
            if (process.ExitCode != 0 || inputError != null) throw new IOException(string.IsNullOrWhiteSpace(error) ? inputError?.Message ?? "FFmpeg failed." : error.Length > 1600 ? error[^1600..] : error);
            if (!File.Exists(temporary) || new FileInfo(temporary).Length == 0) throw new IOException("FFmpeg produced no video.");
            File.Move(temporary, destination, overwrite);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
}
