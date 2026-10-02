using System.IO;
using System.Text.Json.Nodes;

namespace BlenderRenderHeadless;

public static class CloudRestart
{
    public static string Publish(RenderJob job)
    {
        if (string.IsNullOrWhiteSpace(job.CloudManifestPath)) throw new IOException("This is not a cloud submission.");
        if (!File.Exists(job.BlendFile)) throw new IOException("The Blender file is unavailable.");
        var folder = Path.GetDirectoryName(Path.GetFullPath(job.CloudManifestPath))!;
        var original = JsonNode.Parse(File.ReadAllText(job.CloudManifestPath)) as JsonObject
            ?? throw new IOException("Invalid original cloud manifest.");
        JsonObject? entry = original["jobs"] is JsonArray batch
            ? batch.OfType<JsonObject>().FirstOrDefault(j => j["jobId"]?.GetValue<string>() == job.JobId)
            : original["jobId"]?.GetValue<string>() == job.JobId ? original : null;
        if (entry == null) throw new IOException("The original job was not found in its cloud manifest.");
        if (!Guid.TryParse(job.JobId, out _)) throw new IOException("Invalid original job ID.");
        var claims = Path.Combine(folder, "_claims", job.JobId);
        if (Directory.Exists(claims) && Directory.EnumerateFiles(claims, "*.claim").Any())
            throw new IOException("The original job still has worker claims. Wait for all workers to stop before restarting.");
        var replacement = (JsonObject)entry.DeepClone();
        var id = Guid.NewGuid().ToString();
        replacement["jobId"] = id;
        replacement.Remove("batchId"); // Restart this row only, never revive cancelled sibling cameras.
        replacement["preRendered"] = false;
        replacement["distributed"] = true;
        replacement["restartedFrom"] = job.JobId;
        replacement["restartedBy"] = Environment.MachineName;
        replacement["sentAt"] = DateTimeOffset.Now.ToString("O");
        // Keep original sender identity so Only my PC retains its established meaning.
        var suffix = job.TileSize > 0 ? ".tilejob.json" : ".renderjob.json";
        var path = Path.Combine(folder, DateTime.UtcNow.ToString("yyyyMMdd_HHmmss") + "_" + id + suffix);
        var temp = path + ".tmp";
        try { File.WriteAllText(temp, replacement.ToJsonString()); File.Move(temp, path); }
        finally { if (File.Exists(temp)) File.Delete(temp); }
        return path;
    }
}
