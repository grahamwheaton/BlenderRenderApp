using System.IO;
using System.Reflection;
using System.Security.Cryptography;

namespace BlenderRenderHeadless;

public static class BundledFfmpeg
{
    private const string ExpectedHash = "05F4251BCE9293C2AB492CB17CA7724A0FFD0D06C881BA2EE83B82A89C2FC740";
    private static readonly object Gate = new();
    private static string? cached;

    // FFmpeg runs as a separate process, not as a linked library. No PATH or admin access needed.
    public static string Resolve()
    {
        lock (Gate)
        {
            if (cached != null && File.Exists(cached)) return cached;
            var directory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "BlenderRenderLauncher", "tools", "ffmpeg-9.0-" + ExpectedHash[..12]);
            Directory.CreateDirectory(directory);
            var path = Path.Combine(directory, "ffmpeg.exe");
            if (!Matches(path))
            {
                var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
                try
                {
                    using (var source = Resource("ffmpeg.exe"))
                    using (var target = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None)) source.CopyTo(target);
                    if (!Matches(temporary)) throw new IOException("Bundled FFmpeg failed its integrity check.");
                    try { File.Move(temporary, path, true); }
                    catch (IOException) when (Matches(path)) { /* Another launcher extracted the same verified build. */ }
                }
                finally { if (File.Exists(temporary)) File.Delete(temporary); }
            }
            foreach (var name in new[] { "LICENSE", "README.txt" })
            {
                using var source = Resource(name);
                using var target = new FileStream(Path.Combine(directory, name), FileMode.Create, FileAccess.Write, FileShare.Read);
                source.CopyTo(target);
            }
            return cached = path;
        }
    }
    private static Stream Resource(string name) => Assembly.GetExecutingAssembly().GetManifestResourceStream("BlenderRenderHeadless.FFmpeg." + name)
        ?? throw new IOException("This build is missing bundled FFmpeg: " + name);
    private static bool Matches(string path)
    {
        if (!File.Exists(path)) return false;
        using var stream = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(stream)) == ExpectedHash;
    }
}
