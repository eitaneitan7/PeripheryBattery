using PeripheryBattery.Models;

namespace PeripheryBattery;

/// <summary>
/// Compact borderless popup that shows battery status for all devices.
/// Appears near the system tray when the tray icon is clicked.
/// </summary>
public class BatteryPopup : Form
{
    private readonly TableLayoutPanel _table;
    private readonly Label _titleLabel;

    public BatteryPopup()
    {
        // Borderless, compact, tool-window style (doesn't show in taskbar/alt-tab)
        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar = false;
        TopMost = true;
        StartPosition = FormStartPosition.Manual;
        AutoSize = true;
        AutoSizeMode = AutoSizeMode.GrowAndShrink;
        Padding = new Padding(12);
        BackColor = Color.FromArgb(30, 30, 30);
        ForeColor = Color.White;
        MaximumSize = new Size(450, 500);

        // Round corners
        Region = null; // Will set after size is determined

        _titleLabel = new Label
        {
            Text = "Periphery Battery",
            Font = new Font("Segoe UI", 11, FontStyle.Bold),
            ForeColor = Color.FromArgb(200, 200, 200),
            AutoSize = true,
            Margin = new Padding(0, 0, 0, 8),
        };

        _table = new TableLayoutPanel
        {
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            ColumnCount = 3,
            Padding = new Padding(0),
            Margin = new Padding(0),
        };
        _table.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize)); // icon/vendor
        _table.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize)); // device name
        _table.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize)); // battery %

        var container = new FlowLayoutPanel
        {
            FlowDirection = FlowDirection.TopDown,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            WrapContents = false,
            Padding = new Padding(0),
            Margin = new Padding(0),
            BackColor = Color.Transparent,
        };
        container.Controls.Add(_titleLabel);
        container.Controls.Add(_table);

        Controls.Add(container);

        // Close when clicked outside
        Deactivate += (_, _) => Hide();
    }

    public void UpdateDevices(List<DeviceInfo> devices)
    {
        if (InvokeRequired)
        {
            Invoke(() => UpdateDevices(devices));
            return;
        }

        _table.Controls.Clear();
        _table.RowStyles.Clear();
        _table.RowCount = 0;

        if (devices.Count == 0)
        {
            AddRow("", "No devices found", "", Color.Gray);
            return;
        }

        foreach (var device in devices)
        {
            var icon = device.DeviceType switch
            {
                "Mouse" => "\U0001F5B1",      // 🖱
                "Keyboard" => "\u2328",         // ⌨
                "Headset" => "\U0001F3A7",     // 🎧
                _ => "\U0001F50B",              // 🔋
            };

            var batteryColor = GetBatteryColor(device);
            var statusText = device.StatusText;

            AddRow(icon, device.DisplayName, statusText, batteryColor);
        }
    }

    private void AddRow(string icon, string name, string status, Color statusColor)
    {
        var row = _table.RowCount;
        _table.RowCount++;
        _table.RowStyles.Add(new RowStyle(SizeType.AutoSize));

        var iconLabel = new Label
        {
            Text = icon,
            Font = new Font("Segoe UI Emoji", 12),
            AutoSize = true,
            Margin = new Padding(0, 4, 8, 4),
            ForeColor = Color.White,
            BackColor = Color.Transparent,
        };

        // Truncate long device names to keep the popup compact
        var shortName = name.Length > 25 ? name[..25] + "…" : name;
        var nameLabel = new Label
        {
            Text = shortName,
            Font = new Font("Segoe UI", 10),
            AutoSize = true,
            Margin = new Padding(0, 6, 16, 4),
            ForeColor = Color.FromArgb(220, 220, 220),
            BackColor = Color.Transparent,
        };

        var statusLabel = new Label
        {
            Text = status,
            Font = new Font("Segoe UI", 11, FontStyle.Bold),
            AutoSize = true,
            Margin = new Padding(0, 5, 0, 4),
            ForeColor = statusColor,
            BackColor = Color.Transparent,
            TextAlign = ContentAlignment.MiddleRight,
        };

        _table.Controls.Add(iconLabel, 0, row);
        _table.Controls.Add(nameLabel, 1, row);
        _table.Controls.Add(statusLabel, 2, row);
    }

    private static Color GetBatteryColor(DeviceInfo device)
    {
        if (!device.Connected) return Color.Gray;
        if (device.Error != null) return Color.FromArgb(255, 100, 100);
        if (device.BatteryPercent == null) return Color.Gray;
        if (device.Charging) return Color.FromArgb(100, 200, 255);
        return device.BatteryPercent switch
        {
            <= 10 => Color.FromArgb(255, 60, 60),   // Red
            <= 20 => Color.FromArgb(255, 160, 50),   // Orange
            <= 50 => Color.FromArgb(255, 220, 50),   // Yellow
            _ => Color.FromArgb(80, 220, 80),         // Green
        };
    }

    /// <summary>
    /// Positions the popup near the system tray (bottom-right of screen).
    /// </summary>
    public void ShowNearTray()
    {
        var workArea = Screen.PrimaryScreen!.WorkingArea;

        // Force layout so we get accurate size
        PerformLayout();
        var popupSize = PreferredSize;

        var x = workArea.Right - popupSize.Width - 8;
        var y = workArea.Bottom - popupSize.Height - 8;

        Location = new Point(x, y);
        Show();
        Activate();
    }

    protected override CreateParams CreateParams
    {
        get
        {
            // WS_EX_TOOLWINDOW: hides from alt-tab
            // WS_EX_TOPMOST: always on top
            var cp = base.CreateParams;
            cp.ExStyle |= 0x00000080; // WS_EX_TOOLWINDOW
            return cp;
        }
    }
}
