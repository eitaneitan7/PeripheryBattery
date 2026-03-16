using PeripheryBattery.Models;

namespace PeripheryBattery;

/// <summary>
/// Compact borderless popup that shows battery status for all devices.
/// Appears near the system tray when the tray icon is clicked.
/// Dark themed with color-coded battery bars.
/// </summary>
public class BatteryPopup : Form
{
    private readonly Panel _contentPanel;

    public BatteryPopup()
    {
        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar = false;
        TopMost = true;
        StartPosition = FormStartPosition.Manual;
        BackColor = Color.FromArgb(28, 28, 32);
        ForeColor = Color.White;
        Size = new Size(280, 10); // Width fixed, height grows
        AutoSize = false;
        DoubleBuffered = true;

        _contentPanel = new Panel
        {
            Dock = DockStyle.Fill,
            Padding = new Padding(14, 12, 14, 12),
            AutoSize = false,
            BackColor = Color.Transparent,
        };
        Controls.Add(_contentPanel);

        Deactivate += (_, _) => Hide();
    }

    public void UpdateDevices(List<DeviceInfo> devices)
    {
        if (InvokeRequired)
        {
            Invoke(() => UpdateDevices(devices));
            return;
        }

        _contentPanel.Controls.Clear();

        var y = 12; // top padding

        // Title
        var title = new Label
        {
            Text = "Periphery Battery",
            Font = new Font("Segoe UI", 10, FontStyle.Bold),
            ForeColor = Color.FromArgb(160, 160, 170),
            Location = new Point(14, y),
            AutoSize = true,
        };
        _contentPanel.Controls.Add(title);
        y += 26;

        // Separator line
        var sep = new Panel
        {
            BackColor = Color.FromArgb(55, 55, 60),
            Location = new Point(14, y),
            Size = new Size(252, 1),
        };
        _contentPanel.Controls.Add(sep);
        y += 10;

        if (devices.Count == 0)
        {
            var noDevices = new Label
            {
                Text = "No devices found",
                Font = new Font("Segoe UI", 9),
                ForeColor = Color.Gray,
                Location = new Point(14, y),
                AutoSize = true,
            };
            _contentPanel.Controls.Add(noDevices);
            y += 28;
        }
        else
        {
            foreach (var device in devices)
            {
                y = AddDeviceRow(device, y);
            }
        }

        y += 4; // bottom padding
        Height = y;
    }

    private int AddDeviceRow(DeviceInfo device, int y)
    {
        var icon = device.DeviceType switch
        {
            "Mouse" => "\U0001F5B1",
            "Keyboard" => "\u2328\uFE0F",
            "Headset" => "\U0001F3A7",
            _ => "\U0001F50B",
        };

        // Short display name
        var name = device.DisplayName;
        if (name.Length > 22) name = name[..22] + "…";

        var batteryColor = GetBatteryColor(device);
        var statusText = device.StatusText;

        // Row: [icon] [name]              [status]
        var iconLabel = new Label
        {
            Text = icon,
            Font = new Font("Segoe UI Emoji", 11),
            Location = new Point(14, y),
            Size = new Size(28, 24),
            ForeColor = Color.White,
            BackColor = Color.Transparent,
        };
        _contentPanel.Controls.Add(iconLabel);

        var nameLabel = new Label
        {
            Text = name,
            Font = new Font("Segoe UI", 9.5f),
            Location = new Point(42, y + 3),
            AutoSize = true,
            ForeColor = Color.FromArgb(210, 210, 215),
            BackColor = Color.Transparent,
        };
        _contentPanel.Controls.Add(nameLabel);

        var statusLabel = new Label
        {
            Text = statusText,
            Font = new Font("Segoe UI", 10.5f, FontStyle.Bold),
            ForeColor = batteryColor,
            BackColor = Color.Transparent,
            TextAlign = ContentAlignment.MiddleRight,
            Location = new Point(200, y + 2),
            Size = new Size(66, 22),
        };
        _contentPanel.Controls.Add(statusLabel);

        y += 26;

        // Battery bar (only if connected with known percentage)
        if (device.Connected && device.BatteryPercent.HasValue)
        {
            var barBg = new Panel
            {
                BackColor = Color.FromArgb(50, 50, 55),
                Location = new Point(42, y),
                Size = new Size(224, 4),
            };
            _contentPanel.Controls.Add(barBg);

            var barWidth = (int)(224 * device.BatteryPercent.Value / 100.0);
            if (barWidth > 0)
            {
                var barFill = new Panel
                {
                    BackColor = batteryColor,
                    Location = new Point(42, y),
                    Size = new Size(barWidth, 4),
                };
                _contentPanel.Controls.Add(barFill);
                barFill.BringToFront();
            }

            y += 12;
        }
        else
        {
            y += 4;
        }

        return y;
    }

    private static Color GetBatteryColor(DeviceInfo device)
    {
        if (!device.Connected) return Color.FromArgb(100, 100, 105);
        if (device.Error != null) return Color.FromArgb(255, 100, 100);
        if (device.BatteryPercent == null) return Color.FromArgb(100, 100, 105);
        if (device.Charging) return Color.FromArgb(100, 200, 255);
        return device.BatteryPercent switch
        {
            <= 10 => Color.FromArgb(255, 60, 60),
            <= 20 => Color.FromArgb(255, 160, 50),
            <= 50 => Color.FromArgb(255, 220, 50),
            _ => Color.FromArgb(80, 220, 80),
        };
    }

    public void ShowNearTray()
    {
        var workArea = Screen.PrimaryScreen!.WorkingArea;

        var x = workArea.Right - Width - 12;
        var y = workArea.Bottom - Height - 12;

        Location = new Point(x, y);
        Show();
        Activate();
    }

    protected override CreateParams CreateParams
    {
        get
        {
            var cp = base.CreateParams;
            cp.ExStyle |= 0x00000080; // WS_EX_TOOLWINDOW
            return cp;
        }
    }
}
