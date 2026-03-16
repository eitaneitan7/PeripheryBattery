using Microsoft.Win32;
using PeripheryBattery.Config;
using PeripheryBattery.Models;
using PeripheryBattery.Services;
using PeripheryBattery.Utils;

namespace PeripheryBattery;

/// <summary>
/// Main application class. Sets up the tray icon, context menu, popup,
/// and wires everything together. The tray icon cycles through all connected
/// devices, showing each one's battery % with a color-coded indicator.
/// </summary>
public class TrayApp : ApplicationContext
{
    private readonly NotifyIcon _trayIcon;
    private readonly DeviceManager _deviceManager;
    private readonly NotificationService _notificationService;
    private readonly BatteryPopup _popup;
    private readonly AppConfig _config;

    // Icon cycling state
    private System.Windows.Forms.Timer? _cycleTimer;
    private int _currentDeviceIndex;
    private List<DeviceInfo> _connectedDevices = new();

    private int CycleIntervalMs => _config.IconCycleSeconds * 1000;

    public TrayApp()
    {
        _config = AppConfig.Load();
        Logger.Log("=== PeripheryBattery starting ===");
        Logger.Log($"Config: poll={_config.PollIntervalSeconds}s, lowBattery={_config.LowBatteryThreshold}%");

        // Apply auto-start setting
        ApplyAutoStart(_config.StartWithWindows);

        // Create tray icon
        _trayIcon = new NotifyIcon
        {
            Icon = CreateTextIcon("--", Color.Gray),
            Text = "PeripheryBattery - Loading...",
            Visible = true,
            ContextMenuStrip = BuildContextMenu(),
        };

        _trayIcon.MouseClick += OnTrayClick;

        // Create popup
        _popup = new BatteryPopup();

        // Create device manager
        _deviceManager = new DeviceManager(_config);
        _deviceManager.DevicesUpdated += OnDevicesUpdated;

        // Create notification service
        _notificationService = new NotificationService(_config, _trayIcon);

        // Set up icon cycling timer (runs on UI thread)
        _cycleTimer = new System.Windows.Forms.Timer { Interval = Math.Max(CycleIntervalMs, 1000) };
        _cycleTimer.Tick += OnCycleTick;

        // Start polling
        _ = StartAsync();
    }

    private async Task StartAsync()
    {
        try
        {
            await _deviceManager.StartAsync();
        }
        catch (Exception ex)
        {
            Logger.Log($"[TrayApp] Startup error: {ex.Message}");
            _trayIcon.Text = "PeripheryBattery - Error";
        }
    }

    private void OnTrayClick(object? sender, MouseEventArgs e)
    {
        if (e.Button == MouseButtons.Left)
        {
            if (_popup.Visible)
                _popup.Hide();
            else
            {
                _popup.UpdateDevices(_deviceManager.Devices);
                _popup.ShowNearTray();
            }
        }
    }

    private void OnDevicesUpdated()
    {
        try
        {
            // Update tooltip with summary
            var lines = _deviceManager.Devices
                .Select(d => $"{d.DisplayName}: {d.StatusText}")
                .ToList();

            var tooltip = lines.Count > 0
                ? string.Join("\n", lines)
                : "No devices found";

            // NotifyIcon.Text max is 127 chars
            if (tooltip.Length > 127)
                tooltip = tooltip[..124] + "...";

            _trayIcon.Text = tooltip;

            // Update connected devices list for cycling
            _connectedDevices = _deviceManager.Devices
                .Where(d => d.Connected && d.BatteryPercent.HasValue)
                .ToList();

            if (_connectedDevices.Count == 0)
            {
                _cycleTimer?.Stop();
                _trayIcon.Icon = CreateTextIcon("--", Color.Gray);
            }
            else if (_connectedDevices.Count == 1)
            {
                // Only one device — no need to cycle
                _cycleTimer?.Stop();
                var d = _connectedDevices[0];
                _trayIcon.Icon = CreateTextIcon(d.BatteryPercent!.Value.ToString(), GetBatteryColor(d));
            }
            else
            {
                // Multiple devices — start cycling
                _currentDeviceIndex = 0;
                ShowCurrentDevice();
                if (!_cycleTimer!.Enabled)
                    _cycleTimer.Start();
            }

            // Update popup if visible
            if (_popup.Visible)
                _popup.UpdateDevices(_deviceManager.Devices);

            // Check for low battery notifications
            _notificationService.CheckDevices(_deviceManager.Devices);
        }
        catch (Exception ex)
        {
            Logger.Log($"[TrayApp] UI update error: {ex.Message}");
        }
    }

