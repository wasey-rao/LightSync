namespace LightSync.Sync;

/// <summary>Linear 0..1 RGB triple used through the color pipeline.</summary>
public readonly record struct Rgb(float R, float G, float B)
{
    public float Max => MathF.Max(R, MathF.Max(G, B));
    public float Min => MathF.Min(R, MathF.Min(G, B));
    public bool IsFinite => float.IsFinite(R) && float.IsFinite(G) && float.IsFinite(B);

    /// <summary>Clamps every channel to 0..1; NaN becomes 0.</summary>
    public Rgb Clamp01() => new(Clamp01(R), Clamp01(G), Clamp01(B));

    static float Clamp01(float v) => v > 0f ? (v < 1f ? v : 1f) : 0f;

    public static Rgb operator *(Rgb c, float k) => new(c.R * k, c.G * k, c.B * k);

    public static Rgb Lerp(Rgb a, Rgb b, float t) =>
        new(a.R + (b.R - a.R) * t, a.G + (b.G - a.G) * t, a.B + (b.B - a.B) * t);

    public (byte R, byte G, byte B) ToBytes() => (ToByte(R), ToByte(G), ToByte(B));

    static byte ToByte(float v) => (byte)Math.Clamp((int)MathF.Round(v * 255f), 0, 255);
}
