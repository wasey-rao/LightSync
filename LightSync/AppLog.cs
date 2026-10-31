using System.IO;

namespace LightSync;

/// <summary>Tiny thread-safe log: appends to %APPDATA%\LightSync\lightsync.log and notifies the UI.</summary>
public static class AppLog
{
    static readonly object Gate = new();
    static readonly string FilePath = Path.Combine(AppPaths.DataDir, "lightsync.log");
    static bool _initialized;

    public static event Action<string>? LineAdded;

    public static void Write(string message)
    {
        string line = $"{DateTime.Now:HH:mm:ss}  {message}";
        lock (Gate)
        {
            try
            {
                if (!_initialized)
                {
                    _initialized = true;
                    Directory.CreateDirectory(AppPaths.DataDir);
                    if (File.Exists(FilePath) && new FileInfo(FilePath).Length > 512 * 1024) File.Delete(FilePath);
                    File.AppendAllText(FilePath, $"{Environment.NewLine}==== {DateTime.Now:yyyy-MM-dd HH:mm:ss} ===={Environment.NewLine}");
                }
                File.AppendAllText(FilePath, line + Environment.NewLine);
            }
            catch { /* logging must never break the app */ }
        }
        LineAdded?.Invoke(line);
    }
}
