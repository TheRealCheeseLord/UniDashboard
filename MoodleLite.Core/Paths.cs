namespace MoodleLite.Core;

public static class Paths
{
    public static string Root { get; } = EnsureDir(Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "MoodleLite"));
    public static string Db => Path.Combine(Root, "data.db");
    public static string TokenFile => Path.Combine(Root, "token.bin");
    public static string LogDir => EnsureDir(Path.Combine(Root, "logs"));
    public static string DefaultTemplate => Path.Combine(Root, "template.docx");

    static string EnsureDir(string p) { Directory.CreateDirectory(p); return p; }
}
