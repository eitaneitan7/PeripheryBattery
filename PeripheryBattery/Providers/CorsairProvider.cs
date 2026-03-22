using System.Runtime.InteropServices;
using PeripheryBattery.Models;
using PeripheryBattery.Utils;

namespace PeripheryBattery.Providers;

/// <summary>
/// Reads battery data from Corsair wireless devices via the iCUE SDK (v4.x).
/// Requires iCUE to be running. Loads iCUESDK.x64_2019.dll via P/Invoke.
/// </summary>
public class CorsairProvider : IDeviceProvider
{
    public string Vendor => "Corsair";

    private bool _connected;
    private bool _sdkAvailable;
    private List<DeviceInfo> _lastGoodDevices = new();

    #region P/Invoke Declarations

    private const string DllName = "iCUESDK.x64_2019.dll";

    // Enums
    private const int CE_Success = 0;
    private const int CSS_Connected = 6;
    private const int CDPI_BatteryLevel = 9;
    private const int CT_Int32 = 1;

    // Device type flags
    private const int CDT_Keyboard = 0x0001;
    private const int CDT_Mouse = 0x0002;
    private const int CDT_Headset = 0x0008;
    private const int CDT_All = unchecked((int)0xFFFFFFFF);

    private const int CORSAIR_STRING_SIZE_M = 128;
    private const int CORSAIR_DEVICE_COUNT_MAX = 64;

    // CorsairVersion: 3 ints = 12 bytes
    [StructLayout(LayoutKind.Sequential)]
    private struct CorsairVersion
    {
        public int major, minor, patch;
    }

    // CorsairSessionDetails: 3 x CorsairVersion = 36 bytes
    [StructLayout(LayoutKind.Sequential)]
    private struct CorsairSessionDetails
    {
        public CorsairVersion clientVersion;
        public CorsairVersion serverVersion;
        public CorsairVersion serverHostVersion;
    }

    // CorsairSessionStateChanged: state + details
    [StructLayout(LayoutKind.Sequential)]
    private struct CorsairSessionStateChanged
    {
        public int state;
        public CorsairSessionDetails details;
    }

    // CorsairDeviceFilter: just a type mask
    [StructLayout(LayoutKind.Sequential)]
    private struct CorsairDeviceFilter
    {
        public int deviceTypeMask;
    }

