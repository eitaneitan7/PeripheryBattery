using System.Text.Json;
using System.Text.RegularExpressions;
using PeripheryBattery.Models;
using PeripheryBattery.Utils;

namespace PeripheryBattery.Providers;

/// <summary>
/// Reads battery data from Razer Synapse log files.
/// Supports both Synapse 3 and Synapse 4 log formats.
/// </summary>
public class RazerProvider : IDeviceProvider
{
    public string Vendor => "Razer";

    private readonly List<DeviceInfo> _lastKnown = new();

    // Synapse 3: %LOCALAPPDATA%\Razer\Synapse3\Log\Razer Synapse 3.log
    private static readonly string Synapse3LogPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "Razer", "Synapse3", "Log", "Razer Synapse 3.log");

    // Synapse 4: %LOCALAPPDATA%\Razer\RazerAppEngine\User Data\Logs\systray_systrayv2.log
    private static readonly string Synapse4LogPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "Razer", "RazerAppEngine", "User Data", "Logs", "systray_systrayv2.log");

    // Synapse 3 regex: multiline battery event
    private static readonly Regex S3BatteryRegex = new(
        @"^(?<dateTime>.+?)\s+INFO.+?_OnBatteryLevelChanged[\s\S]*?Name:\s*(?<name>.*?)[\r\n][\s\S]*?Handle:\s*(?<handle>\d+)[\s\S]*?level\s+(?<level>\d+)\s+state\s+(?<isCharging>\d+)",
        RegexOptions.Multiline | RegexOptions.Compiled);

    // Synapse 3 simple fallback: just find the last "level X state Y"
    private static readonly Regex S3SimpleBatteryRegex = new(
        @"level\s+(?<level>\d+)\s+state\s+(?<isCharging>\d+)",
        RegexOptions.Compiled);

    // Synapse 4 regex: JSON payload with device data
    private static readonly Regex S4BatteryRegex = new(
        @"^\[(?<timestamp>.+?)\].*connectingDeviceData:\s*(?<json>.+)$",
        RegexOptions.Multiline | RegexOptions.Compiled);

    public Task StartAsync(CancellationToken ct) => Task.CompletedTask;
    public Task StopAsync() => Task.CompletedTask;

    public Task<List<DeviceInfo>> GetDevicesAsync(CancellationToken ct)
    {
        try
        {
            // Try Synapse 4 first, then fall back to Synapse 3
            if (File.Exists(Synapse4LogPath))
            {
                Logger.Log($"[Razer] Using Synapse 4 log: {Synapse4LogPath}");
                return Task.FromResult(ParseSynapse4Log());
            }

            if (File.Exists(Synapse3LogPath))
            {
                Logger.Log($"[Razer] Using Synapse 3 log: {Synapse3LogPath}");
                return Task.FromResult(ParseSynapse3Log());
            }

            Logger.Log("[Razer] No Synapse log files found. Synapse may not be installed or running.");
            return Task.FromResult(new List<DeviceInfo>
            {
                new()
                {
                    Id = "razer-unknown",
                    Vendor = "Razer",
                    DeviceType = "Mouse",
                    DisplayName = "Razer Mouse",
                    Connected = false,
                    Source = "SynapseLog",
                    LastUpdated = DateTime.Now,
                    Error = "Synapse not found"
                }
            });
        }
        catch (Exception ex)
        {
            Logger.Log($"[Razer] Error reading battery data: {ex.Message}");
            return Task.FromResult(new List<DeviceInfo>
            {
                new()
                {
                    Id = "razer-error",
                    Vendor = "Razer",
                    DeviceType = "Mouse",
                    DisplayName = "Razer Mouse",
                    Connected = false,
                    Source = "SynapseLog",
                    LastUpdated = DateTime.Now,
                    Error = ex.Message
                }
            });
        }
    }

    private List<DeviceInfo> ParseSynapse3Log()
    {
        var content = ReadLogFileSafe(Synapse3LogPath);
        if (content == null) return ReturnError("Cannot read Synapse 3 log");

        // Try detailed regex first (gets device name)
        var matches = S3BatteryRegex.Matches(content);
        if (matches.Count > 0)
        {
            // Group by device name, take the last (most recent) entry for each
            var deviceMap = new Dictionary<string, DeviceInfo>(StringComparer.OrdinalIgnoreCase);

            foreach (Match m in matches)
            {
                var name = m.Groups["name"].Value.Trim();
                var level = int.Parse(m.Groups["level"].Value);
                var charging = m.Groups["isCharging"].Value != "0";

                deviceMap[name] = new DeviceInfo
                {
                    Id = $"razer-s3-{name.GetHashCode():x8}",
                    Vendor = "Razer",
                    DeviceType = ClassifyRazerDevice(name),
                    DisplayName = name,
                    BatteryPercent = Math.Clamp(level, 0, 100),
                    Charging = charging,
                    Connected = true,
                    Source = "SynapseLog-V3",
                    LastUpdated = DateTime.Now,
                };
            }

            _lastKnown.Clear();
            _lastKnown.AddRange(deviceMap.Values);
            return _lastKnown.ToList();
        }

        // Fallback: simple regex, can't distinguish devices
        var simpleMatches = S3SimpleBatteryRegex.Matches(content);
        if (simpleMatches.Count > 0)
        {
            var last = simpleMatches[^1]; // most recent
            var level = int.Parse(last.Groups["level"].Value);
            var charging = last.Groups["isCharging"].Value != "0";

            return new List<DeviceInfo>
            {
                new()
                {
                    Id = "razer-s3-default",
                    Vendor = "Razer",
                    DeviceType = "Mouse",
                    DisplayName = "Razer Device",
                    BatteryPercent = Math.Clamp(level, 0, 100),
                    Charging = charging,
                    Connected = true,
                    Source = "SynapseLog-V3",
                    LastUpdated = DateTime.Now,
                }
            };
        }

        return ReturnError("No battery data in Synapse 3 log");
    }

    private List<DeviceInfo> ParseSynapse4Log()
    {
        var content = ReadLogFileSafe(Synapse4LogPath);
        if (content == null) return ReturnError("Cannot read Synapse 4 log");

        var matches = S4BatteryRegex.Matches(content);
        if (matches.Count == 0)
            return ReturnError("No battery data in Synapse 4 log");

        // Take the last match (most recent)
        var lastMatch = matches[^1];
        var jsonStr = lastMatch.Groups["json"].Value.Trim();

        try
        {
            var devices = new List<DeviceInfo>();
            using var doc = JsonDocument.Parse(jsonStr);
            var root = doc.RootElement;

            // The JSON may be an array or object containing device entries
            var elements = root.ValueKind == JsonValueKind.Array
                ? root.EnumerateArray()
                : new[] { root }.AsEnumerable();

            foreach (var dev in elements)
            {
                var hasBattery = dev.TryGetProperty("hasBattery", out var hb) && hb.GetBoolean();
                if (!hasBattery) continue;

                var name = "";
                if (dev.TryGetProperty("name", out var nameObj))
                {
                    name = nameObj.TryGetProperty("en", out var en)
                        ? en.GetString() ?? ""
                        : nameObj.GetString() ?? "";
                }

                int? level = null;
                var charging = false;
                if (dev.TryGetProperty("powerStatus", out var ps))
                {
                    if (ps.TryGetProperty("level", out var lv))
                        level = lv.GetInt32();
                    if (ps.TryGetProperty("chargingStatus", out var cs))
                        charging = cs.GetString()?.Contains("Charging", StringComparison.OrdinalIgnoreCase) == true
                                   && cs.GetString() != "NoCharge_BatteryFull";
                }

                devices.Add(new DeviceInfo
                {
                    Id = $"razer-s4-{name.GetHashCode():x8}",
                    Vendor = "Razer",
                    DeviceType = ClassifyRazerDevice(name),
                    DisplayName = name,
                    BatteryPercent = level.HasValue ? Math.Clamp(level.Value, 0, 100) : null,
                    Charging = charging,
                    Connected = true,
                    Source = "SynapseLog-V4",
                    LastUpdated = DateTime.Now,
                });
            }

            if (devices.Count > 0)
            {
                _lastKnown.Clear();
                _lastKnown.AddRange(devices);
                return devices;
            }

            return ReturnError("No battery-capable devices in Synapse 4 log");
        }
        catch (JsonException ex)
        {
            Logger.Log($"[Razer] JSON parse error: {ex.Message}");
            return ReturnError("Failed to parse Synapse 4 battery JSON");
        }
    }

    /// <summary>
    /// Reads log file with shared read access (Synapse may still be writing to it).
    /// Reads last 512KB to avoid loading huge log files.
    /// </summary>
    private static string? ReadLogFileSafe(string path)
    {
        try
        {
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            const int maxRead = 512 * 1024;
            if (fs.Length > maxRead)
                fs.Seek(-maxRead, SeekOrigin.End);
            using var reader = new StreamReader(fs);
            return reader.ReadToEnd();
        }
        catch (Exception ex)
        {
            Logger.Log($"[Razer] Failed to read {path}: {ex.Message}");
            return null;
        }
    }

    private static string ClassifyRazerDevice(string name)
    {
        var lower = name.ToLowerInvariant();
        if (lower.Contains("mouse") || lower.Contains("deathadder") || lower.Contains("viper") ||
            lower.Contains("basilisk") || lower.Contains("mamba") || lower.Contains("naga") ||
            lower.Contains("orochi") || lower.Contains("cobra"))
            return "Mouse";
        if (lower.Contains("keyboard") || lower.Contains("huntsman") || lower.Contains("blackwidow") ||
            lower.Contains("deathstalker") || lower.Contains("ornata"))
            return "Keyboard";
        if (lower.Contains("headset") || lower.Contains("headphone") || lower.Contains("kraken") ||
            lower.Contains("blackshark") || lower.Contains("barracuda") || lower.Contains("opus"))
            return "Headset";
        return "Unknown";
    }

    private static List<DeviceInfo> ReturnError(string error)
    {
        Logger.Log($"[Razer] {error}");
        return new List<DeviceInfo>
        {
            new()
            {
                Id = "razer-unavailable",
                Vendor = "Razer",
                DeviceType = "Mouse",
                DisplayName = "Razer Device",
                Connected = false,
                Source = "SynapseLog",
                LastUpdated = DateTime.Now,
                Error = error
            }
        };
    }
}
