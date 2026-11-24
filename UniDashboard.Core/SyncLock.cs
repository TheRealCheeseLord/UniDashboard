namespace UniDashboard.Core;

/// <summary>Cross-process, await-safe named lock (exclusive file handle, released automatically if the process dies).</summary>
public static class SyncLock
{
    public static async Task<FileStream> AcquireAsync(string name, TimeSpan timeout, CancellationToken ct = default)
    {
        var path = Path.Combine(Paths.Root, name + ".lock");
        var deadline = DateTime.UtcNow + timeout;
        while (true)
        {
            try { return new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None, 1, FileOptions.DeleteOnClose); }
            catch (IOException) when (DateTime.UtcNow < deadline) { await Task.Delay(300, ct); }
            catch (IOException) { throw new TimeoutException($"Another {name} is already running."); }
        }
    }
}
