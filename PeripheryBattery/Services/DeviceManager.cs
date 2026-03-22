using PeripheryBattery.Config;
using PeripheryBattery.Models;
using PeripheryBattery.Providers;
using PeripheryBattery.Utils;

namespace PeripheryBattery.Services;

/// <summary>
/// Orchestrates all device providers with simple timer-based polling.
/// Exposes a unified device list via the DevicesUpdated event.
/// </summary>
public class DeviceManager : IDisposable
{
    private readonly List<IDeviceProvider> _providers = new();
    private readonly AppConfig _config;
    private System.Threading.Timer? _pollTimer;
    private CancellationTokenSource? _cts;

    public List<DeviceInfo> Devices { get; private set; } = new();
    public event Action? DevicesUpdated;

    public DeviceManager(AppConfig config)
    {
        _config = config;
        _providers.Add(new LogitechProvider());
        _providers.Add(new RazerProvider());
        _providers.Add(new CorsairProvider());
    }

    public async Task StartAsync()
    {
        _cts = new CancellationTokenSource();

        foreach (var provider in _providers)
        {
            try
            {
                await provider.StartAsync(_cts.Token);
                Logger.Log($"[DeviceManager] Started {provider.Vendor} provider");
            }
            catch (Exception ex)
            {
                Logger.Log($"[DeviceManager] Failed to start {provider.Vendor}: {ex.Message}");
            }
        }

        await PollAsync();

        _pollTimer = new System.Threading.Timer(
            async _ => await PollAsync(),
            null,
            TimeSpan.FromSeconds(_config.PollIntervalSeconds),
            TimeSpan.FromSeconds(_config.PollIntervalSeconds));
    }

    private int _pollCount;

    private async Task PollAsync()
    {
        _pollCount++;
        var isDetailedLog = _pollCount <= 3 || _pollCount % 60 == 0;
        var allDevices = new List<DeviceInfo>();

        foreach (var provider in _providers)
        {
            try
            {
                var devices = await provider.GetDevicesAsync(_cts?.Token ?? CancellationToken.None);

                if (isDetailedLog && devices.Count > 0)
                    Logger.Log($"[DeviceManager] {provider.Vendor} returned {devices.Count} device(s): [{string.Join(", ", devices.Select(d => $"{d.DisplayName}({d.BatteryPercent?.ToString() ?? "?"}%)"))}]");

                foreach (var d in devices)
                {
                    var original = d.DisplayName;
                    d.DisplayName = _config.ApplyNameOverride(d.DisplayName);
                    if (isDetailedLog && d.DisplayName != original)
                        Logger.Log($"[DeviceManager] Name override: \"{original}\" -> \"{d.DisplayName}\"");
                }
                allDevices.AddRange(devices);
            }
            catch (Exception ex)
            {
                Logger.Log($"[DeviceManager] Poll failed for {provider.Vendor}: {ex.Message}");
            }
        }

        // Log summary of all devices
        if (isDetailedLog)
        {
            Logger.Log($"[DeviceManager] Poll #{_pollCount} complete: {allDevices.Count} total device(s)");
            foreach (var d in allDevices)
                Logger.Log($"[DeviceManager]   [{d.Vendor}] {d.DisplayName}: {d.StatusText} (id={d.Id}, type={d.DeviceType}, source={d.Source})");

            // Warn about potential duplicates (same display name from different sources)
            var dupes = allDevices.GroupBy(d => d.DisplayName, StringComparer.OrdinalIgnoreCase)
                .Where(g => g.Count() > 1);
            foreach (var g in dupes)
                Logger.Log($"[DeviceManager] WARNING: Duplicate display name \"{g.Key}\" from sources: {string.Join(", ", g.Select(d => d.Source))}");
        }

        Devices = allDevices;

        try
        {
            DevicesUpdated?.Invoke();
        }
        catch (Exception ex)
        {
            Logger.Log($"[DeviceManager] UI update callback error: {ex.Message}");
        }
    }

    public void Dispose()
    {
        _pollTimer?.Dispose();
        _cts?.Cancel();
        foreach (var provider in _providers)
        {
            try { provider.StopAsync().Wait(TimeSpan.FromSeconds(2)); }
            catch { }
        }
        _cts?.Dispose();
    }
}
