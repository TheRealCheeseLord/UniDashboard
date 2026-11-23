namespace MoodleDashboard.Core;

/// <summary>Tiny rolling file logger: one file per day, old ones deleted after 7 days, each capped at ~512 KB.</summary>
public static class FileLog
{
    static readonly object Gate = new();

    public static void Write(string source, string message)
    {
        try
        {
            lock (Gate)
            {
                var file = Path.Combine(Paths.LogDir, $"{source}-{DateTime.Now:yyyyMMdd}.log");
                if (File.Exists(file) && new FileInfo(file).Length > 512 * 1024) File.Delete(file);
                File.AppendAllText(file, $"{DateTime.Now:HH:mm:ss} {message}{Environment.NewLine}");
                foreach (var old in Directory.GetFiles(Paths.LogDir, "*.log").Where(f => File.GetLastWriteTime(f) < DateTime.Now.AddDays(-7)))
                    File.Delete(old);
            }
        }
        catch { /* logging must never break a sync */ }
    }
}
