using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using PeripheryBattery.Models;
using PeripheryBattery.Utils;

namespace PeripheryBattery.Providers;

/// <summary>
/// Reads battery data from Logitech G HUB via its local WebSocket at ws://localhost:9010.
/// Simple request/response polling — no subscriptions, no background listener.
/// Reconnects automatically if the connection drops.
/// </summary>
public class LogitechProvider : IDeviceProvider
{
    public string Vendor => "Logitech";

    private const string GHubWsUrl = "ws://localhost:9010";
    private ClientWebSocket? _ws;
    private List<DeviceInfo> _lastGoodDevices = new();
    private int _consecutiveFailures;
    private bool _loggedUnavailable;

    private static readonly Dictionary<string, string> DeviceTypeKeywords = new(StringComparer.OrdinalIgnoreCase)
    {
        { "keyboard", "Keyboard" },
        { "mouse", "Mouse" },
        { "headset", "Headset" },
        { "headphone", "Headset" },
        { "speaker", "Speaker" },
    };

    public Task StartAsync(CancellationToken ct)
    {
        Logger.Log("[Logitech] Provider starting, will attempt G HUB WebSocket connection on first poll");
        return Task.CompletedTask;
    }

    public async Task StopAsync()
    {
        await CloseWebSocket();
    }

    public async Task<List<DeviceInfo>> GetDevicesAsync(CancellationToken ct)
    {
        // After 3 consecutive failures, assume G HUB isn't installed — stop trying
        if (_consecutiveFailures >= 3)
        {
            if (!_loggedUnavailable)
            {
                Logger.Log("[Logitech] G HUB not available, provider disabled (will not retry)");
                _loggedUnavailable = true;
            }
            return new List<DeviceInfo>();
        }

        try
        {
            // Fresh connection each poll
            await CloseWebSocket();
            await ConnectAsync(ct);

            var deviceList = await SendAndReceiveAsync(new
            {
                msgId = "",
                verb = "GET",
                path = "/devices/list"
            }, ct);

            if (deviceList == null ||
                !deviceList.RootElement.TryGetProperty("payload", out var payload) ||
                !payload.TryGetProperty("deviceInfos", out var deviceInfos))
            {
                Logger.Log("[Logitech] No device data from G HUB");
                return _lastGoodDevices.ToList();
            }

            var results = new List<DeviceInfo>();

            foreach (var dev in deviceInfos.EnumerateArray())
            {
                var deviceId = dev.TryGetProperty("id", out var idProp) ? idProp.GetString() ?? "" : "";
                var displayName = dev.TryGetProperty("extendedDisplayName", out var dn)
                    ? dn.GetString() ?? ""
                    : dev.TryGetProperty("displayName", out var dn2) ? dn2.GetString() ?? "" : "";

                var hasBattery = dev.TryGetProperty("capabilities", out var caps)
                    && caps.TryGetProperty("hasBatteryStatus", out var bs)
                    && bs.GetBoolean();

                if (!hasBattery) continue;

                var ghubType = dev.TryGetProperty("deviceType", out var dt) ? dt.GetString() ?? "" : "";
                var deviceType = ghubType.Length > 0
                    ? char.ToUpper(ghubType[0]) + ghubType[1..].ToLower()
                    : ClassifyDevice(displayName);

                var info = new DeviceInfo
                {
                    Id = deviceId,
                    Vendor = "Logitech",
                    DeviceType = deviceType,
                    DisplayName = displayName,
                    Connected = true,
                    Source = "GHubWebSocket",
                    LastUpdated = DateTime.Now,
                };

                try
                {
                    var batteryResp = await SendAndReceiveAsync(new
                    {
                        msgId = "",
                        verb = "GET",
                        path = $"/battery/{deviceId}/state"
                    }, ct);

                    if (batteryResp != null && batteryResp.RootElement.TryGetProperty("payload", out var bp))
                    {
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
            }

            _lastGoodDevices = results;
            _consecutiveFailures = 0;
            _loggedUnavailable = false;
            return results;
        }
        catch (Exception ex)
        {
            _consecutiveFailures++;
            if (_consecutiveFailures <= 3)
                Logger.Log($"[Logitech] GetDevices failed ({_consecutiveFailures}/3): {ex.Message}");
            return _lastGoodDevices.ToList();
        }
    }

    private async Task ConnectAsync(CancellationToken ct)
    {
        _ws = new ClientWebSocket();
        _ws.Options.AddSubProtocol("json");
        _ws.Options.SetRequestHeader("Origin", "file://");
        _ws.Options.SetRequestHeader("Cache-Control", "no-cache");
        _ws.Options.SetRequestHeader("Pragma", "no-cache");

        await _ws.ConnectAsync(new Uri(GHubWsUrl), ct);

        // Consume the OPTIONS handshake G HUB sends on connect
        await ReceiveFullMessageAsync(ct);
    }

    private async Task CloseWebSocket()
    {
        if (_ws != null)
        {
            try
            {
                if (_ws.State == WebSocketState.Open)
                    await _ws.CloseAsync(WebSocketCloseStatus.NormalClosure, "bye", CancellationToken.None);
            }
            catch { }
            _ws.Dispose();
            _ws = null;
        }
    }

    private async Task<string?> ReceiveFullMessageAsync(CancellationToken ct)
    {
        if (_ws is not { State: WebSocketState.Open }) return null;

        var buffer = new byte[16384];
        using var ms = new MemoryStream();

        WebSocketReceiveResult result;
        do
        {
            result = await _ws.ReceiveAsync(buffer, ct);
            if (result.MessageType == WebSocketMessageType.Close) return null;
            ms.Write(buffer, 0, result.Count);
        }
        while (!result.EndOfMessage);

        return Encoding.UTF8.GetString(ms.ToArray());
    }

    private async Task<JsonDocument?> SendAndReceiveAsync(object request, CancellationToken ct)
    {
        if (_ws is not { State: WebSocketState.Open }) return null;

        var json = JsonSerializer.Serialize(request);
        await _ws!.SendAsync(Encoding.UTF8.GetBytes(json), WebSocketMessageType.Text, true, ct);

        var responseJson = await ReceiveFullMessageAsync(ct);
        if (responseJson == null) return null;

        return JsonDocument.Parse(responseJson);
    }

    private static string ClassifyDevice(string name)
    {
        foreach (var (keyword, type) in DeviceTypeKeywords)
        {
            if (name.Contains(keyword, StringComparison.OrdinalIgnoreCase))
                return type;
        }
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
