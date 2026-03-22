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
    private int _pollCount;

    /// <summary>
    /// How old a product log file can be before we consider the device disconnected.
    /// Synapse writes to active device logs every ~2.5 minutes, so 15 minutes is generous.
    /// </summary>
    private static readonly TimeSpan StaleLogThreshold = TimeSpan.FromMinutes(15);

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

    public Task StartAsync(CancellationToken ct)
    {
        Logger.Log($"[Razer] Provider starting. Synapse 4 log dir: {Synapse4LogDir}");
        Logger.Log($"[Razer] Synapse 4 dir exists: {Directory.Exists(Synapse4LogDir)}");
        Logger.Log($"[Razer] Synapse 3 log exists: {File.Exists(Synapse3LogPath)}");
        Logger.Log($"[Razer] Stale log threshold: {StaleLogThreshold.TotalMinutes} minutes");
        return Task.CompletedTask;
    }

    public Task StopAsync() => Task.CompletedTask;

    public Task<List<DeviceInfo>> GetDevicesAsync(CancellationToken ct)
    {
        _pollCount++;
        var isDetailedLog = _pollCount <= 3 || _pollCount % 60 == 0; // Log details on first 3 polls and every 5 min

        try
        {
            // Try Synapse 4 product logs first (most accurate, real-time)
            if (Directory.Exists(Synapse4LogDir))
            {
                var (s4Result, hadProductLogs) = ParseSynapse4ProductLogs(isDetailedLog);
                if (s4Result.Count > 0)
                    return Task.FromResult(s4Result);

                // If product logs existed but were all stale, don't fall back to systray
                // (systray has no timestamps and would show disconnected devices)
                if (hadProductLogs)
                {
                    if (isDetailedLog)
                        Logger.Log("[Razer] Product logs existed but all stale — NOT falling back to systray (would show disconnected devices)");
                    return Task.FromResult(new List<DeviceInfo>());
                }

                if (isDetailedLog)
                    Logger.Log("[Razer] No product logs found at all, trying systray fallback...");

                // Fallback: try systray log (only if no product logs exist at all)
                var systrayLog = Path.Combine(Synapse4LogDir, "systray_systrayv2.log");
                if (File.Exists(systrayLog))
                {
                    if (isDetailedLog)
                        Logger.Log($"[Razer] Systray log exists: {systrayLog}");
                    var fallback = ParseSynapse4SystrayLog(systrayLog);
                    if (fallback.Count > 0)
                        return Task.FromResult(fallback);
                    if (isDetailedLog)
                        Logger.Log("[Razer] Systray log had no usable battery data");
                }
                else if (isDetailedLog)
                {
                    Logger.Log($"[Razer] Systray log not found: {systrayLog}");
                }
            }

            // Try Synapse 3
            if (File.Exists(Synapse3LogPath))
            {
                if (isDetailedLog)
                    Logger.Log("[Razer] Trying Synapse 3 log...");
                return Task.FromResult(ParseSynapse3Log());
            }

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
    /// Stale log files (not modified within StaleLogThreshold) are skipped
    /// to avoid showing disconnected devices.
    /// </summary>
    // Regex to extract product ID from filename: products_<pid>_mw {<guid>}.log
    private static readonly Regex ProductIdRegex = new(
        @"products_(\d+)_mw\s", RegexOptions.Compiled);

    /// <summary>
    /// Returns (devices, hadProductLogs) — hadProductLogs is true if product log files existed
    /// (even if all were stale), so the caller knows not to fall back to the systray log.
    /// </summary>
    private (List<DeviceInfo> devices, bool hadProductLogs) ParseSynapse4ProductLogs(bool isDetailedLog)
    {
        var results = new List<DeviceInfo>();

        string[] allLogs;
        try
        {
            allLogs = Directory.GetFiles(Synapse4LogDir, "products_*_mw *.log");
        }
        catch (Exception ex)
        {
            Logger.Log($"[Razer] Failed to enumerate product logs: {ex.Message}");
            return (results, false);
        }

        if (isDetailedLog)
            Logger.Log($"[Razer] Found {allLogs.Length} total product log file(s) in {Synapse4LogDir}");

        // Synapse rotates logs: {guid}.log, {guid}1.log, {guid}2.log, etc.
        // Group by product ID (e.g. "182") and only take the most recently modified file per product.
        var grouped = allLogs
            .Select(f => new { Path = f, Info = new FileInfo(f) })
            .Where(f => f.Info.Exists)
            .GroupBy(f =>
            {
                var m = ProductIdRegex.Match(Path.GetFileName(f.Path));
                return m.Success ? m.Groups[1].Value : Path.GetFileName(f.Path);
            })
            .ToList();

        if (isDetailedLog)
        {
            foreach (var g in grouped)
            {
                var files = g.OrderByDescending(f => f.Info.LastWriteTime).ToList();
                Logger.Log($"[Razer] Product group '{g.Key}': {files.Count} log file(s)");
                foreach (var f in files)
                    Logger.Log($"[Razer]   {Path.GetFileName(f.Path)} — modified={f.Info.LastWriteTime:yyyy-MM-dd HH:mm:ss}, size={f.Info.Length} bytes");
            }
        }

        var now = DateTime.Now;

        var productLogs = grouped
            .Select(g =>
            {
                var newest = g.OrderByDescending(f => f.Info.LastWriteTime).First();
                return new { ProductId = g.Key, newest.Path, newest.Info };
            })
            .ToList();

        // Filter out stale logs (device likely disconnected)
        var activeLogs = new List<(string ProductId, string Path, FileInfo Info)>();
        foreach (var log in productLogs)
        {
            var age = now - log.Info.LastWriteTime;
            if (age > StaleLogThreshold)
            {
                if (isDetailedLog)
                    Logger.Log($"[Razer] SKIPPING product '{log.ProductId}' — log is stale (last modified {age.TotalMinutes:F1} min ago, threshold={StaleLogThreshold.TotalMinutes} min): {Path.GetFileName(log.Path)}");
                continue;
            }
            if (isDetailedLog)
                Logger.Log($"[Razer] Product '{log.ProductId}' is ACTIVE (last modified {age.TotalMinutes:F1} min ago): {Path.GetFileName(log.Path)}");
            activeLogs.Add((log.ProductId, log.Path, log.Info));
        }

        if (!_loggedFirstPoll && allLogs.Length > 0)
            Logger.Log($"[Razer] Found {allLogs.Length} product log file(s), {grouped.Count} product(s), {activeLogs.Count} active (not stale)");

        foreach (var (productId, logPath, logInfo) in activeLogs)
        {
            var content = ReadLogFileSafe(logPath);
            if (content == null)
            {
                if (isDetailedLog)
                    Logger.Log($"[Razer] Could not read log for product '{productId}': {Path.GetFileName(logPath)}");
                continue;
            }

            if (isDetailedLog)
                Logger.Log($"[Razer] Read {content.Length} chars from product '{productId}' log: {Path.GetFileName(logPath)}");

            var matches = S4ProductBatteryRegex.Matches(content);
            if (matches.Count == 0)
            {
                if (isDetailedLog)
                    Logger.Log($"[Razer] No GET_BATTERY_STATE entries in product '{productId}' log");
                continue;
            }

            if (isDetailedLog)
                Logger.Log($"[Razer] Found {matches.Count} GET_BATTERY_STATE entries in product '{productId}' log");

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

                if (level == null)
                {
                    if (isDetailedLog)
                        Logger.Log($"[Razer] Product '{productId}': battery JSON had no 'level' field. JSON: {jsonStr}");
                    continue;
                }

                // Extract device name from log content
                // Filename format: products_<pid>_mw {<guid>}.log
                var fileName = Path.GetFileName(logPath);
                var deviceName = ExtractDeviceNameFromProductLog(content) ?? $"Razer Device ({fileName})";

                if (isDetailedLog || !_loggedFirstPoll)
                    Logger.Log($"[Razer] Product '{productId}': name=\"{deviceName}\" level={level} charging=\"{chargingRaw}\" file={fileName} fileAge={((now - logInfo.LastWriteTime).TotalMinutes):F1}min");

                results.Add(new DeviceInfo
                {
                    Id = $"razer-s4-{productId}",
                    Vendor = "Razer",
                    DeviceType = ClassifyRazerDevice(deviceName),
                    DisplayName = deviceName,
                    BatteryPercent = Math.Clamp(level.Value, 0, 100),
                    Charging = charging,
                    Connected = true,
                    Source = $"SynapseLog-V4-Product-{productId}",
                    LastUpdated = DateTime.Now,
                });
            }
            catch (JsonException ex)
            {
                Logger.Log($"[Razer] JSON parse error in product '{productId}' ({Path.GetFileName(logPath)}): {ex.Message}. JSON: {jsonStr}");
            }
        }

        _loggedFirstPoll = true;

        if (isDetailedLog)
            Logger.Log($"[Razer] S4 product logs result: {results.Count} device(s) — [{string.Join(", ", results.Select(r => $"{r.DisplayName}({r.BatteryPercent}%)"))}]");

        if (results.Count > 0)
        {
            _lastKnown.Clear();
            _lastKnown.AddRange(results);
        }

        return (results, allLogs.Length > 0);
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
        if (content == null)
        {
            Logger.Log("[Razer] Could not read systray log");
            return new List<DeviceInfo>();
        }

        Logger.Log($"[Razer] Systray log: read {content.Length} chars");

        var matches = S4SystrayBatteryRegex.Matches(content);
        Logger.Log($"[Razer] Systray log: found {matches.Count} connectingDeviceData entries");
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

            var elementIndex = 0;
            foreach (var dev in elements)
            {
                var hasBattery = dev.TryGetProperty("hasBattery", out var hb) && hb.GetBoolean();

                var name = "";
                if (dev.TryGetProperty("name", out var nameObj))
                {
                    name = nameObj.TryGetProperty("en", out var en)
                        ? en.GetString() ?? "" : nameObj.GetString() ?? "";
                }

                Logger.Log($"[Razer] Systray device #{elementIndex}: name=\"{name}\" hasBattery={hasBattery}");

                if (!hasBattery)
                {
                    elementIndex++;
                    continue;
                }

                int? level = null;
                var charging = false;
                string? chargingRaw = null;
                if (dev.TryGetProperty("powerStatus", out var ps))
                {
                    if (ps.TryGetProperty("level", out var lv)) level = lv.GetInt32();
                    if (ps.TryGetProperty("chargingStatus", out var cs))
                    {
                        chargingRaw = cs.GetString();
                        charging = chargingRaw != null
                            && chargingRaw.Contains("Charging", StringComparison.OrdinalIgnoreCase)
                            && !chargingRaw.Contains("NoCharge", StringComparison.OrdinalIgnoreCase);
                    }
                }

                Logger.Log($"[Razer] Systray device #{elementIndex}: name=\"{name}\" level={level} charging=\"{chargingRaw}\"");

                devices.Add(new DeviceInfo
                {
                    Id = $"razer-s4-systray-{name.GetHashCode():x8}",
                    Vendor = "Razer",
                    DeviceType = ClassifyRazerDevice(name),
                    DisplayName = name,
                    BatteryPercent = level.HasValue ? Math.Clamp(level.Value, 0, 100) : null,
                    Charging = charging,
                    Connected = true,
                    Source = "SynapseLog-V4-Systray",
                    LastUpdated = DateTime.Now,
                });
                elementIndex++;
            }

            Logger.Log($"[Razer] Systray result: {devices.Count} device(s) with battery");

            if (devices.Count > 0)
            {
                _lastKnown.Clear();
                _lastKnown.AddRange(devices);
            }
            return devices;
        }
        catch (JsonException ex)
        {
            Logger.Log($"[Razer] Systray JSON parse error: {ex.Message}. JSON preview: {jsonStr[..Math.Min(jsonStr.Length, 200)]}");
            return new List<DeviceInfo>();
        }
    }

    private List<DeviceInfo> ParseSynapse3Log()
    {
        var content = ReadLogFileSafe(Synapse3LogPath);
        if (content == null)
        {
            Logger.Log("[Razer] Could not read Synapse 3 log");
            return new List<DeviceInfo>();
        }

        Logger.Log($"[Razer] Synapse 3 log: read {content.Length} chars");

        var matches = S3BatteryRegex.Matches(content);
        Logger.Log($"[Razer] Synapse 3: found {matches.Count} _OnBatteryLevelChanged entries");

        if (matches.Count > 0)
        {
            var deviceMap = new Dictionary<string, DeviceInfo>(StringComparer.OrdinalIgnoreCase);
            foreach (Match m in matches)
            {
                var name = m.Groups["name"].Value.Trim();
                var level = int.Parse(m.Groups["level"].Value);
                var charging = m.Groups["isCharging"].Value != "0";

                Logger.Log($"[Razer] S3 battery entry: name=\"{name}\" level={level} charging={charging}");

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

            Logger.Log($"[Razer] S3 result: {deviceMap.Count} unique device(s)");
            _lastKnown.Clear();
            _lastKnown.AddRange(deviceMap.Values);
            return _lastKnown.ToList();
        }

        var simpleMatches = S3SimpleBatteryRegex.Matches(content);
        if (simpleMatches.Count > 0)
        {
            Logger.Log($"[Razer] S3 simple fallback: {simpleMatches.Count} level/state entries");
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

        Logger.Log("[Razer] S3 log: no battery data found");
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
        catch (Exception ex)
        {
            Logger.Log($"[Razer] Failed to read log file {Path.GetFileName(path)}: {ex.Message}");
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
}
