using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.Json;

namespace BlenderRenderHeadless;

public partial class MainWindow
{
    private async Task RunCameraBatchWithRetries(List<RenderJob> batch)
    {
        for (var attempt = 0; batch.Count > 0; attempt++)
        {
            foreach (var job in batch) { job.FailureDetail = ""; job.WorkerStatus = "Camera batch — one scene load per worker"; }
            await RunCameraBatch(batch);
            var failed = batch.Where(j => AutomaticRetryPolicy.Eligible(j, AutoRetryCheckBox.IsChecked == true, _cancelRequested, attempt)).ToList();
            if (failed.Count == 0 || !await WaitForAutomaticRetry(failed[0], attempt)) break;
            foreach (var job in failed) job.Status = "Waiting";
            batch = failed;
        }
    }

    private async Task RunCameraBatch(List<RenderJob> jobs)
    {
        var primary = jobs[0];
        var logs = new Dictionary<string, StreamWriter>();
        string? packetPath = null;
        Process? process = null;
        void Record(RenderJob job, string text)
        {
            try { if (logs.TryGetValue(job.JobId, out var log)) log.WriteLine($"{DateTimeOffset.Now:O} {text}"); }
            catch (IOException ex) { job.FailureDetail = "Log write failed: " + ex.Message; }
        }
        try
        {
            var script = ExtractScript("camera_batch.py");
            ExtractScript("render_scene.py");
            var packets = jobs.Select(j => new { jobId = j.JobId, args = new[] {
                j.CameraName, j.StartFrame, j.EndFrame, j.FrameStep, j.OutputPath, j.Engine, j.Width, j.Height, j.Scale,
                j.FrameRate, j.Format, "FINAL", j.Overwrite || ForceOverwriteCheckBox.IsChecked == true ? "1" : "0", "0",
                j.IgnoreCompositor ? "1" : "0", j.TransparentBackground ? "1" : "0", j.ViewportShading, "1", j.JobId,
                j.CoordinationFolder, j.UsesCompositorOutput ? "1" : "0", j.CompositorOutputNode } }).ToList();
            if (jobs.Any(j => !j.Distributed || j.TileSize != 0 || j.RequiresViewport || j.StartFrame != j.EndFrame || !Guid.TryParse(j.JobId, out _)))
                throw new IOException("Camera batches require shared non-tiled final-render stills with valid job IDs.");
            packetPath = Path.Combine(Path.GetTempPath(), "BRH-camera-batch-" + Guid.NewGuid().ToString("N") + ".json");
            File.WriteAllText(packetPath, JsonSerializer.Serialize(packets), new UTF8Encoding(false));
            var logFolder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "BlenderRenderLauncher", "logs");
            Directory.CreateDirectory(logFolder);
            foreach (var job in jobs)
            {
                job.LogPath = Path.Combine(logFolder, DateTime.UtcNow.ToString("yyyyMMdd-HHmmss") + "-" + Guid.NewGuid().ToString("N") + ".log");
                logs[job.JobId] = new StreamWriter(job.LogPath) { AutoFlush = true };
                Record(job, $"Camera batch {job.BatchId}; Machine {Environment.MachineName}; Blender {_blenderExe}; File {job.BlendFile}");
            }
            var byId = jobs.ToDictionary(j => j.JobId);
            process = PrepareWorker(_blenderExe!, new[] { "--background", primary.BlendFile, "--python", script, "--", packetPath });
            _renderProcess = process;
            void Handle(string? line)
            {
                if (string.IsNullOrEmpty(line)) return;
                Dispatcher.Invoke(() =>
                {
                    const string statusPrefix = "BRH_BATCH_STATUS:", logPrefix = "BRH_BATCH_LOG:";
                    if (line == "BRH_SCENE_READY") { foreach (var item in jobs) item.IsOpening = false; RefreshActiveRender(); }
                    if (line.StartsWith(statusPrefix) || line.StartsWith(logPrefix))
                    {
                        var statusLine = line.StartsWith(statusPrefix);
                        var parts = line[(statusLine ? statusPrefix.Length : logPrefix.Length)..].Split('|', 2);
                        if (parts.Length != 2 || !byId.TryGetValue(parts[0], out var job)) return;
                        Record(job, parts[1]);
                        if (statusLine)
                        {
                            if (parts[1] == "Rendering") { if (job.Status != "Rendering") job.Begin(); }
                            else if (parts[1] is "Waiting" or "Complete" or "Cancelled" or "Failed") job.Status = job.IsGloballyCancelled ? "Cancelled" : parts[1];
                            if (job.Status == "Complete") job.Finish();
                            if (job.Status == "Failed") job.FailureDetail = "Camera render failed — see View log";
                        }
                        else
                        {
                            WorkerReady(job, parts[1]);
                            var match = FramePattern.Match(parts[1]);
                            if (match.Success && int.TryParse(match.Groups[1].Value, out var frame)) job.ReportFrame(frame, match.Groups[2].Value);
                            AppendLog($"[{job.CameraName}] {parts[1]}");
                        }
                    }
                    else { foreach (var job in jobs) Record(job, line); AppendLog(line); }
                });
            }
            process.OutputDataReceived += (_, e) => Handle(e.Data);
            process.ErrorDataReceived += (_, e) => Handle(e.Data);
            foreach (var job in jobs) { job.IsBatchReserved = true; job.IsOpening = true; }
            RefreshActiveRender();
            StartPreparedWorker(process);
            await process.WaitForExitAsync();
            foreach (var job in jobs)
            {
                Record(job, "Batch process exit: " + process.ExitCode);
                if (job.Status is "Waiting" or "Rendering")
                {
                    job.Status = _cancelRequested || job.HasGlobalCancellationMarker() ? "Cancelled" : process.ExitCode == 75 ? "Waiting" : "Failed";
                    job.FailureDetail = process.ExitCode == 75 ? "" : $"Batch worker exited before this camera finished (exit {process.ExitCode}).";
                }
            }
        }
        catch (Exception ex)
        {
            foreach (var job in jobs.Where(j => j.Status is "Waiting" or "Rendering")) { job.Status = _cancelRequested ? "Cancelled" : "Failed"; job.FailureDetail = ex.Message; Record(job, ex.ToString()); }
            AppendLog(ex.ToString());
        }
        finally
        {
            foreach (var job in jobs) { job.IsOpening = false; job.IsBatchReserved = false; }
            if (process != null) { try { if (!process.HasExited) { process.Kill(true); await process.WaitForExitAsync(); } } catch { } process.Dispose(); }
            _renderProcess = null;
            foreach (var log in logs.Values) log.Dispose();
            if (packetPath != null) try { File.Delete(packetPath); } catch { }
        }
    }
}
