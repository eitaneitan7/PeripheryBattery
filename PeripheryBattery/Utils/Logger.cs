namespace PeripheryBattery.Utils;

public static class Logger
{
    private static readonly string LogDir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "PeripheryBattery", "Logs");

    private static readonly string LogFile = Path.Combine(LogDir, "app.log");
    private static readonly Lock LogLock = new();
    private const long MaxLogSize = 5 * 1024 * 1024; // 5 MB

    static Logger()
    {
        Directory.CreateDirectory(LogDir);
    }

    public static void Log(string message)
    {
        var line = $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}] {message}";

        lock (LogLock)
        {
            try
            {
                // Rotate if too large
                if (File.Exists(LogFile) && new FileInfo(LogFile).Length > MaxLogSize)
                {
                    var backup = LogFile + ".old";
                    if (File.Exists(backup)) File.Delete(backup);
                    File.Move(LogFile, backup);
                }

                File.AppendAllText(LogFile, line + Environment.NewLine);
            }
            catch
            {
                // Logging should never crash the app
            }
        }

#if DEBUG
        System.Diagnostics.Debug.WriteLine(line);
#endif
    }

    public static string LogFilePath => LogFile;
}
