using PeripheryBattery.Config;
using PeripheryBattery.Models;
using PeripheryBattery.Utils;

namespace PeripheryBattery.Services;

/// <summary>
/// Tracks device battery levels and fires low-battery notifications.
/// Avoids spamming by only notifying once per device per threshold crossing.
/// </summary>
public class NotificationService
{
    private readonly AppConfig _config;
    private readonly HashSet<string> _notifiedDevices = new();
    private readonly NotifyIcon _trayIcon;

    public NotificationService(AppConfig config, NotifyIcon trayIcon)
    {
        _config = config;
        _trayIcon = trayIcon;
    }

    public void CheckDevices(List<DeviceInfo> devices)
    {
        if (!_config.ShowNotifications) return;

        foreach (var device in devices)
        {
            if (!device.Connected || device.BatteryPercent == null) continue;

            if (device.BatteryPercent <= _config.LowBatteryThreshold && !device.Charging)
            {
                if (_notifiedDevices.Add(device.Id))
                {
                    Logger.Log($"[Notify] Low battery: {device.DisplayName} at {device.BatteryPercent}%");
                    _trayIcon.ShowBalloonTip(
                        5000,
                        "Low Battery",
                        $"{device.DisplayName}: {device.BatteryPercent}%",
                        ToolTipIcon.Warning);
                }
            }
            else if (device.BatteryPercent > _config.LowBatteryThreshold)
            {
                // Reset notification flag when battery recovers
                _notifiedDevices.Remove(device.Id);
            }
        }
    }
}
