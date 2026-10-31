namespace LightSync.Sync;

/// <summary>Turns a raw screen color into what looks right on an LED strip.</summary>
public static class ColorPipeline
{
    public static Rgb Correct(Rgb c, SyncSettings s)
    {
        // Clamp after every step: float rounding can leave a channel at -0.0000001, and a negative
        // base in MathF.Pow returns NaN, which would then stick in the smoother forever.
        c = AdjustSaturation(c.Clamp01(), (float)s.Saturation).Clamp01();

        // Brightness boost, clipping by scaling all channels so the hue survives.
        c *= (float)s.BrightnessBoost;
        float max = c.Max;
        if (max > 1f) c *= 1f / max;

        // Screen values are gamma-encoded but LED PWM is linear; gamma > 1 keeps dark scenes dark.
        float gamma = (float)s.Gamma;
        if (MathF.Abs(gamma - 1f) > 0.001f)
            c = new Rgb(MathF.Pow(c.R, gamma), MathF.Pow(c.G, gamma), MathF.Pow(c.B, gamma));

        float floor = (float)(s.MinBrightness / 100.0);
        max = c.Max;
        if (max < floor)
            c = max < 0.002f ? new Rgb(floor, floor, floor) : c * (floor / max);

        return c.Clamp01();
    }

    public static Rgb WhiteBalance(Rgb c, SyncSettings s)
    {
        var balanced = new Rgb(c.R * (float)s.GainR, c.G * (float)s.GainG, c.B * (float)s.GainB).Clamp01();
        // Very low PWM values flicker on cheap controllers; snap them to off.
        return balanced.Max < 3f / 255f ? default : balanced;
    }

    /// <summary>Scales HSV saturation while keeping hue and value.</summary>
    static Rgb AdjustSaturation(Rgb c, float factor)
    {
        float max = c.Max, min = c.Min;
        if (max <= 0f || max - min <= 0f || MathF.Abs(factor - 1f) < 0.001f) return c;
        float saturation = (max - min) / max;
        float scale = Math.Clamp(saturation * factor, 0f, 1f) / saturation;
        return new Rgb(max - (max - c.R) * scale, max - (max - c.G) * scale, max - (max - c.B) * scale);
    }
}
