using PeripheryBattery.Config;
using PeripheryBattery.Services;
using PeripheryBattery.Utils;

namespace PeripheryBattery;

/// <summary>
/// Main application class. Sets up the tray icon, context menu, popup,
/// and wires everything together.
/// </summary>
public class TrayApp : ApplicationContext
{
    private readonly NotifyIcon _trayIcon;
    private readonly DeviceManager _deviceManager;
    private readonly NotificationService _notificationService;
    private readonly BatteryPopup _popup;
    private readonly AppConfig _config;

    public TrayApp()
    {
        _config = AppConfig.Load();
        Logger.Log("=== PeripheryBattery starting ===");
        Logger.Log($"Config: poll={_config.PollIntervalSeconds}s, lowBattery={_config.LowBatteryThreshold}%");

        // Create tray icon
        _trayIcon = new NotifyIcon
        {
            Icon = CreateTextIcon("--"),
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

            // Update tray icon with lowest battery %
            var lowestBattery = _deviceManager.Devices
                .Where(d => d.Connected && d.BatteryPercent.HasValue)
                .MinBy(d => d.BatteryPercent!.Value);

            var iconText = lowestBattery?.BatteryPercent?.ToString() ?? "--";
            _trayIcon.Icon = CreateTextIcon(iconText);

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

    private ContextMenuStrip BuildContextMenu()
    {
        var menu = new ContextMenuStrip();

        menu.Items.Add("Refresh Now", null, async (_, _) =>
        {
            await _deviceManager.PollAsync();
        });

        menu.Items.Add(new ToolStripSeparator());

        menu.Items.Add($"Open Log File", null, (_, _) =>
        {
            try { System.Diagnostics.Process.Start("notepad.exe", Logger.LogFilePath); }
            catch { }
        });

        menu.Items.Add($"Open Config", null, (_, _) =>
        {
            try { System.Diagnostics.Process.Start("notepad.exe", AppConfig.ConfigFilePath); }
            catch { }
        });

        menu.Items.Add(new ToolStripSeparator());

        menu.Items.Add("Exit", null, (_, _) =>
        {
            _trayIcon.Visible = false;
            _deviceManager.Dispose();
            Application.Exit();
        });

        return menu;
    }

    /// <summary>
    /// Creates a small icon with battery percentage text rendered on it.
    /// Shows the number directly in the system tray for quick visibility.
    /// </summary>
    private static Icon CreateTextIcon(string text)
    {
        var bitmap = new Bitmap(16, 16);
        using var g = Graphics.FromImage(bitmap);

        g.Clear(Color.Transparent);
        g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.SingleBitPerPixelGridFit;

        // Pick font size based on text length
        var fontSize = text.Length switch
        {
            1 => 10f,
            2 => 8f,
            3 => 6.5f,
            _ => 6f,
        };

        using var font = new Font("Segoe UI", fontSize, FontStyle.Bold, GraphicsUnit.Pixel);
        var size = g.MeasureString(text, font);
        var x = (16 - size.Width) / 2;
        var y = (16 - size.Height) / 2;

        // Draw text with slight shadow for readability
        using var shadowBrush = new SolidBrush(Color.FromArgb(128, 0, 0, 0));
        g.DrawString(text, font, shadowBrush, x + 1, y + 1);
        using var textBrush = new SolidBrush(Color.White);
        g.DrawString(text, font, textBrush, x, y);

        var handle = bitmap.GetHicon();
        return Icon.FromHandle(handle);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _trayIcon.Visible = false;
            _trayIcon.Dispose();
            _deviceManager.Dispose();
            _popup.Dispose();
        }
        base.Dispose(disposing);
    }
}
