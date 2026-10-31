namespace LightSync.Capture;

public enum CaptureMethod { Auto, DirectX, Gdi }

/// <summary>A small, downscaled snapshot of a monitor: tightly packed BGRA pixels.</summary>
public sealed class CaptureFrame
{
    public byte[] Pixels { get; private set; } = [];
    public int Width { get; private set; }
    public int Height { get; private set; }

    public void Resize(int width, int height)
    {
        if (Pixels.Length != width * height * 4) Pixels = new byte[width * height * 4];
        Width = width;
        Height = height;
    }
}

public interface IScreenCapture : IDisposable
{
    string Name { get; }

    /// <summary>
    /// Waits up to <paramref name="timeoutMs"/> for a new frame and writes it into <paramref name="frame"/>.
    /// Returns false when the screen hasn't changed (the previous frame is still valid).
    /// </summary>
    bool TryCapture(int timeoutMs, CaptureFrame frame);
}

/// <summary>The capture source became invalid (mode change, fullscreen switch, lock screen) and must be recreated.</summary>
public sealed class CaptureLostException(string message) : Exception(message);

public sealed record MonitorInfo(string DeviceName, string Label, System.Drawing.Rectangle Bounds, bool Primary);

public static class Monitors
{
    public static IReadOnlyList<MonitorInfo> GetAll() =>
        System.Windows.Forms.Screen.AllScreens
            .Select(s => new MonitorInfo(s.DeviceName, Describe(s), s.Bounds, s.Primary))
            .OrderBy(m => m.DeviceName, StringComparer.OrdinalIgnoreCase)
            .ToList();

    /// <summary>Finds a monitor by device name, falling back to the primary one.</summary>
    public static MonitorInfo Resolve(string? deviceName)
    {
        var all = GetAll();
        return all.FirstOrDefault(m => string.Equals(m.DeviceName, deviceName, StringComparison.OrdinalIgnoreCase))
            ?? all.FirstOrDefault(m => m.Primary)
            ?? all[0];
    }

    static string Describe(System.Windows.Forms.Screen screen)
    {
        var digits = new string(screen.DeviceName.Where(char.IsDigit).ToArray());
        var name = digits.Length > 0 ? $"Display {digits}" : screen.DeviceName;
        return $"{name}  ·  {screen.Bounds.Width}×{screen.Bounds.Height}{(screen.Primary ? "  ·  primary" : "")}";
    }
}

public static class CaptureFactory
{
    public static IScreenCapture Create(CaptureMethod method, MonitorInfo monitor)
    {
        if (method != CaptureMethod.Gdi)
        {
            try
            {
                return DxgiCapture.Create(monitor.DeviceName);
            }
            catch (Exception ex) when (method == CaptureMethod.Auto)
            {
                AppLog.Write($"DirectX capture unavailable ({ex.Message}); using GDI.");
            }
        }
        return new GdiCapture(monitor.Bounds);
    }
}
