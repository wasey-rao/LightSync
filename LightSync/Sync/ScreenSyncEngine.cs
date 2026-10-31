using System.Diagnostics;
using System.Runtime.InteropServices;
using LightSync.Ble;
using LightSync.Capture;

namespace LightSync.Sync;

public sealed record SyncFrame(byte R, byte G, byte B, string Source, double Fps, double ProcessMs);

/// <summary>
/// Capture → extract → correct → smooth → send, on a dedicated thread at the configured rate.
/// </summary>
public sealed class ScreenSyncEngine(ElkBledomDevice device, Func<SyncSettings> settings) : IDisposable
{
    const int UpgradeRetryMs = 30_000;

    Thread? _thread;
    volatile bool _stop;
    string? _lastMessage;

    public bool IsRunning => _thread != null;

    /// <summary>Raised on the sync thread after every processed frame.</summary>
    public event Action<SyncFrame>? FrameProcessed;

    public void Start()
    {
        if (_thread != null) return;
        _stop = false;
        _lastMessage = null;
        _thread = new Thread(Run) { IsBackground = true, Name = "Screen sync", Priority = ThreadPriority.AboveNormal };
        _thread.Start();
        AppLog.Write("Screen sync started.");
    }

    public void Stop()
    {
        if (_thread == null) return;
        _stop = true;
        _thread.Join(TimeSpan.FromSeconds(2));
        _thread = null;
        AppLog.Write("Screen sync stopped.");
    }

    void Run()
    {
        timeBeginPeriod(1);
        IScreenCapture? capture = null;
        string? captureKey = null;
        var frame = new CaptureFrame();
        bool haveFrame = false;
        var clock = Stopwatch.StartNew();
        double lastTick = 0, lastUpgradeTry = 0, fpsWindowStart = 0, fps = 0;
        int fpsFrames = 0;
        Rgb smoothed = default;

        try
        {
            while (!_stop)
            {
                var s = settings();
                double interval = 1000.0 / Math.Clamp(s.Fps, 5, 60);
                double start = clock.Elapsed.TotalMilliseconds;
                device.ColorIntervalMs = Math.Max(10, (int)interval - 3);

                // (Re)create the capture source when the monitor or method changes.
                string key = $"{s.Monitor}|{s.Capture}";
                if (capture == null || key != captureKey)
                {
                    capture?.Dispose();
                    capture = null;
                    try
                    {
                        var monitor = Monitors.Resolve(s.Monitor);
                        capture = CaptureFactory.Create(s.Capture, monitor);
                        captureKey = key;
                        haveFrame = false;
                        lastUpgradeTry = start;
                        Log($"Capturing {monitor.Label} via {capture.Name}.");
                    }
                    catch (Exception ex)
                    {
                        Log($"Screen capture unavailable: {ex.Message}");
                        Thread.Sleep(1000);
                        continue;
                    }
                }
                else if (capture is GdiCapture && s.Capture == CaptureMethod.Auto && start - lastUpgradeTry > UpgradeRetryMs)
                {
                    // DirectX may have failed transiently (e.g. at the lock screen); try again periodically.
                    // The first frame of a new duplication arrives immediately, so a test grab proves it works.
                    lastUpgradeTry = start;
                    DxgiCapture? upgraded = null;
                    try
                    {
                        upgraded = DxgiCapture.Create(Monitors.Resolve(s.Monitor).DeviceName);
                        if (upgraded.TryCapture(100, frame)) haveFrame = true;
                        capture.Dispose();
                        capture = upgraded;
                        Log("Switched to DirectX capture.");
                    }
                    catch
                    {
                        if (upgraded != capture) upgraded?.Dispose(); // stay on GDI
                    }
                }

                try
                {
                    if (capture.TryCapture(Math.Max(1, (int)(interval * 0.75)), frame)) haveFrame = true;
                }
                catch (Exception ex)
                {
                    bool lost = ex is CaptureLostException;
                    bool wasDirectX = capture is DxgiCapture;
                    capture.Dispose();
                    capture = null;

                    if (!lost && wasDirectX && s.Capture == CaptureMethod.Auto)
                    {
                        // DirectX can't handle this desktop; GDI always can.
                        Log($"DirectX capture failed ({ex.Message}); using GDI capture instead.");
                        try
                        {
                            capture = new GdiCapture(Monitors.Resolve(s.Monitor).Bounds);
                            haveFrame = false;
                            lastUpgradeTry = start;
                        }
                        catch (Exception gdiError)
                        {
                            Log($"GDI capture unavailable: {gdiError.Message}");
                            Thread.Sleep(1000);
                        }
                        continue;
                    }

                    Log(lost ? ex.Message : $"Capture error: {ex.Message}");
                    Thread.Sleep(lost ? 200 : 1000);
                    continue;
                }

                if (haveFrame)
                {
                    double now = clock.Elapsed.TotalMilliseconds;
                    float dt = (float)(now - lastTick);
                    lastTick = now;

                    // Re-extract even without a new frame so setting changes apply on a static screen.
                    var target = ColorPipeline.Correct(ColorExtractor.Extract(frame, s.Mode, s.Region, s.CropBlackBars), s);
                    float tau = SmoothingTimeConstant(s.Smoothing);
                    float alpha = tau <= 0 ? 1f : 1f - MathF.Exp(-dt / tau);
                    smoothed = Rgb.Lerp(smoothed, target, alpha);

                    var (r, g, b) = ColorPipeline.WhiteBalance(smoothed, s).ToBytes();
                    device.SetColor(r, g, b);

                    fpsFrames++;
                    if (now - fpsWindowStart >= 1000)
                    {
                        fps = fpsFrames * 1000.0 / (now - fpsWindowStart);
                        fpsFrames = 0;
                        fpsWindowStart = now;
                    }
                    FrameProcessed?.Invoke(new SyncFrame(r, g, b, capture.Name, fps, clock.Elapsed.TotalMilliseconds - start));
                }

                int sleep = (int)(interval - (clock.Elapsed.TotalMilliseconds - start));
                if (sleep > 0) Thread.Sleep(sleep);
            }
        }
        catch (Exception ex)
        {
            AppLog.Write($"Screen sync crashed: {ex}");
        }
        finally
        {
            capture?.Dispose();
            timeEndPeriod(1);
        }
    }

    /// <summary>Logs a message unless it repeats the previous one (keeps retry loops from flooding the log).</summary>
    void Log(string message)
    {
        if (message == _lastMessage) return;
        _lastMessage = message;
        AppLog.Write(message);
    }

    /// <summary>Maps the 0-100 smoothing slider to an exponential time constant (0 … 1.5 s).</summary>
    static float SmoothingTimeConstant(double smoothing)
    {
        double t = Math.Clamp(smoothing, 0, 100) / 100.0;
        return (float)(1500.0 * t * t);
    }

    public void Dispose() => Stop();

    [DllImport("winmm.dll")] static extern uint timeBeginPeriod(uint period);
    [DllImport("winmm.dll")] static extern uint timeEndPeriod(uint period);
}
