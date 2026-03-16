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

    private async Task PollAsync()
    {
        var allDevices = new List<DeviceInfo>();

        foreach (var provider in _providers)
        {
            try
            {
                var devices = await provider.GetDevicesAsync(_cts?.Token ?? CancellationToken.None);
                foreach (var d in devices)
                {
                    d.DisplayName = _config.ApplyNameOverride(d.DisplayName);
                }
                allDevices.AddRange(devices);
            }
            catch (Exception ex)
            {
                Logger.Log($"[DeviceManager] Poll failed for {provider.Vendor}: {ex.Message}");
            }
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
