using LightSync.Capture;

namespace LightSync.Sync;

public enum SyncMode
{
    /// <summary>Weights colorful pixels more heavily, so the strip shows the scene's mood instead of muddy grey.</summary>
    Vibrant,
    /// <summary>Plain average of the selected area.</summary>
    Average,
    /// <summary>The most common strong hue on screen.</summary>
    Dominant,
}

public enum CaptureRegion { Full, Edges, Center }

/// <summary>
/// Live-tunable screen sync parameters. The engine reads these every frame, so UI bindings
/// take effect immediately.
/// </summary>
public sealed class SyncSettings
{
    public string Monitor { get; set; } = "";
    /// <summary>With several monitors, show the visual display picker each time sync is started by hand.</summary>
    public bool AskForDisplay { get; set; } = true;
    public CaptureMethod Capture { get; set; } = CaptureMethod.Auto;
    public SyncMode Mode { get; set; } = SyncMode.Vibrant;
    public CaptureRegion Region { get; set; } = CaptureRegion.Full;
    public bool CropBlackBars { get; set; } = true;

    public double Fps { get; set; } = 30;
    /// <summary>0 = instant, 100 = very slow fades.</summary>
    public double Smoothing { get; set; } = 25;
    public double Saturation { get; set; } = 1.35;
    public double BrightnessBoost { get; set; } = 1.4;
    public double Gamma { get; set; } = 1.4;
    /// <summary>Percent; keeps the strip glowing faintly in dark scenes.</summary>
    public double MinBrightness { get; set; } = 0;

    public double GainR { get; set; } = 1.0;
    public double GainG { get; set; } = 1.0;
    public double GainB { get; set; } = 1.0;
}
