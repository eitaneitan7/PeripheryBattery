using System.Text.Json;
using PeripheryBattery.Utils;

namespace PeripheryBattery.Config;

public class AppConfig
{
    public int PollIntervalSeconds { get; set; } = 5;
    public int LowBatteryThreshold { get; set; } = 20;
    public bool ShowNotifications { get; set; } = true;
    public bool StartWithWindows { get; set; } = false;
    public int IconCycleSeconds { get; set; } = 3;
    public bool EnableLogging { get; set; } = true;

    // Device name overrides: map detected name substring -> friendly name
    // Example: { "DeathAdder": "My Mouse" } would rename any device containing "DeathAdder"
    public Dictionary<string, string> DeviceNameOverrides { get; set; } = new();

    private static readonly string ConfigDir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "PeripheryBattery");

    private static readonly string ConfigFile = Path.Combine(ConfigDir, "config.json");

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true,
    };

    public static AppConfig Load()
    {
        try
        {
            Directory.CreateDirectory(ConfigDir);
            if (File.Exists(ConfigFile))
            {
                var json = File.ReadAllText(ConfigFile);
                return JsonSerializer.Deserialize<AppConfig>(json, JsonOptions) ?? new AppConfig();
            }
        }
        catch (Exception ex)
        {
            Logger.Log($"[Config] Failed to load: {ex.Message}");
        }

        var config = new AppConfig();
        config.Save();
        return config;
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(ConfigDir);
            var json = JsonSerializer.Serialize(this, JsonOptions);
            File.WriteAllText(ConfigFile, json);
        }
        catch (Exception ex)
        {
            Logger.Log($"[Config] Failed to save: {ex.Message}");
        }
    }

    /// <summary>
    /// Applies name overrides: if any key is a substring of the original name, replace it.
    /// </summary>
    public string ApplyNameOverride(string originalName)
    {
        foreach (var (pattern, friendly) in DeviceNameOverrides)
        {
            if (originalName.Contains(pattern, StringComparison.OrdinalIgnoreCase))
                return friendly;
        }
        return originalName;
    }

    public static string ConfigFilePath => ConfigFile;
}
