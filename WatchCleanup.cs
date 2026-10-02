using System.IO;

namespace BlenderRenderHeadless;

public static class WatchCleanup
{
    // Move out of the watched directory rather than permanently deleting shared data.
    public static string ArchiveContents(string folder)
    {
        var root = Validate(folder);
        var entries = Directory.GetFileSystemEntries(root);
        foreach (var entry in entries) CheckTree(entry);
        if (Directory.EnumerateFiles(root, "*.claim", SearchOption.AllDirectories).Any())
            throw new IOException("The folder contains worker claims. Stop rendering on every machine before clearing it.");
        if (Directory.EnumerateFiles(root, "*.lock", SearchOption.AllDirectories).Any(path => IsLocked(path)))
            throw new IOException("A worker/export still holds a lock. Wait until it has stopped.");
        var archive = Path.Combine(Path.GetDirectoryName(root)!, "CloudRender-cleared-" + DateTime.UtcNow.ToString("yyyyMMdd-HHmmss") + "-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(archive);
        foreach (var entry in entries)
        {
            CheckTree(entry);
            var destination = Path.Combine(archive, Path.GetFileName(entry));
            if (Directory.Exists(entry)) Directory.Move(entry, destination); else File.Move(entry, destination);
        }
        return archive;
    }
    public static string Validate(string folder)
    {
        if (!Path.IsPathFullyQualified(folder)) throw new IOException("Watch folder must be an absolute path.");
        var root = Path.GetFullPath(folder).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        if (!Path.GetFileName(root).Equals("CloudRender", StringComparison.OrdinalIgnoreCase))
            throw new IOException("For safety, this action only clears a folder named CloudRender.");
        if (!Directory.Exists(root)) throw new IOException("Watch folder is unavailable.");
        for (var parent = root; !string.IsNullOrEmpty(parent); parent = Path.GetDirectoryName(parent))
            if ((File.GetAttributes(parent) & FileAttributes.ReparsePoint) != 0) throw new IOException("Refusing to clear a linked folder.");
        return root;
    }
    private static void CheckTree(string path)
    {
        if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0) throw new IOException("Refusing to follow a linked file/folder: " + path);
        if (Directory.Exists(path)) foreach (var child in Directory.EnumerateFileSystemEntries(path)) CheckTree(child);
    }
    private static bool IsLocked(string path)
    {
        try { using var stream = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None); return false; }
        catch (IOException) { return true; }
    }
}