    private void OnCycleTick(object? sender, EventArgs e)
    {
        if (_connectedDevices.Count <= 1)
        {
            _cycleTimer?.Stop();
            return;
        }

        _currentDeviceIndex = (_currentDeviceIndex + 1) % _connectedDevices.Count;
        ShowCurrentDevice();
    }

    private void ShowCurrentDevice()
    {
        if (_currentDeviceIndex >= _connectedDevices.Count) return;

        var device = _connectedDevices[_currentDeviceIndex];
        var color = GetBatteryColor(device);
        var text = device.BatteryPercent!.Value.ToString();

        _trayIcon.Icon = CreateTextIcon(text, color);
    }

    private static Color GetBatteryColor(DeviceInfo device)
    {
        if (device.Charging) return Color.FromArgb(100, 200, 255); // Cyan
        return device.BatteryPercent switch
        {
            <= 10 => Color.FromArgb(255, 60, 60),    // Red
            <= 20 => Color.FromArgb(255, 160, 50),    // Orange
            <= 50 => Color.FromArgb(255, 220, 50),    // Yellow
            _ => Color.FromArgb(80, 220, 80),          // Green
        };
    }

    private ContextMenuStrip BuildContextMenu()
    {
        var menu = new ContextMenuStrip();

        menu.Items.Add("Refresh Now", null, async (_, _) =>
        {
            await _deviceManager.PollAsync();
        });

        menu.Items.Add(new ToolStripSeparator());

        menu.Items.Add("Open Log File", null, (_, _) =>
        {
            try { System.Diagnostics.Process.Start("notepad.exe", Logger.LogFilePath); }
            catch { }
        });

        menu.Items.Add("Open Config", null, (_, _) =>
        {
            try { System.Diagnostics.Process.Start("notepad.exe", AppConfig.ConfigFilePath); }
            catch { }
        });

        menu.Items.Add(new ToolStripSeparator());

        menu.Items.Add("Exit", null, (_, _) =>
        {
            _cycleTimer?.Stop();
            _trayIcon.Visible = false;
            _deviceManager.Dispose();
            Application.Exit();
        });

        return menu;
    }

    /// <summary>
    /// Creates a 16x16 icon with battery percentage text, color-coded by level.
    /// </summary>
    private static Icon CreateTextIcon(string text, Color color)
    {
        var bitmap = new Bitmap(16, 16);
        using var g = Graphics.FromImage(bitmap);

        g.Clear(Color.Transparent);
        g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.SingleBitPerPixelGridFit;

        // Pick font size based on text length
        var fontSize = text.Length switch
        {
            1 => 11f,
            2 => 9f,
            3 => 7f,
            _ => 6f,
        };

        using var font = new Font("Segoe UI", fontSize, FontStyle.Bold, GraphicsUnit.Pixel);
        var size = g.MeasureString(text, font);
        var x = (16 - size.Width) / 2;
        var y = (16 - size.Height) / 2;

        // Draw shadow for readability on any background
        using var shadowBrush = new SolidBrush(Color.FromArgb(180, 0, 0, 0));
        g.DrawString(text, font, shadowBrush, x + 1, y + 1);

        // Draw text in battery color
        using var textBrush = new SolidBrush(color);
        g.DrawString(text, font, textBrush, x, y);

        var handle = bitmap.GetHicon();
        return Icon.FromHandle(handle);
    }

    /// <summary>
    /// Adds or removes the app from Windows startup via registry.
    /// </summary>
    private static void ApplyAutoStart(bool enable)
    {
        const string keyName = "PeripheryBattery";
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(
                @"SOFTWARE\Microsoft\Windows\CurrentVersion\Run", writable: true);
            if (key == null) return;

            if (enable)
            {
                var exePath = Application.ExecutablePath;
                key.SetValue(keyName, $"\"{exePath}\"");
                Logger.Log($"[AutoStart] Enabled: {exePath}");
            }
            else
            {
                if (key.GetValue(keyName) != null)
                {
                    key.DeleteValue(keyName, throwOnMissingValue: false);
                    Logger.Log("[AutoStart] Disabled");
                }
            }
        }
        catch (Exception ex)
        {
            Logger.Log($"[AutoStart] Failed: {ex.Message}");
        }
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _cycleTimer?.Stop();
            _cycleTimer?.Dispose();
            _trayIcon.Visible = false;
            _trayIcon.Dispose();
            _deviceManager.Dispose();
            _popup.Dispose();
        }
        base.Dispose(disposing);
    }
}