    // CorsairDeviceInfo: type(4) + id(128) + serial(128) + model(128) + ledCount(4) + channelCount(4) = 396 bytes
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Ansi)]
    private struct CorsairDeviceInfo
    {
        public int type;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = CORSAIR_STRING_SIZE_M)]
        public string id;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = CORSAIR_STRING_SIZE_M)]
        public string serial;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = CORSAIR_STRING_SIZE_M)]
        public string model;

        public int ledCount;
        public int channelCount;
    }

    // CorsairProperty: type(4) + padding(4 on x64) + union(16 on x64)
    // The union CorsairDataValue contains pointers and doubles, so it's 16 bytes on x64
    [StructLayout(LayoutKind.Sequential)]
    private struct CorsairProperty
    {
        public int type;
        private int _padding; // alignment
        public long value;    // union — for CT_Int32, lower 4 bytes are the int value
    }

    // Callback delegate
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate void SessionStateChangedHandler(IntPtr context, ref CorsairSessionStateChanged eventData);

    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
    private static extern int CorsairConnect(SessionStateChangedHandler onStateChanged, IntPtr context);

    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
    private static extern int CorsairDisconnect();

    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
    private static extern int CorsairGetDevices(ref CorsairDeviceFilter filter, int sizeMax,
        [Out] CorsairDeviceInfo[] devices, out int size);

    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Ansi)]
    private static extern int CorsairReadDeviceProperty(
        byte[] deviceId,
        int propertyId, uint index, out CorsairProperty property);

    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
    private static extern int CorsairFreeProperty(ref CorsairProperty property);

    // Pin the callback delegate so GC doesn't collect it
    private SessionStateChangedHandler? _callbackDelegate;

    #endregion

    private int _pollCount;

    public Task StartAsync(CancellationToken ct)
    {
        Logger.Log("[Corsair] Provider starting, attempting to load iCUE SDK...");
        try
        {
            // Test if the DLL is available
            _callbackDelegate = OnSessionStateChanged;
            var err = CorsairConnect(_callbackDelegate, IntPtr.Zero);
            if (err == CE_Success)
            {
                _sdkAvailable = true;
                Logger.Log("[Corsair] iCUE SDK connected, waiting for session...");
                // Give iCUE a moment to establish the session
                Thread.Sleep(500);
                Logger.Log($"[Corsair] After 500ms wait: connected={_connected}");
            }
            else
            {
                Logger.Log($"[Corsair] CorsairConnect returned error {err} (iCUE may not be running)");
                _sdkAvailable = true; // DLL loaded, just not connected yet
            }
        }
        catch (DllNotFoundException ex)
        {
            Logger.Log($"[Corsair] iCUESDK.x64_2019.dll not found — Corsair support disabled. Details: {ex.Message}");
            _sdkAvailable = false;
        }
        catch (Exception ex)
        {
            Logger.Log($"[Corsair] SDK init failed: {ex.GetType().Name}: {ex.Message}");
            _sdkAvailable = false;
        }

        Logger.Log($"[Corsair] Provider started: sdkAvailable={_sdkAvailable}, connected={_connected}");
        return Task.CompletedTask;
    }

    public Task StopAsync()
    {
        if (_sdkAvailable && _connected)
        {
            try { CorsairDisconnect(); }
            catch { }
        }
        return Task.CompletedTask;
    }

    private bool _loggedTimeout;

    // Map of known session state codes for logging
    private static string SessionStateName(int state) => state switch
    {
        0 => "Disconnected",
        1 => "Connected_NoExclusiveAccess",
        2 => "Connected_ReadOnly",
        3 => "Timeout",
        4 => "ConnectionRefused",
        5 => "ConnectionLost",
        6 => "Connected",
        _ => $"Unknown({state})"
    };

    private int _sessionCallbackCount;

    private void OnSessionStateChanged(IntPtr context, ref CorsairSessionStateChanged eventData)
    {
        var wasConnected = _connected;
        _connected = eventData.state == CSS_Connected;
        _sessionCallbackCount++;

        // Log meaningful transitions always; log repeat states only for first few callbacks
        if (_connected && !wasConnected)
        {
            _loggedTimeout = false;
            Logger.Log($"[Corsair] Session state: {SessionStateName(eventData.state)} — Connected to iCUE, will enumerate devices on next poll");
        }
        else if (!_connected && wasConnected)
        {
            Logger.Log($"[Corsair] Session state: {SessionStateName(eventData.state)} — Disconnected from iCUE, will return last known devices");
        }
        else if (eventData.state == 3 && !_loggedTimeout) // CSS_Timeout, first time
        {
            _loggedTimeout = true;
            Logger.Log("[Corsair] iCUE not responding (will keep retrying silently). Is iCUE running?");
        }
        else if (_sessionCallbackCount <= 5)
        {
            // Log first few state transitions for diagnostics
            Logger.Log($"[Corsair] Session state: {SessionStateName(eventData.state)} (callback #{_sessionCallbackCount})");
        }
    }

    public Task<List<DeviceInfo>> GetDevicesAsync(CancellationToken ct)
    {
        _pollCount++;
        var isDetailedLog = _pollCount <= 3 || _pollCount % 60 == 0;

        if (!_sdkAvailable)
        {
            if (isDetailedLog)
                Logger.Log("[Corsair] SDK not available, skipping poll");
            return Task.FromResult(new List<DeviceInfo>());
        }

        if (!_connected)
        {
            if (isDetailedLog)
                Logger.Log($"[Corsair] Not connected to iCUE (poll #{_pollCount}), returning {_lastGoodDevices.Count} cached device(s)");
            return Task.FromResult(_lastGoodDevices.ToList());
        }

        try
        {
            var filter = new CorsairDeviceFilter { deviceTypeMask = CDT_All };
            var devices = new CorsairDeviceInfo[CORSAIR_DEVICE_COUNT_MAX];
            var err = CorsairGetDevices(ref filter, CORSAIR_DEVICE_COUNT_MAX, devices, out int count);

            if (err != CE_Success)
            {
                Logger.Log($"[Corsair] CorsairGetDevices error: {err}");
                return Task.FromResult(_lastGoodDevices.ToList());
            }

            if (isDetailedLog)
            {
                Logger.Log($"[Corsair] CorsairGetDevices returned {count} device(s) (poll #{_pollCount})");
                for (int j = 0; j < count; j++)
                {
                    var d = devices[j];
                    Logger.Log($"[Corsair]   #{j}: model=\"{d.model}\" serial=\"{d.serial}\" type=0x{d.type:X} ({ClassifyCorsairDevice(d.type)}) id=\"{d.id?[..Math.Min(d.id?.Length ?? 0, 40)]}\" ledCount={d.ledCount} channels={d.channelCount}");
                }
            }

            var results = new List<DeviceInfo>();

            for (int i = 0; i < count; i++)
            {
                var dev = devices[i];
                var deviceTypeName = ClassifyCorsairDevice(dev.type);

                // Only care about wireless device types that can have battery
                if ((dev.type & (CDT_Mouse | CDT_Keyboard | CDT_Headset)) == 0)
                {
                    if (isDetailedLog)
                        Logger.Log($"[Corsair] Skipping device \"{dev.model}\" — type 0x{dev.type:X} is not mouse/keyboard/headset");
                    continue;
                }

                var info = new DeviceInfo
                {
                    Id = $"corsair-{dev.id}",
                    Vendor = "Corsair",
                    DeviceType = deviceTypeName,
                    DisplayName = dev.model,
                    Connected = true,
                    Source = "iCUESDK",
                    LastUpdated = DateTime.Now,
                };

                // Try reading battery level
                try
                {
                    var prop = new CorsairProperty();
                    var deviceIdBytes = new byte[CORSAIR_STRING_SIZE_M];
                    System.Text.Encoding.ASCII.GetBytes(dev.id ?? "", deviceIdBytes);
                    var readErr = CorsairReadDeviceProperty(deviceIdBytes, CDPI_BatteryLevel, 0, out prop);

                    if (isDetailedLog)
                        Logger.Log($"[Corsair] ReadDeviceProperty(\"{dev.model}\", BatteryLevel): err={readErr}, type={prop.type}, rawValue=0x{prop.value:X16}");

                    if (readErr == CE_Success)
                    {
                        int battery;
                        if (prop.type == CT_Int32)
                        {
                            battery = (int)(prop.value & 0xFFFFFFFF);
                        }
                        else
                        {
                            battery = (int)(prop.value & 0xFFFFFFFF);
                            Logger.Log($"[Corsair] {dev.model}: unexpected property type {prop.type} (expected CT_Int32={CT_Int32}), trying raw int interpretation: {battery}");
                        }

                        info.BatteryPercent = Math.Clamp(battery, 0, 100);
                        if (isDetailedLog)
                            Logger.Log($"[Corsair] {dev.model}: battery={info.BatteryPercent}% (raw={battery})");
                        CorsairFreeProperty(ref prop);
                    }
                    else
                    {
                        // Error reading battery — device might be wired or battery not supported
                        if (isDetailedLog)
                            Logger.Log($"[Corsair] {dev.model}: battery read error {readErr} — skipping device (wired or no battery support)");
                        continue;
                    }
                }
                catch (Exception ex)
                {
                    Logger.Log($"[Corsair] Battery read exception for {dev.model}: {ex.GetType().Name}: {ex.Message}");
                    continue;
                }

                results.Add(info);
            }

            if (isDetailedLog)
                Logger.Log($"[Corsair] Poll result: {results.Count} device(s) with battery — [{string.Join(", ", results.Select(r => $"{r.DisplayName}({r.BatteryPercent}%)"))}]");

            _lastGoodDevices = results;
            return Task.FromResult(results);
        }
        catch (Exception ex)
        {
            Logger.Log($"[Corsair] GetDevices failed: {ex.GetType().Name}: {ex.Message}");
            _connected = false;
            return Task.FromResult(_lastGoodDevices.ToList());
        }
    }

    private static string ClassifyCorsairDevice(int type)
    {
        if ((type & CDT_Mouse) != 0) return "Mouse";
        if ((type & CDT_Keyboard) != 0) return "Keyboard";
        if ((type & CDT_Headset) != 0) return "Headset";
        return "Unknown";
    }
}
