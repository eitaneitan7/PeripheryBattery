using System.Drawing.Drawing2D;
using PeripheryBattery.Models;

namespace PeripheryBattery;

/// <summary>
/// Compact borderless popup with warm dark gradient background.
/// Reuses controls on update to avoid flicker.
/// </summary>
public class BatteryPopup : Form
{
    private readonly Panel _contentPanel;
    private readonly Label _titleLabel;
    private readonly Panel _separator;
    private readonly Label _noDevicesLabel;
    private readonly List<DeviceRow> _rows = new();

    private static readonly Color BgTop = Color.FromArgb(36, 34, 40);
    private static readonly Color BgBottom = Color.FromArgb(28, 26, 32);
    private static readonly Color TextPrimary = Color.FromArgb(225, 220, 215);
    private static readonly Color TextSecondary = Color.FromArgb(160, 155, 150);
    private static readonly Color Divider = Color.FromArgb(58, 54, 62);
    private static readonly Color BarBackground = Color.FromArgb(48, 45, 52);

    private const int LeftMargin = 16;
    private const int IconX = 16;
    private const int NameX = 44;
    private const int StatusX = 200;
    private const int BarX = 44;
    private const int BarMaxWidth = 220;

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
            AutoSize = false,
            BackColor = Color.Transparent,
        };
        Controls.Add(_contentPanel);

        // Pre-create static controls
        _titleLabel = new Label
        {
            Text = "Periphery Battery",
            Font = new Font("Segoe UI", 9.5f, FontStyle.Regular),
            ForeColor = TextSecondary,
            Location = new Point(LeftMargin, 14),
            AutoSize = true,
            BackColor = Color.Transparent,
        };
        _contentPanel.Controls.Add(_titleLabel);

        _separator = new Panel
        {
            BackColor = Divider,
            Location = new Point(LeftMargin, 38),
            Size = new Size(248, 1),
        };
        _contentPanel.Controls.Add(_separator);

        _noDevicesLabel = new Label
        {
            Text = "No devices found",
            Font = new Font("Segoe UI", 9),
            ForeColor = TextSecondary,
            Location = new Point(LeftMargin, 52),
            AutoSize = true,
            BackColor = Color.Transparent,
            Visible = false,
        };
        _contentPanel.Controls.Add(_noDevicesLabel);

        Deactivate += (_, _) => Hide();
    }

    protected override void OnPaintBackground(PaintEventArgs e)
    {
        using var brush = new LinearGradientBrush(
            ClientRectangle, BgTop, BgBottom, LinearGradientMode.Vertical);
        e.Graphics.FillRectangle(brush, ClientRectangle);

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

        _contentPanel.SuspendLayout();

        if (devices.Count == 0)
        {
            _noDevicesLabel.Visible = true;
            foreach (var row in _rows) row.SetVisible(false);
            Height = 82;
            _contentPanel.ResumeLayout();
            return;
        }

        _noDevicesLabel.Visible = false;

        // Ensure we have enough rows
        while (_rows.Count < devices.Count)
        {
            var row = new DeviceRow(_contentPanel);
            _rows.Add(row);
        }

        var y = 52; // after title + separator

        for (int i = 0; i < devices.Count; i++)
        {
            var device = devices[i];
            var row = _rows[i];

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
            var barWidth = (device.Connected && device.BatteryPercent.HasValue)
                ? (int)(BarMaxWidth * device.BatteryPercent.Value / 100.0)
                : -1; // -1 means no bar

            row.Update(icon, name, statusText, batteryColor, barWidth, y);
            row.SetVisible(true);

            y += 26; // text row height
            y += barWidth >= 0 ? 16 : 6; // bar + spacing, or just spacing
        }

        // Hide unused rows
        for (int i = devices.Count; i < _rows.Count; i++)
            _rows[i].SetVisible(false);

        y += 8;
        Height = y;

        _contentPanel.ResumeLayout();
    }

    private static Color GetBatteryColor(DeviceInfo device)
    {
        if (!device.Connected) return Color.FromArgb(90, 88, 95);
        if (device.Error != null) return Color.FromArgb(190, 95, 95);
        if (device.BatteryPercent == null) return Color.FromArgb(90, 88, 95);
        if (device.Charging) return Color.FromArgb(130, 185, 210);
        return device.BatteryPercent switch
        {
            <= 10 => Color.FromArgb(200, 100, 95),
            <= 20 => Color.FromArgb(200, 155, 85),
            <= 50 => Color.FromArgb(195, 185, 115),
            _ => Color.FromArgb(115, 185, 130),
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

    /// <summary>
    /// Holds the controls for one device row. Created once, reused on each update.
    /// </summary>
    private class DeviceRow
    {
        private readonly Label _iconLabel;
        private readonly Label _nameLabel;
        private readonly Label _statusLabel;
        private readonly Panel _barBg;
        private readonly BarPanel _barFill;

        public DeviceRow(Panel parent)
        {
            _iconLabel = new Label
            {
                Font = new Font("Segoe UI Emoji", 11),
                Size = new Size(26, 22),
                ForeColor = TextPrimary,
                BackColor = Color.Transparent,
            };

            _nameLabel = new Label
            {
                Font = new Font("Segoe UI", 9.5f),
                AutoSize = true,
                ForeColor = TextPrimary,
                BackColor = Color.Transparent,
            };

            _statusLabel = new Label
            {
                Font = new Font("Segoe UI Semibold", 10f),
                BackColor = Color.Transparent,
                TextAlign = ContentAlignment.MiddleRight,
                Size = new Size(64, 22),
            };

            _barBg = new Panel
            {
                BackColor = BarBackground,
                Size = new Size(BarMaxWidth, 3),
            };

            _barFill = new BarPanel(Color.White)
            {
                Size = new Size(0, 3),
            };

            parent.Controls.Add(_iconLabel);
            parent.Controls.Add(_nameLabel);
            parent.Controls.Add(_statusLabel);
            parent.Controls.Add(_barBg);
            parent.Controls.Add(_barFill);
            _barFill.BringToFront();
        }

        public void Update(string icon, string name, string status, Color color, int barWidth, int y)
        {
            _iconLabel.Text = icon;
            _iconLabel.Location = new Point(IconX, y);

            _nameLabel.Text = name;
            _nameLabel.Location = new Point(NameX, y + 3);

            _statusLabel.Text = status;
            _statusLabel.ForeColor = color;
            _statusLabel.Location = new Point(StatusX, y + 1);

            var barY = y + 26;
            if (barWidth >= 0)
            {
                _barBg.Location = new Point(BarX, barY);
                _barBg.Visible = true;
                _barFill.SetColor(color);
                _barFill.Location = new Point(BarX, barY);
                _barFill.Size = new Size(Math.Max(barWidth, 0), 3);
                _barFill.Visible = barWidth > 0;
            }
            else
            {
                _barBg.Visible = false;
                _barFill.Visible = false;
            }
        }

        public void SetVisible(bool visible)
        {
            _iconLabel.Visible = visible;
            _nameLabel.Visible = visible;
            _statusLabel.Visible = visible;
            if (!visible)
            {
                _barBg.Visible = false;
                _barFill.Visible = false;
            }
        }
    }

    private class BarPanel : Panel
    {
        private Color _color;
        public BarPanel(Color color) { _color = color; DoubleBuffered = true; }

        public void SetColor(Color color)
        {
            if (_color == color) return;
            _color = color;
            Invalidate();
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            if (Width <= 0 || Height <= 0) return;
            var lighter = ControlPaint.Light(_color, 0.3f);
            using var brush = new LinearGradientBrush(
                ClientRectangle, lighter, _color, LinearGradientMode.Horizontal);
            e.Graphics.FillRectangle(brush, ClientRectangle);
        }
    }
}
