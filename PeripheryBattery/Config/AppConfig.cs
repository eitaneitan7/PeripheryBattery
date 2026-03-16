using System.Text.Json;
using PeripheryBattery.Utils;

namespace PeripheryBattery.Config;

public class AppConfig
{
    public int PollIntervalSeconds { get; set; } = 60;
    public int LowBatteryThreshold { get; set; } = 20;
    public bool ShowNotifications { get; set; } = true;
    public bool StartWithWindows { get; set; } = false;

    // Device name overrides: map detected name -> friendly name
    // e.g. "G915 TKL LIGHTSPEED Wireless RGB Mechanical Gaming Keyboard" -> "Keyboard"
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

        // Create default config
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

    public static string ConfigFilePath => ConfigFile;
}
