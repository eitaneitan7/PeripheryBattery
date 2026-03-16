using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using PeripheryBattery.Models;
using PeripheryBattery.Utils;

namespace PeripheryBattery.Providers;

/// <summary>
/// Reads battery data from Logitech G HUB via its local WebSocket at ws://localhost:9010.
/// Protocol is JSON request/response. Device list + per-device battery queries.
/// </summary>
public class LogitechProvider : IDeviceProvider
{
    public string Vendor => "Logitech";

    private const string GHubWsUrl = "ws://localhost:9010";
    private ClientWebSocket? _ws;
    private readonly Lock _wsLock = new();
    private readonly Dictionary<string, DeviceInfo> _devices = new();

    // Known device type keywords for classification
    private static readonly Dictionary<string, string> DeviceTypeKeywords = new(StringComparer.OrdinalIgnoreCase)
    {
        { "keyboard", "Keyboard" },
        { "mouse", "Mouse" },
        { "headset", "Headset" },
        { "headphone", "Headset" },
        { "speaker", "Speaker" },
    };

    public async Task StartAsync(CancellationToken ct)
    {
        await ConnectAsync(ct);
    }

    public async Task StopAsync()
    {
        if (_ws is { State: WebSocketState.Open })
        {
            try
            {
                await _ws.CloseAsync(WebSocketCloseStatus.NormalClosure, "bye", CancellationToken.None);
            }
            catch { /* best effort */ }
        }
        _ws?.Dispose();
        _ws = null;
    }

    public async Task<List<DeviceInfo>> GetDevicesAsync(CancellationToken ct)
    {
        try
        {
            await EnsureConnectedAsync(ct);
            var deviceList = await SendAndReceiveAsync(new
            {
                msgId = "",
                verb = "GET",
                path = "/devices/list"
            }, ct);

            if (deviceList == null)
            {
                Logger.Log("[Logitech] No response from G HUB device list");
                return GetCachedWithError("No response from G HUB");
            }

            var payload = deviceList.RootElement.GetProperty("payload");
            if (!payload.TryGetProperty("deviceInfos", out var deviceInfos))
            {
                Logger.Log("[Logitech] No deviceInfos in response");
                return GetCachedWithError("No devices found");
            }

            var results = new List<DeviceInfo>();

            foreach (var dev in deviceInfos.EnumerateArray())
            {
                var deviceId = dev.GetProperty("id").GetString() ?? "";
                var displayName = dev.TryGetProperty("extendedDisplayName", out var dn)
                    ? dn.GetString() ?? ""
                    : dev.GetProperty("displayName").GetString() ?? "";

                var hasBattery = dev.TryGetProperty("capabilities", out var caps)
                    && caps.TryGetProperty("hasBatteryStatus", out var bs)
                    && bs.GetBoolean();

                if (!hasBattery) continue;

                var info = new DeviceInfo
                {
                    Id = deviceId,
                    Vendor = "Logitech",
                    DeviceType = ClassifyDevice(displayName),
                    DisplayName = displayName,
                    Connected = true,
                    Source = "GHubWebSocket",
                    LastUpdated = DateTime.Now,
                };

                // Query battery for this device
                try
                {
                    var batteryResp = await SendAndReceiveAsync(new
                    {
                        msgId = "",
                        verb = "GET",
                        path = $"/battery/{deviceId}/state"
                    }, ct);

                    if (batteryResp != null)
                    {
                        var bp = batteryResp.RootElement.GetProperty("payload");
                        if (bp.TryGetProperty("percentage", out var pct))
                            info.BatteryPercent = pct.GetInt32();
                        if (bp.TryGetProperty("charging", out var chg))
                            info.Charging = chg.GetBoolean();
                    }
                }
                catch (Exception ex)
                {
                    Logger.Log($"[Logitech] Battery query failed for {displayName}: {ex.Message}");
                    info.Error = "Battery query failed";
                }

                results.Add(info);
                _devices[deviceId] = info;
            }

            return results;
        }
        catch (Exception ex)
        {
            Logger.Log($"[Logitech] GetDevices failed: {ex.Message}");
            return GetCachedWithError(ex.Message);
        }
    }

    private async Task ConnectAsync(CancellationToken ct)
    {
        _ws?.Dispose();
        _ws = new ClientWebSocket();
        _ws.Options.AddSubProtocol("json");
        _ws.Options.SetRequestHeader("Origin", "file://");
        _ws.Options.SetRequestHeader("Cache-Control", "no-cache");
        _ws.Options.SetRequestHeader("Pragma", "no-cache");

        await _ws.ConnectAsync(new Uri(GHubWsUrl), ct);
        Logger.Log("[Logitech] Connected to G HUB WebSocket");
    }

    private async Task EnsureConnectedAsync(CancellationToken ct)
    {
        if (_ws is not { State: WebSocketState.Open })
        {
            await ConnectAsync(ct);
        }
    }

    private async Task<JsonDocument?> SendAndReceiveAsync(object request, CancellationToken ct)
    {
        lock (_wsLock)
        {
            if (_ws is not { State: WebSocketState.Open })
                return null;
        }

        var json = JsonSerializer.Serialize(request);
        var bytes = Encoding.UTF8.GetBytes(json);
        await _ws!.SendAsync(bytes, WebSocketMessageType.Text, true, ct);

        var buffer = new byte[8192];
        var result = await _ws.ReceiveAsync(buffer, ct);

        if (result.MessageType == WebSocketMessageType.Close)
        {
            Logger.Log("[Logitech] WebSocket closed by server");
            return null;
        }

        var responseJson = Encoding.UTF8.GetString(buffer, 0, result.Count);
        return JsonDocument.Parse(responseJson);
    }

    private List<DeviceInfo> GetCachedWithError(string error)
    {
        foreach (var d in _devices.Values)
        {
            d.Error = error;
            d.Connected = false;
        }
        return _devices.Values.ToList();
    }

    private static string ClassifyDevice(string name)
    {
        foreach (var (keyword, type) in DeviceTypeKeywords)
        {
            if (name.Contains(keyword, StringComparison.OrdinalIgnoreCase))
                return type;
        }
        // Fallback: check common model patterns
        if (name.Contains("G915", StringComparison.OrdinalIgnoreCase) ||
            name.Contains("G815", StringComparison.OrdinalIgnoreCase) ||
            name.Contains("G715", StringComparison.OrdinalIgnoreCase))
            return "Keyboard";
        if (name.Contains("PRO X", StringComparison.OrdinalIgnoreCase) && name.Contains("LIGHT", StringComparison.OrdinalIgnoreCase))
            return "Headset";
        if (name.Contains("G502", StringComparison.OrdinalIgnoreCase) ||
            name.Contains("G PRO", StringComparison.OrdinalIgnoreCase) ||
            name.Contains("SUPERLIGHT", StringComparison.OrdinalIgnoreCase))
            return "Mouse";
        return "Unknown";
    }
}
