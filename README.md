# PeripheryBattery

Lightweight Windows system tray app that shows battery percentages for your wireless peripherals.

Supports:
- **Logitech** devices via G HUB WebSocket (ws://localhost:9010)
- **Razer** devices via Synapse 3/4 log parsing

## Requirements

- Windows 10/11
- .NET 8 SDK (for building)
- Logitech G HUB (for Logitech devices)
- Razer Synapse 3 or 4 (for Razer devices)

## Quick Start

```bash
# Install .NET 8 SDK from https://dotnet.microsoft.com/download/dotnet/8.0

# Build and run
cd PeripheryBattery
dotnet run

# Or build a standalone exe
dotnet publish -c Release -r win-x64 --self-contained -p:PublishSingleFile=true
# Output: bin/Release/net8.0-windows/win-x64/publish/PeripheryBattery.exe
```

## Usage

- **Left-click** the tray icon to open the battery popup
- **Right-click** for context menu (Refresh, Open Log, Open Config, Exit)
- The tray icon shows the lowest battery percentage across all devices
- Hover over the tray icon for a tooltip summary of all devices

## Config

Edit `%LOCALAPPDATA%\PeripheryBattery\config.json`:

```json
{
  "PollIntervalSeconds": 60,
  "LowBatteryThreshold": 20,
  "ShowNotifications": true,
  "StartWithWindows": false,
  "DeviceNameOverrides": {
    "G915 TKL LIGHTSPEED Wireless RGB Mechanical Gaming Keyboard": "G915 TKL"
  }
}
```

## Logs

Logs are at `%LOCALAPPDATA%\PeripheryBattery\Logs\app.log`

## Auto-start with Windows

Set `"StartWithWindows": true` in config, or manually add `PeripheryBattery.exe` to:
`HKCU\Software\Microsoft\Windows\CurrentVersion\Run`
