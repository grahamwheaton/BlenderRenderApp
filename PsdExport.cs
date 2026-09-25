using Microsoft.Win32;
using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace BlenderRenderHeadless;

public sealed class PsdExportWindow : Window
{
    private static string? _converter;
    private readonly RenderJob _job;
    private readonly TextBox _log = new() { IsReadOnly = true, TextWrapping = TextWrapping.Wrap, Height = 190, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Margin = new Thickness(0, 14, 0, 14) };
    private readonly Button _start = new() { Content = "Make PSD" };
    private readonly Button _close = new() { Content = "Close", Margin = new Thickness(10, 0, 0, 0) };
    private bool _running, _stop;

    public PsdExportWindow(RenderJob job)
    {
        _job = job; Title = "Make PSD — RLAYER4"; Width = 580; SizeToContent = SizeToContent.Height;
        WindowStartupLocation = WindowStartupLocation.CenterOwner; ResizeMode = ResizeMode.NoResize;
        Background = new SolidColorBrush(Color.FromRgb(27, 33, 40));
        var panel = new StackPanel { Margin = new Thickness(22) }; Content = panel;
        panel.Children.Add(new TextBlock { Text = job.CameraName + " — Make PSD", FontSize = 18, Margin = new Thickness(0, 0, 0, 14) });
        panel.Children.Add(new TextBlock { Text = "RLAYER4 correction • 8-bit sRGB • COMP/RLAYERS groups\nRequires Image and Diff passes. Saves beside each EXR.\nLarge documents use PSB automatically. Existing outputs are kept.", TextWrapping = TextWrapping.Wrap });
        panel.Children.Add(_log);
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        buttons.Children.Add(_start); buttons.Children.Add(_close); panel.Children.Add(buttons);
        _start.Click += Convert;
        _close.Click += (_, _) => { if (_running) { _stop = true; _close.IsEnabled = false; Log("Stopping after the current file finishes…"); } else Close(); };
        Closing += (_, e) => { if (_running) { e.Cancel = true; _stop = true; Log("Waiting for the current conversion to finish safely…"); } };
    }

    private void Log(string text) { _log.AppendText(text + Environment.NewLine); if (_log.Text.Length > 24000) _log.Text = _log.Text[^16000..]; _log.ScrollToEnd(); }

    private async void Convert(object sender, RoutedEventArgs e)
    {
        _running = true; _stop = false; _start.IsEnabled = false; _close.Content = "Stop after this file";
        try
        {
            _converter ??= PsdConverter.FindExecutable();
            if (_converter == null || !File.Exists(_converter))
            {
                var picker = new OpenFileDialog { Title = "Locate BlenderBatchEXR-CLI.exe", Filter = "Blender Batch EXR CLI|BlenderBatchEXR-CLI.exe" };
                if (picker.ShowDialog(this) != true) return;
                _converter = picker.FileName;
            }
            Log("Finding completed EXR outputs…");
            var files = await Task.Run(() => PsdConverter.ResolveFiles(_job));
            if (files == null)
            {
                var picker = new OpenFileDialog { Title = "Select this job's completed EXR output(s)", Filter = "EXR images|*.exr", Multiselect = true };
                if (picker.ShowDialog(this) != true) return;
                files = picker.FileNames.ToList();
            }
            PsdConverter.Validate(files);
            if (_stop) return;
            Log($"{files.Count} EXR file(s). Output: beside each original.");
            var failed = 0; var processed = 0;
            foreach (var file in files)
            {
                if (_stop) break;
                Log($"[{processed + 1}/{files.Count}] {file}");
                var code = await PsdConverter.ConvertFile(_converter, file, new Progress<string>(Log));
                processed++;
                if (code != 0) { failed++; Log($"Conversion failed (exit {code}). See converter details above."); }
            }
            Log($"{(_stop ? "Stopped" : "Finished")}: {processed}/{files.Count} processed; {failed} failed. Existing PSD/PSB files were left unchanged.");
        }
        catch (Exception ex) { Log("Conversion failed: " + ex.Message); }
        finally { _running = false; _start.IsEnabled = true; _close.IsEnabled = true; _close.Content = "Close"; }
    }
}

