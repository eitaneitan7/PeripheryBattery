using PeripheryBattery.Utils;

namespace PeripheryBattery;

static class Program
{
    private static Mutex? _mutex;

    [STAThread]
    static void Main()
    {
        // Single instance check
        const string mutexName = "PeripheryBattery_SingleInstance";
        _mutex = new Mutex(true, mutexName, out bool isNew);

        if (!isNew)
        {
            MessageBox.Show("PeripheryBattery is already running.", "PeripheryBattery",
                MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }

        // Global error handling
        Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);
        Application.ThreadException += (_, e) =>
        {
            Logger.Log($"[FATAL] UI thread exception: {e.Exception}");
        };
        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
        {
            Logger.Log($"[FATAL] Unhandled exception: {e.ExceptionObject}");
        };

        ApplicationConfiguration.Initialize();
        Application.Run(new TrayApp());

        GC.KeepAlive(_mutex);
    }
}
