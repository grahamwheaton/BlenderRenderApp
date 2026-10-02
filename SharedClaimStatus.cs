using System.IO;
using System.Text.Json;

namespace BlenderRenderHeadless;

public static class SharedClaimStatus
{
    public static string Read(RenderJob job)
    {
        if (!job.Distributed || job.TileSize != 0 || job.StartFrame != job.EndFrame || job.Status is not ("Waiting" or "Rendering")) return "";
        var safe = string.Concat(job.JobId.Where(c => char.IsLetterOrDigit(c) || c is '-' or '_' or '.'));
        if (safe.Length == 0 || safe is "." or ".." || !int.TryParse(job.StartFrame, out var frame)) return "";
        var path = Path.Combine(job.CoordinationFolder, "_claims", safe, frame + ".claim");
        try
        {
            if (!File.Exists(path) || DateTime.UtcNow - File.GetLastWriteTimeUtc(path) > TimeSpan.FromSeconds(120)) return "";
            using var data = JsonDocument.Parse(File.ReadAllText(path));
            return data.RootElement.GetProperty("machine").GetString() ?? "";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or KeyNotFoundException or InvalidOperationException) { return ""; }
    }
}
