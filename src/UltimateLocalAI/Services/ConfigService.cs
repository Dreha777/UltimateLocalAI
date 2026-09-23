using System.Text.Json;
using UltimateLocalAI.Models;

namespace UltimateLocalAI.Services;

public sealed class ConfigService
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    public AppConfig Load()
    {
        try
        {
            if (!File.Exists(AppPaths.ConfigFile)) return new AppConfig();
            return JsonSerializer.Deserialize<AppConfig>(File.ReadAllText(AppPaths.ConfigFile), JsonOptions) ?? new AppConfig();
        }
        catch (Exception ex)
        {
            LogService.Error("Cannot load config", ex);
            return new AppConfig();
        }
    }

    public void Save(AppConfig config)
    {
        try
        {
            AppPaths.EnsureDirectories();
            var tmp = AppPaths.ConfigFile + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(config, JsonOptions));
            File.Move(tmp, AppPaths.ConfigFile, true);
        }
        catch (Exception ex)
        {
            LogService.Error("Cannot save config", ex);
            throw;
        }
    }
}
