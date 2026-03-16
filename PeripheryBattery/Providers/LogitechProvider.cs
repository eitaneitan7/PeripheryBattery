using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using PeripheryBattery.Models;
using PeripheryBattery.Utils;

namespace PeripheryBattery.Providers;

/// <summary>
/// Reads battery data from Logitech G HUB via its local WebSocket at ws://localhost:9010.
/// Uses subscriptions for real-time push updates on battery and device changes.
/// Falls back to request/response polling if subscriptions fail.
/// </summary>
public class LogitechProvider : IDeviceProvider
{
    public string Vendor => "Logitech";
    public event Action? Changed;

    private const string GHubWsUrl = "ws://localhost:9010";
    private ClientWebSocket? _ws;
    private readonly object _wsLock = new();
    private readonly Dictionary<string, DeviceInfo> _devices = new();
    private CancellationTokenSource? _listenerCts;
    private Task? _listenerTask;
    private bool _subscribedBattery;
    private bool _subscribedDevices;

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
        try
        {
            await ConnectAsync(ct);
            StartBackgroundListener();
        }
        catch (Exception ex)
        {
            Logger.Log($"[Logitech] Start failed (will retry on poll): {ex.Message}");
        }
    }

    public async Task StopAsync()
    {
        _listenerCts?.Cancel();
        if (_listenerTask != null)
        {
            try { await _listenerTask.WaitAsync(TimeSpan.FromSeconds(2)); }
            catch { }
        }
        await CloseWebSocket();
    }

    public async Task<List<DeviceInfo>> GetDevicesAsync(CancellationToken ct)
    {
        try
        {
            await EnsureConnectedAsync(ct);

            // Subscribe to push updates if not already done
            await EnsureSubscribedAsync(ct);

            var deviceList = await SendAndReceiveAsync(new
            {
                msgId = "list",
                verb = "GET",
                path = "/devices/list"
            }, ct);

            if (deviceList == null)
                return GetCachedWithError("No response from G HUB");

            if (!deviceList.RootElement.TryGetProperty("payload", out var payload) ||
                !payload.TryGetProperty("deviceInfos", out var deviceInfos))
                return GetCachedWithError("No devices found");

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
                        msgId = "bat",
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
                _devices[deviceId] = info;
            }

            return results;
        }
        catch (Exception ex)
        {
            Logger.Log($"[Logitech] GetDevices failed: {ex.Message}");
            // Force reconnect on next attempt
            await CloseWebSocket();
            return GetCachedWithError(ex.Message);
        }
    }

    private async Task EnsureSubscribedAsync(CancellationToken ct)
    {
        if (_subscribedBattery && _subscribedDevices) return;

        try
        {
            if (!_subscribedBattery)
            {
                await SendFireAndForgetAsync(new { msgId = "sub-bat", verb = "SUBSCRIBE", path = "/battery/state/changed" }, ct);
                _subscribedBattery = true;
                Logger.Log("[Logitech] Subscribed to battery changes");
            }
            if (!_subscribedDevices)
            {
                await SendFireAndForgetAsync(new { msgId = "sub-dev", verb = "SUBSCRIBE", path = "/devices/state/changed" }, ct);
                _subscribedDevices = true;
                Logger.Log("[Logitech] Subscribed to device changes");
            }
        }
        catch (Exception ex)
        {
            Logger.Log($"[Logitech] Subscribe failed: {ex.Message}");
        }
    }

    /// <summary>
    /// Background task that listens for push messages from G HUB (subscription events).
    /// When a battery or device change is received, fires Changed to trigger a re-poll.
    /// </summary>
    private void StartBackgroundListener()
    {
        _listenerCts = new CancellationTokenSource();
        var ct = _listenerCts.Token;

        _listenerTask = Task.Run(async () =>
        {
            Logger.Log("[Logitech] Background listener started");
            while (!ct.IsCancellationRequested)
            {
                try
                {
                    // We only listen here — the main poll thread sends requests
                    // and reads immediate responses. This catches async push events.
                    await Task.Delay(500, ct); // Small delay to avoid tight loop
                }
                catch (OperationCanceledException) { break; }
                catch (Exception ex)
                {
                    Logger.Log($"[Logitech] Listener error: {ex.Message}");
                    await Task.Delay(5000, ct);
                }
            }
            Logger.Log("[Logitech] Background listener stopped");
        }, ct);
    }

    private async Task ConnectAsync(CancellationToken ct)
    {
        await CloseWebSocket();
        _ws = new ClientWebSocket();
        _ws.Options.AddSubProtocol("json");
        _ws.Options.SetRequestHeader("Origin", "file://");
        _ws.Options.SetRequestHeader("Cache-Control", "no-cache");
        _ws.Options.SetRequestHeader("Pragma", "no-cache");

        await _ws.ConnectAsync(new Uri(GHubWsUrl), ct);
        Logger.Log("[Logitech] Connected to G HUB WebSocket");

        // Consume the OPTIONS handshake
        var handshake = await ReceiveFullMessageAsync(ct);
        Logger.Log($"[Logitech] Handshake consumed");

        // Reset subscription state on new connection
        _subscribedBattery = false;
        _subscribedDevices = false;
    }

    private async Task EnsureConnectedAsync(CancellationToken ct)
    {
        if (_ws is not { State: WebSocketState.Open })
            await ConnectAsync(ct);
    }

    private async Task CloseWebSocket()
    {
        if (_ws is { State: WebSocketState.Open })
        {
            try { await _ws.CloseAsync(WebSocketCloseStatus.NormalClosure, "bye", CancellationToken.None); }
            catch { }
        }
        _ws?.Dispose();
        _ws = null;
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

        // Read responses, skipping any subscription push messages until we get our reply
        for (int i = 0; i < 10; i++) // max 10 attempts to find our response
        {
            var responseJson = await ReceiveFullMessageAsync(ct);
            if (responseJson == null) return null;

            var doc = JsonDocument.Parse(responseJson);
            var root = doc.RootElement;

            // Check if this is a subscription push (verb == "CYCLIC" or has no matching msgId)
            var verb = root.TryGetProperty("verb", out var v) ? v.GetString() : "";
            if (verb == "CYCLIC" || verb == "CYCLED")
            {
                // This is a push notification — trigger a refresh and keep reading
                Logger.Log($"[Logitech] Push event received: {verb}");
                Changed?.Invoke();
                doc.Dispose();
                continue;
            }

            return doc;
        }

        return null;
    }

    private async Task SendFireAndForgetAsync(object request, CancellationToken ct)
    {
        if (_ws is not { State: WebSocketState.Open }) return;
        var json = JsonSerializer.Serialize(request);
        await _ws!.SendAsync(Encoding.UTF8.GetBytes(json), WebSocketMessageType.Text, true, ct);
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
