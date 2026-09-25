using System.Globalization;
using System.IO;
using System.Text.Json;

namespace BlenderRenderHeadless;

public static class AutomaticExports
{
    // FileShare.None is an OS/SMB lock, released even if the launcher crashes.
    // Keep the lock file: unlinking it would introduce a competing-open race.
    public static async Task<string> Run(RenderJob job, string kind, IProgress<string> log)
    {
        var identity = string.IsNullOrWhiteSpace(job.JobId) ? job.LocalExportId : job.JobId;
        var safeId = string.Concat(identity.Where(c => char.IsLetterOrDigit(c) || c is '-' or '_' or '.'));
        var root = job.Distributed ? job.CoordinationFolder : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "BlenderRenderLauncher", "exports");
        var directory = Path.Combine(root, "_claims", safeId);
        Directory.CreateDirectory(directory);
        FileStream claim;
        try { claim = new FileStream(Path.Combine(directory, "export-" + kind + ".lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None); }
        catch (IOException) { return "Another machine is converting " + kind; }
        using (claim)
        {
            var receipt = Path.Combine(directory, "export-" + kind + ".done.json");
            if (File.Exists(receipt)) return kind + " already processed";
            List<string> outputs;
            if (kind == "PSD")
            {
                var exe = PsdConverter.FindExecutable() ?? throw new IOException("BlenderBatchEXR-CLI.exe not found. Use Make PSD manually to choose a converter.");
                var files = PsdConverter.ResolveFiles(job) ?? throw new IOException("No recorded EXR paths. Use Make PSD manually to select files.");
                outputs = new();
                foreach (var file in files)
                {
                    log.Report("PSD: " + file);
                    if (await PsdConverter.ConvertFile(exe, file, log) != 0) throw new IOException("PSD conversion failed. Check the render log; RLAYER4 requires unique Diff and Image passes.");
                    var output = new[] { Path.ChangeExtension(file, ".psd"), Path.ChangeExtension(file, ".psb") }.FirstOrDefault(p => File.Exists(p) && new FileInfo(p).Length > 0);
                    if (output == null) throw new IOException("Converter produced no PSD/PSB for " + file);
                    outputs.Add(output);
                }
            }
            else
            {
                var exe = Mp4Encoder.FindFfmpeg() ?? throw new IOException("FFmpeg not found. Install it or place ffmpeg.exe beside the launcher.");
                var files = Mp4Encoder.ResolveFiles(job, CancellationToken.None) ?? throw new IOException("No recorded sequence paths. Use Make MP4 manually to select the sequence.");
                var safeCamera = string.Concat(job.CameraName.Select(c => Path.GetInvalidFileNameChars().Contains(c) ? '_' : c));
                var output = Path.Combine(Path.GetDirectoryName(files[0])!, $"{safeCamera}_{job.StartFrame}-{job.EndFrame}.mp4");
                if (File.Exists(output)) throw new IOException("MP4 already exists; kept unchanged: " + output);
                var fps = double.Parse(job.FrameRate, CultureInfo.InvariantCulture) / Math.Max(1, int.Parse(job.FrameStep));
                await Mp4Encoder.Encode(exe, files, output, fps, 18, new Progress<int>(frame => log.Report($"MP4: {frame}/{files.Count}")), CancellationToken.None, overwrite: false);
                outputs = new() { output };
            }
            var temporary = receipt + "." + Guid.NewGuid().ToString("N") + ".tmp";
            await File.WriteAllTextAsync(temporary, JsonSerializer.Serialize(new { outputs, machine = Environment.MachineName, completed = DateTimeOffset.UtcNow }));
            File.Move(temporary, receipt, true);
            return kind + " ready: " + string.Join(", ", outputs);
        }
    }
}
