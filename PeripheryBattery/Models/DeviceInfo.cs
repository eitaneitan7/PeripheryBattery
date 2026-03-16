namespace PeripheryBattery.Models;

public class DeviceInfo
{
    public string Id { get; set; } = "";
    public string Vendor { get; set; } = "";       // "Logitech" or "Razer"
    public string DeviceType { get; set; } = "";    // "Mouse", "Keyboard", "Headset"
    public string DisplayName { get; set; } = "";   // e.g. "DeathAdder V3 Pro"
    public int? BatteryPercent { get; set; }         // 0-100, null if unknown
    public bool Charging { get; set; }
    public bool Connected { get; set; }
    public DateTime LastUpdated { get; set; }
    public string Source { get; set; } = "";         // "GHubWebSocket", "SynapseLog"
    public string? Error { get; set; }

    public string StatusText
    {
        get
        {
            if (!Connected) return "Disconnected";
            if (Error != null) return $"Error: {Error}";
            if (BatteryPercent == null) return "Unknown";
            var chg = Charging ? " ⚡" : "";
            return $"{BatteryPercent}%{chg}";
        }
    }
}
