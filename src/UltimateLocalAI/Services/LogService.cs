using System.Text;

namespace UltimateLocalAI.Services;

public static class LogService
{
    private static readonly object Sync = new();
    private static string _file = "";

    public static void Initialize()
    {
        AppPaths.EnsureDirectories();
        _file = Path.Combine(AppPaths.LogsDir, $"localai_{DateTime.Now:yyyyMMdd}.log");
        Info("=== Ultimate Local AI started ===");
    }

    public static void Info(string message) => Write("INFO", message);
    public static void Warn(string message) => Write("WARN", message);
    public static void Error(string message, Exception? ex = null) => Write("ERROR", ex is null ? message : $"{message}\n{ex}");

    private static void Write(string level, string message)
    {
        try
        {
            lock (Sync)
            {
                File.AppendAllText(_file, $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}] {level} {message}{Environment.NewLine}", Encoding.UTF8);
            }
        }
        catch { }
    }
}
