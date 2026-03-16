using PeripheryBattery.Models;

namespace PeripheryBattery.Providers;

public interface IDeviceProvider
{
    string Vendor { get; }
    Task<List<DeviceInfo>> GetDevicesAsync(CancellationToken ct);
    Task StartAsync(CancellationToken ct);
    Task StopAsync();

    /// <summary>
    /// Fired when the provider detects a change (new device, battery update, etc.)
    /// DeviceManager listens to this to trigger an immediate re-poll.
    /// </summary>
    event Action? Changed;
}
