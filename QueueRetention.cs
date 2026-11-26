using System.IO;

namespace BlenderRenderHeadless;

public static class QueueRetention
{
    public static bool Expired(DateTime submittedUtc, DateTime nowUtc) => submittedUtc < nowUtc.AddDays(-2);
    public static bool ExpiredFile(string path)
    {
        try { return Expired(File.GetLastWriteTimeUtc(path), DateTime.UtcNow); }
        catch (IOException) { return false; }
        catch (UnauthorizedAccessException) { return false; }
    }
}

public partial class MainWindow
{
    private DateTime _nextRetentionCheck;
    private void PruneExpiredQueueJobs()
    {
        if (DateTime.UtcNow < _nextRetentionCheck) return;
        _nextRetentionCheck = DateTime.UtcNow.AddMinutes(1);
        // Do not disrupt local workers/batches or conversions. Watch manifests
        // are filtered before admission separately, even while a queue is running.
        if (_queueRunning || _exportSerial.CurrentCount == 0) return;
        var expired = _queue.Where(j => QueueRetention.Expired(j.SubmittedUtc, DateTime.UtcNow)
            && j.Status != "Rendering" && !j.IsOpening && !j.IsBatchReserved
            && string.IsNullOrEmpty(j.ClaimedBy) && string.IsNullOrEmpty(j.PreloadLabel)).ToArray();
        foreach (var job in expired) _queue.Remove(job);
        if (expired.Length > 0) { UpdateQueueState(); AppendLog($"Cleared {expired.Length} queue entries older than 48 hours (NAS files retained)."); }
    }
}
