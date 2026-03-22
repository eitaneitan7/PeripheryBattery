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
        Logger.Enabled = _config.EnableLogging;
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
        if (device.Charging) return Color.FromArgb(130, 190, 220); // Soft blue
        return device.BatteryPercent switch
        {
            <= 10 => Color.FromArgb(210, 90, 90),     // Muted red
            <= 20 => Color.FromArgb(210, 150, 80),     // Warm amber
            <= 50 => Color.FromArgb(200, 190, 110),    // Soft gold
            _ => Color.FromArgb(110, 190, 130),         // Soft green
        };
    }

    private ContextMenuStrip BuildContextMenu()
    {
        var menu = new ContextMenuStrip();

        menu.Items.Add("Open Log", null, (_, _) =>
        {
            try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("notepad.exe", $"\"{Logger.LogFilePath}\"") { UseShellExecute = true }); }
            catch (Exception ex) { Logger.Log($"[TrayApp] Failed to open log: {ex.Message}"); }
        });

        menu.Items.Add("Open Config", null, (_, _) =>
        {
            try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("notepad.exe", $"\"{AppConfig.ConfigFilePath}\"") { UseShellExecute = true }); }
            catch (Exception ex) { Logger.Log($"[TrayApp] Failed to open config: {ex.Message}"); }
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
    /// Creates a high-quality tray icon by rendering at 64x64 with anti-aliasing,
    /// then scaling down. This produces much crisper text than rendering at 16x16.
    /// </summary>
    private static Icon CreateTextIcon(string text, Color color)
    {
        const int renderSize = 128;
        const int iconSize = 64;

        using var renderBitmap = new Bitmap(renderSize, renderSize);
        using (var g = Graphics.FromImage(renderBitmap))
        {
            g.Clear(Color.Transparent);
            g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.HighQuality;
            g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.AntiAliasGridFit;

            var fontSize = text.Length switch
            {
                1 => 96f,
                2 => 80f,
                3 => 58f,
                _ => 48f,
            };

            using var font = new Font("Segoe UI", fontSize, FontStyle.Bold, GraphicsUnit.Pixel);

            // Measure with StringFormat for precise centering
            var sf = new StringFormat
            {
                Alignment = StringAlignment.Center,
                LineAlignment = StringAlignment.Center,
            };
            var rect = new RectangleF(0, 0, renderSize, renderSize);

            // Subtle dark glow behind text for readability on light/dark taskbars
            using var shadowBrush = new SolidBrush(Color.FromArgb(120, 0, 0, 0));
            for (int dx = -2; dx <= 2; dx++)
                for (int dy = -2; dy <= 2; dy++)
                    if (dx != 0 || dy != 0)
                    {
                        var shadowRect = rect;
                        shadowRect.Offset(dx * 2, dy * 2);
                        g.DrawString(text, font, shadowBrush, shadowRect, sf);
                    }

            // Draw main text
            using var textBrush = new SolidBrush(color);
            g.DrawString(text, font, textBrush, rect, sf);
        }

        // Scale down with high quality interpolation
        using var iconBitmap = new Bitmap(iconSize, iconSize);
        using (var g = Graphics.FromImage(iconBitmap))
        {
            g.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBicubic;
            g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.HighQuality;
            g.DrawImage(renderBitmap, 0, 0, iconSize, iconSize);
        }

        return Icon.FromHandle(iconBitmap.GetHicon());
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
