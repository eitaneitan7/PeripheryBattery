using System.Text.Json;
using System.Text.RegularExpressions;
using PeripheryBattery.Models;
using PeripheryBattery.Utils;

namespace PeripheryBattery.Providers;

/// <summary>
/// Reads battery data from Razer Synapse log files.
/// Synapse 4: reads per-device product logs (products_*_mw *.log) for real-time battery.
/// Synapse 3: reads main log for battery level change events.
/// Falls back to systray_systrayv2.log connectingDeviceData if product logs unavailable.
/// </summary>
public class RazerProvider : IDeviceProvider
{
    public string Vendor => "Razer";

    private readonly List<DeviceInfo> _lastKnown = new();
    private bool _loggedNotFound;
    private bool _loggedFirstPoll;

    // Synapse 4 logs directory
    private static readonly string Synapse4LogDir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "Razer", "RazerAppEngine", "User Data", "Logs");

    // Synapse 3 log
    private static readonly string Synapse3LogPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "Razer", "Synapse3", "Log", "Razer Synapse 3.log");

    // Synapse 4 product log: GET_BATTERY_STATE JSON
    // Pattern: [updateUI] GET_BATTERY_STATE {"chargingStatus":"...","level":54,...}
    private static readonly Regex S4ProductBatteryRegex = new(
        @"GET_BATTERY_STATE\s+(?<json>\{[^}]+\})",
        RegexOptions.Compiled);

    // Synapse 4 systray: connectingDeviceData (fallback, may be stale)
    private static readonly Regex S4SystrayBatteryRegex = new(
        @"^\[(?<timestamp>.+?)\].*connectingDeviceData:\s*(?<json>.+)$",
        RegexOptions.Multiline | RegexOptions.Compiled);

    // Synapse 3: battery level change event
    private static readonly Regex S3BatteryRegex = new(
        @"^(?<dateTime>.+?)\s+INFO.+?_OnBatteryLevelChanged[\s\S]*?Name:\s*(?<name>.*?)[\r\n][\s\S]*?Handle:\s*(?<handle>\d+)[\s\S]*?level\s+(?<level>\d+)\s+state\s+(?<isCharging>\d+)",
        RegexOptions.Multiline | RegexOptions.Compiled);

    // Synapse 3: simple fallback
    private static readonly Regex S3SimpleBatteryRegex = new(
        @"level\s+(?<level>\d+)\s+state\s+(?<isCharging>\d+)",
        RegexOptions.Compiled);

    public Task StartAsync(CancellationToken ct) => Task.CompletedTask;
    public Task StopAsync() => Task.CompletedTask;

    public Task<List<DeviceInfo>> GetDevicesAsync(CancellationToken ct)
    {
        try
        {
            // Try Synapse 4 product logs first (most accurate, real-time)
            if (Directory.Exists(Synapse4LogDir))
            {
                var s4Result = ParseSynapse4ProductLogs();
                if (s4Result.Count > 0)
                    return Task.FromResult(s4Result);

                // Fallback: try systray log
                var systrayLog = Path.Combine(Synapse4LogDir, "systray_systrayv2.log");
                if (File.Exists(systrayLog))
                {
                    var fallback = ParseSynapse4SystrayLog(systrayLog);
                    if (fallback.Count > 0)
                        return Task.FromResult(fallback);
                }
            }

            // Try Synapse 3
            if (File.Exists(Synapse3LogPath))
                return Task.FromResult(ParseSynapse3Log());

            if (!_loggedNotFound)
            {
                Logger.Log("[Razer] No Synapse log files found, provider disabled");
                _loggedNotFound = true;
            }
            return Task.FromResult(new List<DeviceInfo>());
        }
        catch (Exception ex)
        {
            Logger.Log($"[Razer] Error: {ex.Message}");
            return Task.FromResult(_lastKnown.ToList());
        }
    }

    /// <summary>
    /// Scans all products_*_mw *.log files for GET_BATTERY_STATE entries.
    /// These have real-time battery data updated every ~2.5 minutes by Synapse.
    /// </summary>
    private List<DeviceInfo> ParseSynapse4ProductLogs()
    {
        var results = new List<DeviceInfo>();

        string[] productLogs;
        try
        {
            productLogs = Directory.GetFiles(Synapse4LogDir, "products_*_mw *.log");
        }
        catch
        {
            return results;
        }

        if (!_loggedFirstPoll && productLogs.Length > 0)
            Logger.Log($"[Razer] Found {productLogs.Length} product log file(s)");

        foreach (var logPath in productLogs)
        {
            var content = ReadLogFileSafe(logPath);
            if (content == null) continue;

            var matches = S4ProductBatteryRegex.Matches(content);
            if (matches.Count == 0) continue;

            // Take the last (most recent) battery entry
            var lastMatch = matches[^1];
            var jsonStr = lastMatch.Groups["json"].Value;

            try
            {
                using var doc = JsonDocument.Parse(jsonStr);
                var root = doc.RootElement;

                int? level = null;
                var charging = false;
                string? chargingRaw = null;

                if (root.TryGetProperty("level", out var lv))
                    level = lv.GetInt32();
                if (root.TryGetProperty("chargingStatus", out var cs))
                {
                    chargingRaw = cs.GetString();
                    charging = chargingRaw != null
                        && chargingRaw.Contains("Charging", StringComparison.OrdinalIgnoreCase)
                        && !chargingRaw.Contains("NoCharge", StringComparison.OrdinalIgnoreCase);
                }

                if (level == null) continue;

                // Extract device name from log filename or from connectingDeviceData in systray
                // Filename format: products_<pid>_mw {<guid>}.log
                var fileName = Path.GetFileName(logPath);
                var deviceName = ExtractDeviceNameFromProductLog(content) ?? $"Razer Device ({fileName})";

                if (!_loggedFirstPoll)
                    Logger.Log($"[Razer] Product log: {fileName} -> name=\"{deviceName}\" level={level} charging=\"{chargingRaw}\"");

                results.Add(new DeviceInfo
                {
                    Id = $"razer-s4-{fileName.GetHashCode():x8}",
                    Vendor = "Razer",
                    DeviceType = ClassifyRazerDevice(deviceName),
                    DisplayName = deviceName,
                    BatteryPercent = Math.Clamp(level.Value, 0, 100),
                    Charging = charging,
                    Connected = true,
                    Source = "SynapseLog-V4-Product",
                    LastUpdated = DateTime.Now,
                });
            }
            catch (JsonException ex)
            {
                if (!_loggedFirstPoll)
                    Logger.Log($"[Razer] JSON parse error in {Path.GetFileName(logPath)}: {ex.Message}");
            }
        }

        _loggedFirstPoll = true;

        if (results.Count > 0)
        {
            _lastKnown.Clear();
            _lastKnown.AddRange(results);
        }

        return results;
    }

    /// <summary>
    /// Tries to find the device name from the product log content.
    /// Looks for patterns like "deviceName" or product identifiers.
    /// </summary>
    private static string? ExtractDeviceNameFromProductLog(string content)
    {
        // Look for "name":"Razer ..." in the log
        var nameMatch = Regex.Match(content, @"""name""\s*:\s*""(Razer[^""]+)""", RegexOptions.IgnoreCase);
        if (nameMatch.Success) return nameMatch.Groups[1].Value;

        // Look for common Razer device name patterns
        var deviceMatch = Regex.Match(content, @"((?:Razer\s+)?(?:DeathAdder|Viper|Basilisk|Mamba|Naga|Orochi|Cobra|Huntsman|BlackWidow|Kraken|BlackShark|Barracuda)[^""',\]\}]*)", RegexOptions.IgnoreCase);
        if (deviceMatch.Success) return deviceMatch.Groups[1].Value.Trim();

        return null;
    }

    /// <summary>
    /// Fallback: parse systray_systrayv2.log connectingDeviceData (may have stale battery).
    /// </summary>
    private List<DeviceInfo> ParseSynapse4SystrayLog(string path)
    {
        var content = ReadLogFileSafe(path);
        if (content == null) return new List<DeviceInfo>();

        var matches = S4SystrayBatteryRegex.Matches(content);
        if (matches.Count == 0) return new List<DeviceInfo>();

        var lastMatch = matches[^1];
        var jsonStr = lastMatch.Groups["json"].Value.Trim();

        try
        {
            var devices = new List<DeviceInfo>();
            using var doc = JsonDocument.Parse(jsonStr);
            var root = doc.RootElement;

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
                        ? en.GetString() ?? "" : nameObj.GetString() ?? "";
                }

                int? level = null;
                var charging = false;
                if (dev.TryGetProperty("powerStatus", out var ps))
                {
                    if (ps.TryGetProperty("level", out var lv)) level = lv.GetInt32();
                    if (ps.TryGetProperty("chargingStatus", out var cs))
                    {
                        var raw = cs.GetString();
                        charging = raw != null
                            && raw.Contains("Charging", StringComparison.OrdinalIgnoreCase)
                            && !raw.Contains("NoCharge", StringComparison.OrdinalIgnoreCase);
                    }
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
                    Source = "SynapseLog-V4-Systray",
                    LastUpdated = DateTime.Now,
                });
            }

            if (devices.Count > 0)
            {
                _lastKnown.Clear();
                _lastKnown.AddRange(devices);
            }
            return devices;
        }
        catch (JsonException)
        {
            return new List<DeviceInfo>();
        }
    }

    private List<DeviceInfo> ParseSynapse3Log()
    {
        var content = ReadLogFileSafe(Synapse3LogPath);
        if (content == null) return new List<DeviceInfo>();

        var matches = S3BatteryRegex.Matches(content);
        if (matches.Count > 0)
        {
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

        var simpleMatches = S3SimpleBatteryRegex.Matches(content);
        if (simpleMatches.Count > 0)
        {
            var last = simpleMatches[^1];
            return new List<DeviceInfo>
            {
                new()
                {
                    Id = "razer-s3-default", Vendor = "Razer", DeviceType = "Mouse",
                    DisplayName = "Razer Device",
                    BatteryPercent = Math.Clamp(int.Parse(last.Groups["level"].Value), 0, 100),
                    Charging = last.Groups["isCharging"].Value != "0",
                    Connected = true, Source = "SynapseLog-V3", LastUpdated = DateTime.Now,
                }
            };
        }

        return new List<DeviceInfo>();
    }

    private static string? ReadLogFileSafe(string path)
    {
        try
        {
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            const int maxRead = 512 * 1024;
            if (fs.Length > maxRead) fs.Seek(-maxRead, SeekOrigin.End);
            using var reader = new StreamReader(fs);
            return reader.ReadToEnd();
        }
        catch { return null; }
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
}
