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
    private bool _loggedFirstPoll;

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

    public Task StartAsync(CancellationToken ct)
    {
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
            }
            else
            {
                Logger.Log($"[Corsair] CorsairConnect returned error {err} (iCUE may not be running)");
                _sdkAvailable = true; // DLL loaded, just not connected yet
            }
        }
        catch (DllNotFoundException)
        {
            Logger.Log("[Corsair] iCUESDK.x64_2019.dll not found — Corsair support disabled");
            _sdkAvailable = false;
        }
        catch (Exception ex)
        {
            Logger.Log($"[Corsair] SDK init failed: {ex.Message}");
            _sdkAvailable = false;
        }

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

    private void OnSessionStateChanged(IntPtr context, ref CorsairSessionStateChanged eventData)
    {
        var wasConnected = _connected;
        _connected = eventData.state == CSS_Connected;

        // Only log meaningful transitions, not the Connecting/Timeout cycle
        if (_connected && !wasConnected)
        {
            _loggedTimeout = false;
            Logger.Log("[Corsair] Connected to iCUE");
        }
        else if (!_connected && wasConnected)
        {
            Logger.Log("[Corsair] Disconnected from iCUE");
        }
        else if (eventData.state == 3 && !_loggedTimeout) // CSS_Timeout, first time
        {
            _loggedTimeout = true;
            Logger.Log("[Corsair] iCUE not responding (will keep retrying silently)");
        }
    }

    public Task<List<DeviceInfo>> GetDevicesAsync(CancellationToken ct)
    {
        if (!_sdkAvailable)
            return Task.FromResult(new List<DeviceInfo>());

        if (!_connected)
        {
            // SDK handles reconnection via its internal retry loop (session callback).
            // Just return last known state until it connects.
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

            if (!_loggedFirstPoll)
            {
                Logger.Log($"[Corsair] Found {count} device(s) total");
                for (int j = 0; j < count; j++)
                    Logger.Log($"[Corsair]   #{j}: model=\"{devices[j].model}\" type=0x{devices[j].type:X} id=\"{devices[j].id?[..Math.Min(devices[j].id?.Length ?? 0, 30)]}\"");
            }

            var results = new List<DeviceInfo>();

            for (int i = 0; i < count; i++)
            {
                var dev = devices[i];

                // Only care about wireless device types that can have battery
                if ((dev.type & (CDT_Mouse | CDT_Keyboard | CDT_Headset)) == 0)
                    continue;

                var info = new DeviceInfo
                {
                    Id = $"corsair-{dev.id}",
                    Vendor = "Corsair",
                    DeviceType = ClassifyCorsairDevice(dev.type),
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

                    if (!_loggedFirstPoll)
                        Logger.Log($"[Corsair] ReadDeviceProperty({dev.model}, BatteryLevel): err={readErr}, type={prop.type}, rawValue=0x{prop.value:X16}");

                    if (readErr == CE_Success)
                    {
                        int battery;
                        if (prop.type == CT_Int32)
                        {
                            battery = (int)(prop.value & 0xFFFFFFFF);
                        }
                        else
                        {
                            // Try interpreting raw value anyway
                            battery = (int)(prop.value & 0xFFFFFFFF);
                            Logger.Log($"[Corsair] {dev.model}: unexpected property type {prop.type}, trying raw int interpretation: {battery}");
                        }

                        info.BatteryPercent = Math.Clamp(battery, 0, 100);
                        if (!_loggedFirstPoll)
                            Logger.Log($"[Corsair] {dev.model}: battery={info.BatteryPercent}%");
                        CorsairFreeProperty(ref prop);
                    }
                    else
                    {
                        // Error reading battery — device might be wired or battery not supported
                        // Still add it but without battery info so user sees it as connected
                        Logger.Log($"[Corsair] {dev.model}: battery read error {readErr} (device may not have battery)");
                        continue;
                    }
                }
                catch (Exception ex)
                {
                    Logger.Log($"[Corsair] Battery read exception for {dev.model}: {ex.Message}");
                    continue;
                }

                results.Add(info);
            }

            _loggedFirstPoll = true;
            _lastGoodDevices = results;
            return Task.FromResult(results);
        }
        catch (Exception ex)
        {
            Logger.Log($"[Corsair] GetDevices failed: {ex.Message}");
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
