namespace UltimateLocalAI.Services;

public static class AppPaths
{
    public static string BaseDir => AppContext.BaseDirectory;
    public static string DataDir => Path.Combine(BaseDir, "Data");
    public static string LogsDir => Path.Combine(BaseDir, "Logs");
    public static string BackendsDir => Path.Combine(BaseDir, "backends");
    public static string ConfigFile => Path.Combine(DataDir, "config.json");
    public static string ChatsFile => Path.Combine(DataDir, "chats.json");
    public static string KnowledgeFile => Path.Combine(DataDir, "knowledge.json");
    public static string TempDir => Path.Combine(DataDir, "Temp");
    public static string BackupsDir => Path.Combine(DataDir, "Backups");

    public static string ResolveBackendsDir(string runtimeChannel, string? customRuntimePath)
    {
        if (string.Equals(runtimeChannel, "Custom", StringComparison.OrdinalIgnoreCase) &&
            !string.IsNullOrWhiteSpace(customRuntimePath))
            return Path.GetFullPath(customRuntimePath);

        var channel = string.Equals(runtimeChannel, "Latest", StringComparison.OrdinalIgnoreCase) ? "latest" : "stable";
        var channelDir = Path.Combine(BackendsDir, channel);
        return Directory.Exists(channelDir) ? channelDir : BackendsDir; // backwards compatibility
    }

    public static void EnsureDirectories()
    {
        Directory.CreateDirectory(DataDir);
        Directory.CreateDirectory(LogsDir);
        Directory.CreateDirectory(BackendsDir);
        Directory.CreateDirectory(TempDir);
        Directory.CreateDirectory(BackupsDir);
    }
}