public static class PsdConverter
{
    public static string? FindExecutable() => new[] {
        Path.Combine(AppContext.BaseDirectory, "BlenderBatchEXR-CLI.exe"),
        @"W:\Working Graphics\_Plugins\Blender\Blender Batch EXRS\BlenderBatchEXR-CLI.exe",
        @"\\LC-DC01\Company\Working Graphics\_Plugins\Blender\Blender Batch EXRS\BlenderBatchEXR-CLI.exe"
    }.FirstOrDefault(File.Exists);

    public static List<string>? ResolveFiles(RenderJob job)
    {
        var safeId = string.Concat(job.JobId.Where(c => char.IsLetterOrDigit(c) || c is '-' or '_' or '.'));
        var folder = Path.Combine(job.CoordinationFolder, "_claims", safeId);
        if (job.TileSize > 0)
        {
            var complete = Path.Combine(folder, "complete.json");
            if (!File.Exists(complete)) return null;
            using var data = JsonDocument.Parse(File.ReadAllText(complete));
            var path = data.RootElement.GetProperty("output").GetString();
            var result = new List<string> { path ?? "" }; Validate(result); return result;
        }
        var files = new List<string>();
        var start = int.Parse(job.StartFrame); var end = int.Parse(job.EndFrame); var step = int.Parse(job.FrameStep);
        if (step <= 0 || end < start) throw new InvalidOperationException("Invalid frame range.");
        for (long frame = start; frame <= end; frame += step)
        {
            job.RenderedFiles.TryGetValue((int)frame, out var path);
            if (string.IsNullOrWhiteSpace(path) && job.Distributed && safeId.Length > 0)
            {
                var marker = Path.Combine(folder, frame + ".done");
                if (File.Exists(marker)) { using var data = JsonDocument.Parse(File.ReadAllText(marker)); path = data.RootElement.GetProperty("output").GetString(); }
            }
            if (string.IsNullOrWhiteSpace(path)) return null;
            files.Add(path);
        }
        files = files.Distinct(StringComparer.OrdinalIgnoreCase).ToList(); Validate(files); return files;
    }

    public static void Validate(IReadOnlyList<string> files)
    {
        if (files.Count == 0) throw new IOException("No EXRs found for this job.");
        foreach (var path in files)
            if (!Path.GetExtension(path).Equals(".exr", StringComparison.OrdinalIgnoreCase) || !File.Exists(path) || new FileInfo(path).Length == 0)
                throw new IOException("Missing, empty or non-EXR output: " + path);
    }

    public static async Task<int> ConvertFile(string executable, string file, IProgress<string> progress)
    {
        Validate(new[] { file });
        var info = new ProcessStartInfo(executable) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true, StandardOutputEncoding = System.Text.Encoding.UTF8, StandardErrorEncoding = System.Text.Encoding.UTF8 };
        info.Environment["PYTHONIOENCODING"] = "utf-8";
        info.Environment["PYTHONUTF8"] = "1";
        // Omit -o: the CLI saves beside each source. Never use a folder input,
        // which could convert unrelated jobs or intermediate render tiles.
        foreach (var arg in new[] { file, "--headless", "--workflow", "rlayer4", "--format", "auto", "--skip-existing" }) info.ArgumentList.Add(arg);
        using var process = new Process { StartInfo = info };
        process.Start();
        async Task Drain(StreamReader reader) { while (await reader.ReadLineAsync() is { } line) progress.Report(line); }
        await Task.WhenAll(Drain(process.StandardOutput), Drain(process.StandardError), process.WaitForExitAsync());
        return process.ExitCode;
    }
}
