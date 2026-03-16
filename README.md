# PeripheryBattery

Lightweight Windows system tray app that shows battery percentages for all your wireless peripherals in one place.

Supports:
- **Logitech** devices via G HUB WebSocket
- **Razer** devices via Synapse 3/4 log parsing
- **Corsair** devices via iCUE SDK v4.x

## Features

- Single tray icon that cycles through all device battery percentages (color-coded)
- Compact dark-themed popup dashboard (left-click tray icon)
- Low battery notifications
- 5-second polling for near-real-time updates
- Auto-start with Windows
- Gracefully handles missing vendor apps (only shows devices for installed software)

## Requirements

- Windows 10/11
- .NET 8 SDK (for building from source)
- At least one of:
  - **Logitech G HUB** (for Logitech devices)
  - **Razer Synapse 3 or 4** (for Razer devices)
  - **Corsair iCUE v4.31+** (for Corsair devices)

## Quick Start

```bash
# Install .NET 8 SDK from https://dotnet.microsoft.com/download/dotnet/8.0

# Build and run
cd PeripheryBattery
dotnet run

# Or build a standalone exe (runs on any Windows machine, no .NET needed)
dotnet publish -c Release -r win-x64 --self-contained -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true
# Output: bin/Release/net8.0-windows/win-x64/publish/PeripheryBattery.exe
```

## Usage

- **Left-click** the tray icon to open the battery popup
- **Right-click** for context menu (Open Log, Open Config, Exit)
- The tray icon cycles through all connected devices every 3 seconds
- Hover over the tray icon for a tooltip summary

## How It Works

| Vendor | Data Source | Update Frequency |
|--------|-----------|-----------------|
| Logitech | G HUB WebSocket (`ws://localhost:9010`) | Every 5s (polling) |
| Razer | Synapse 4 product logs / Synapse 3 main log | Every 5s (file read) |
| Corsair | iCUE SDK (`iCUESDK.x64_2019.dll`) | Every 5s (SDK call) |

Providers that can't connect (e.g. G HUB not installed) are automatically disabled after 3 failed attempts and won't spam logs.

## Config

Edit `%LOCALAPPDATA%\PeripheryBattery\config.json`:

```json
{
  "PollIntervalSeconds": 5,
  "LowBatteryThreshold": 25,
  "ShowNotifications": true,
  "StartWithWindows": false,
  "IconCycleSeconds": 3,
  "DeviceNameOverrides": {
    "DeathAdder": "DeathAdder V3 Pro",
    "G915 TKL": "G915 TKL",
    "PRO X 2": "PRO X 2 Headset"
  }
}
```

`DeviceNameOverrides` uses substring matching — if any key appears in the detected device name, it gets replaced with the friendly name.

## Logs

`%LOCALAPPDATA%\PeripheryBattery\Logs\app.log` (auto-rotates at 5MB)

## Auto-start with Windows

Set `"StartWithWindows": true` in config. This adds a registry entry to `HKCU\Software\Microsoft\Windows\CurrentVersion\Run`.
