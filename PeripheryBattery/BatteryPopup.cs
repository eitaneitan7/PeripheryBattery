using System.Drawing.Drawing2D;
using PeripheryBattery.Models;

namespace PeripheryBattery;

/// <summary>
/// Compact borderless popup with a warm dark gradient background,
/// rounded feel, and soft color palette.
/// </summary>
public class BatteryPopup : Form
{
    private readonly Panel _contentPanel;

    // Warm dark palette
    private static readonly Color BgTop = Color.FromArgb(36, 34, 40);
    private static readonly Color BgBottom = Color.FromArgb(28, 26, 32);
    private static readonly Color TextPrimary = Color.FromArgb(225, 220, 215);
    private static readonly Color TextSecondary = Color.FromArgb(160, 155, 150);
    private static readonly Color Divider = Color.FromArgb(58, 54, 62);
    private static readonly Color BarBackground = Color.FromArgb(48, 45, 52);

    public BatteryPopup()
    {
        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar = false;
        TopMost = true;
        StartPosition = FormStartPosition.Manual;
        BackColor = BgTop;
        ForeColor = TextPrimary;
        Size = new Size(280, 10);
        AutoSize = false;
        DoubleBuffered = true;

        _contentPanel = new Panel
        {
            Dock = DockStyle.Fill,
            Padding = new Padding(16, 14, 16, 14),
            AutoSize = false,
            BackColor = Color.Transparent,
        };
        Controls.Add(_contentPanel);

        Deactivate += (_, _) => Hide();
    }

    protected override void OnPaintBackground(PaintEventArgs e)
    {
        using var brush = new LinearGradientBrush(
            ClientRectangle, BgTop, BgBottom, LinearGradientMode.Vertical);
        e.Graphics.FillRectangle(brush, ClientRectangle);

        // Subtle top highlight line
        using var highlightPen = new Pen(Color.FromArgb(30, 255, 255, 255));
        e.Graphics.DrawLine(highlightPen, 0, 0, Width, 0);
    }

    public void UpdateDevices(List<DeviceInfo> devices)
    {
        if (InvokeRequired)
        {
            Invoke(() => UpdateDevices(devices));
            return;
        }

        _contentPanel.Controls.Clear();

        var y = 14;

        // Title
        var title = new Label
        {
            Text = "Periphery Battery",
            Font = new Font("Segoe UI", 9.5f, FontStyle.Regular),
            ForeColor = TextSecondary,
            Location = new Point(16, y),
            AutoSize = true,
            BackColor = Color.Transparent,
        };
        _contentPanel.Controls.Add(title);
        y += 24;

        // Separator
        var sep = new Panel
        {
            BackColor = Divider,
            Location = new Point(16, y),
            Size = new Size(248, 1),
        };
        _contentPanel.Controls.Add(sep);
        y += 14;

        if (devices.Count == 0)
        {
            var noDevices = new Label
            {
                Text = "No devices found",
                Font = new Font("Segoe UI", 9),
                ForeColor = TextSecondary,
                Location = new Point(16, y),
                AutoSize = true,
                BackColor = Color.Transparent,
            };
            _contentPanel.Controls.Add(noDevices);
            y += 30;
        }
        else
        {
            foreach (var device in devices)
            {
                y = AddDeviceRow(device, y);
            }
        }

        y += 8;
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

        var name = device.DisplayName;
        if (name.Length > 22) name = name[..22] + "…";

        var batteryColor = GetBatteryColor(device);
        var statusText = device.StatusText;

        // Icon
        var iconLabel = new Label
        {
            Text = icon,
            Font = new Font("Segoe UI Emoji", 11),
            Location = new Point(16, y),
            Size = new Size(26, 22),
            ForeColor = TextPrimary,
            BackColor = Color.Transparent,
        };
        _contentPanel.Controls.Add(iconLabel);

        // Device name
        var nameLabel = new Label
        {
            Text = name,
            Font = new Font("Segoe UI", 9.5f),
            Location = new Point(44, y + 2),
            AutoSize = true,
            ForeColor = TextPrimary,
            BackColor = Color.Transparent,
        };
        _contentPanel.Controls.Add(nameLabel);

        // Battery percentage (right-aligned)
        var statusLabel = new Label
        {
            Text = statusText,
            Font = new Font("Segoe UI Semibold", 10f),
            ForeColor = batteryColor,
            BackColor = Color.Transparent,
            TextAlign = ContentAlignment.MiddleRight,
            Location = new Point(200, y + 1),
            Size = new Size(64, 22),
        };
        _contentPanel.Controls.Add(statusLabel);

        y += 26;

        // Battery bar
        if (device.Connected && device.BatteryPercent.HasValue)
        {
            var barBg = new Panel
            {
                BackColor = BarBackground,
                Location = new Point(44, y),
                Size = new Size(220, 3),
            };
            _contentPanel.Controls.Add(barBg);

            var barWidth = (int)(220 * device.BatteryPercent.Value / 100.0);
            if (barWidth > 0)
            {
                var barFill = new BarPanel(batteryColor)
                {
                    Location = new Point(44, y),
                    Size = new Size(barWidth, 3),
                };
                _contentPanel.Controls.Add(barFill);
                barFill.BringToFront();
            }

            y += 16;
        }
        else
        {
            y += 6;
        }

        return y;
    }

    /// <summary>
    /// A tiny panel that draws itself with a subtle gradient for the battery bar.
    /// </summary>
    private class BarPanel : Panel
    {
        private readonly Color _color;
        public BarPanel(Color color) { _color = color; DoubleBuffered = true; }

        protected override void OnPaint(PaintEventArgs e)
        {
            if (Width <= 0 || Height <= 0) return;
            var lighter = ControlPaint.Light(_color, 0.3f);
            using var brush = new LinearGradientBrush(
                ClientRectangle, lighter, _color, LinearGradientMode.Horizontal);
            e.Graphics.FillRectangle(brush, ClientRectangle);
        }
    }

    private static Color GetBatteryColor(DeviceInfo device)
    {
        if (!device.Connected) return Color.FromArgb(90, 88, 95);
        if (device.Error != null) return Color.FromArgb(190, 95, 95);
        if (device.BatteryPercent == null) return Color.FromArgb(90, 88, 95);
        if (device.Charging) return Color.FromArgb(130, 185, 210);
        return device.BatteryPercent switch
        {
            <= 10 => Color.FromArgb(200, 100, 95),    // Warm red
            <= 20 => Color.FromArgb(200, 155, 85),     // Warm amber
            <= 50 => Color.FromArgb(195, 185, 115),    // Soft gold
            _ => Color.FromArgb(115, 185, 130),         // Sage green
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
