using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.VisualBasic.FileIO;

namespace BlenderRenderHeadless;

public sealed record RecordedFrame(int Frame, string Machine, string Output, string Receipt);

public static class SequenceRepair
{
    public static string Claims(RenderJob job)
    {
        if (!job.Distributed || job.TileSize != 0 || !Guid.TryParse(job.JobId, out _))
            throw new IOException("Repair requires a shared, non-tiled sequence job.");
        return Path.Combine(job.CoordinationFolder, "_claims", job.JobId);
    }
    public static string OutputDirectory(RenderJob job)
    {
        var path = job.OutputPath;
        if (path.StartsWith("//")) path = Path.Combine(Path.GetDirectoryName(job.BlendFile)!, path[2..]);
        if (!Path.IsPathFullyQualified(path)) throw new IOException("Output folder is not an absolute path; repair cannot safely resolve it.");
        var directory = Path.GetFullPath(Path.EndsInDirectorySeparator(path) ? path : Path.GetDirectoryName(path)!);
        if (directory.TrimEnd('\\', '/') == Path.GetPathRoot(directory)?.TrimEnd('\\', '/')) throw new IOException("Repair is not allowed at a drive or share root.");
        CheckLinks(directory);
        return directory.TrimEnd('\\', '/');
    }
    private static void CheckLinks(string path)
    {
        for (var p = path; !string.IsNullOrEmpty(p); p = Path.GetDirectoryName(p))
            if ((Directory.Exists(p) || File.Exists(p)) && (File.GetAttributes(p) & FileAttributes.ReparsePoint) != 0)
                throw new IOException("Repair does not follow linked files or folders: " + p);
    }
    private static string Validate(RenderJob job, string path)
    {
        if (!Path.IsPathFullyQualified(path)) throw new IOException("Invalid recorded image path.");
        path = Path.GetFullPath(path);
        if (!string.Equals(Path.GetDirectoryName(path)?.TrimEnd('\\', '/'), OutputDirectory(job), StringComparison.OrdinalIgnoreCase))
            throw new IOException("Recorded output is outside this job's output folder; no files were selected: " + path);
        if (!new[] { ".png", ".jpg", ".jpeg", ".tif", ".tiff", ".exr", ".bmp" }.Contains(Path.GetExtension(path).ToLowerInvariant()))
            throw new IOException("Recorded output is not a supported image.");
        CheckLinks(path);
        return path;
    }
    public static List<RecordedFrame> Read(RenderJob job)
    {
        var root = Claims(job); var directory = OutputDirectory(job);
        var records = new Dictionary<int, RecordedFrame>();
        var start = int.Parse(job.StartFrame); var end = int.Parse(job.EndFrame); var step = int.Parse(job.FrameStep);
        if (step < 1 || end < start) throw new IOException("Invalid frame range.");
        if (!Directory.Exists(directory)) throw new IOException("Output folder is unavailable: " + directory);
        // Keep old reports readable; prefer the new shared-folder reports when both exist.
        var reportFolders = new[] { directory, job.CoordinationFolder }.Distinct(StringComparer.OrdinalIgnoreCase).Where(Directory.Exists);
        foreach (var report in reportFolders.SelectMany(p => Directory.EnumerateFiles(p, "render-report-*-" + job.JobId[..8] + ".csv")))
        {
            using var parser = new TextFieldParser(report) { HasFieldsEnclosedInQuotes = true };
            parser.SetDelimiters(",");
            var header = parser.ReadFields() ?? [];
            int f = Array.IndexOf(header, "Frame"), m = Array.IndexOf(header, "Machine"), o = Array.IndexOf(header, "Output file");
            if (Math.Min(f, Math.Min(m, o)) < 0) continue;
            while (!parser.EndOfData)
            {
                var row = parser.ReadFields()!;
                if (row.Length <= Math.Max(f, Math.Max(m, o)) || !int.TryParse(row[f], out var frame) || frame < start || frame > end || (frame - start) % step != 0) continue;
                var output = Validate(job, row[o]);
                records[frame] = new(frame, row[m], output, JsonSerializer.Serialize(new { output, machine = row[m], author = "From accountability CSV" }));
            }
        }
        for (long frame = start; frame <= end; frame += step)
        {
            var path = Path.Combine(root, frame + ".done");
            if (File.Exists(path))
            {
                var json = File.ReadAllText(path); var data = JsonNode.Parse(json)!;
                records[(int)frame] = new((int)frame, data["machine"]?.GetValue<string>() ?? "Unknown", Validate(job, data["output"]!.GetValue<string>()), json);
            }
            if (!records.ContainsKey((int)frame)) throw new IOException($"No recorded path for frame {frame}. Keep the job's receipts or CSV; filenames will not be guessed.");
        }
        if (records.Values.Select(r => r.Output).Distinct(StringComparer.OrdinalIgnoreCase).Count() != records.Count)
            throw new IOException("Multiple frames reference the same image; refusing repair.");
        return records.Values.OrderBy(r => r.Frame).ToList();
    }
    public static bool Valid(RecordedFrame frame) => File.Exists(frame.Output) && new FileInfo(frame.Output).Length > 0;
    public static IDisposable Lock(RenderJob job)
    {
        var root = Claims(job); var handles = new List<FileStream>();
        try
        {
            foreach (var name in new[] { "repair.lock", "export-PSD.lock", "export-MP4.lock" })
                handles.Add(new FileStream(Path.Combine(root, name), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None));
            if (Directory.EnumerateFiles(root, "*.claim").Any()) throw new IOException("Workers still own frames. Wait until they have stopped.");
            if (File.Exists(Path.Combine(root, "repair-published.json"))) throw new IOException("A repair job was already issued. Use that job after it completes.");
            return new HeldLocks(handles);
        }
        catch { foreach (var handle in handles) handle.Dispose(); throw; }
    }
    private sealed class HeldLocks(List<FileStream> handles) : IDisposable
    { public void Dispose() { foreach (var handle in handles) handle.Dispose(); } }
    private static void Retire(RenderJob job) => AtomicWrite(Path.Combine(Claims(job), "cancel.json"), JsonSerializer.Serialize(new { reason = "Sequence maintenance; use replacement repair job", machine = Environment.MachineName }));
    public static int Quarantine(RenderJob job, IEnumerable<RecordedFrame> frames)
    {
        var selected = frames.ToList();
        foreach (var frame in selected) Validate(job, frame.Output);
        var backup = Path.Combine(OutputDirectory(job), "_partial-delete", job.JobId, Guid.NewGuid().ToString("N"));
        CheckLinks(backup);
        Directory.CreateDirectory(backup);
        Retire(job);
        int moved = 0;
        foreach (var frame in selected)
        {
            Validate(job, frame.Output);
            if (!File.Exists(frame.Output)) continue;
            File.Move(frame.Output, Path.Combine(backup, Path.GetFileName(frame.Output)));
            moved++;
        }
        return moved;
    }
    public static string Publish(RenderJob job, List<RecordedFrame> records)
    {
        JsonObject? manifest = null;
        foreach (var path in Directory.EnumerateFiles(job.CoordinationFolder, "*.renderjob.json"))
        {
            var candidate = JsonNode.Parse(File.ReadAllText(path)) as JsonObject;
            if (candidate?["jobId"]?.GetValue<string>() == job.JobId) { manifest = candidate; break; }
        }
        if (manifest == null) throw new IOException("Original shared job manifest is missing. Restore it before reissuing this job.");
        if (!File.Exists(job.BlendFile)) throw new IOException("Original Blender file is unavailable.");
        foreach (var record in records) Validate(job, record.Output);
        var id = Guid.NewGuid().ToString();
        var root = Path.Combine(job.CoordinationFolder, "_claims", id);
        Directory.CreateDirectory(root);
        foreach (var record in records.Where(Valid)) AtomicWrite(Path.Combine(root, record.Frame + ".done"), record.Receipt);
        manifest["jobId"] = id; manifest["overwrite"] = false; manifest["placeholders"] = false;
        manifest["preRendered"] = false; manifest["distributed"] = true;
        Retire(job);
        var pathOut = Path.Combine(job.CoordinationFolder, DateTime.UtcNow.ToString("yyyyMMdd_HHmmss") + "_" + id + ".renderjob.json");
        AtomicWrite(pathOut, manifest.ToJsonString());
        AtomicWrite(Path.Combine(Claims(job), "repair-published.json"), JsonSerializer.Serialize(new { jobId = id, path = pathOut }));
        return pathOut;
    }
    private static void AtomicWrite(string path, string data)
    {
        var temp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        File.WriteAllText(temp, data); File.Move(temp, path, true);
    }
}
