namespace UniDashboard.Core;

public static class Paths
{
    public static string Root { get; } = Init();

    static string Init()
    {
        // Lets demos, screenshots and tests run against a throwaway data folder instead of the real one.
        if (Environment.GetEnvironmentVariable("UNIDASHBOARD_DATA_DIR") is { Length: > 0 } custom) return EnsureDir(custom);
        var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var dir = Path.Combine(local, "UniDashboard");
        var legacy = Path.Combine(local, "MoodleLite");   // app was renamed; carry over data, token, session, logs
        try { if (!Directory.Exists(dir) && Directory.Exists(legacy)) Directory.Move(legacy, dir); }
        catch (IOException) { /* in use: start fresh */ }
        return EnsureDir(dir);
    }
    public static string Db => Path.Combine(Root, "data.db");
    public static string TokenFile => Path.Combine(Root, "token.bin");
    public static string LogDir => EnsureDir(Path.Combine(Root, "logs"));
    public static string DefaultTemplate => Path.Combine(Root, "template.docx");

    static string EnsureDir(string p) { Directory.CreateDirectory(p); return p; }
}
