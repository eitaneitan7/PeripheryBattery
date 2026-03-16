using PeripheryBattery.Models;

namespace PeripheryBattery.Providers;

public interface IDeviceProvider
{
    string Vendor { get; }
    Task<List<DeviceInfo>> GetDevicesAsync(CancellationToken ct);
    Task StartAsync(CancellationToken ct);
    Task StopAsync();
}
