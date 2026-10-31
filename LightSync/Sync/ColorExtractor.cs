using LightSync.Capture;

namespace LightSync.Sync;

/// <summary>Reduces a downscaled screen frame to the single color a non-addressable strip can show.</summary>
public static class ColorExtractor
{
    const int HueBins = 24;
    const int BarThreshold = 24;   // max channel value still counted as "black bar"

    public static Rgb Extract(CaptureFrame frame, SyncMode mode, CaptureRegion region, bool cropBlackBars)
    {
        int w = frame.Width, h = frame.Height;
        if (w == 0 || h == 0) return default;
        byte[] px = frame.Pixels;

        int x0 = 0, y0 = 0, x1 = w, y1 = h;
        if (cropBlackBars) FindContentBounds(px, w, h, ref x0, ref y0, ref x1, ref y1);

        int cw = x1 - x0, ch = y1 - y0;
        if (region == CaptureRegion.Center)
        {
            x0 += cw / 5; x1 -= cw / 5;
            y0 += ch / 5; y1 -= ch / 5;
        }

        // For "edges" the interior rectangle is skipped.
        bool edgesOnly = region == CaptureRegion.Edges;
        int bx = Math.Max(1, cw * 18 / 100), by = Math.Max(1, ch * 18 / 100);
        int ix0 = x0 + bx, ix1 = x1 - bx, iy0 = y0 + by, iy1 = y1 - by;

        double sumR = 0, sumG = 0, sumB = 0;
        double wR = 0, wG = 0, wB = 0, wSum = 0;
        Span<double> bins = stackalloc double[HueBins * 4]; // weight, r, g, b
        bins.Clear();
        int count = 0;

        for (int y = y0; y < y1; y++)
        {
            bool interiorRow = edgesOnly && y >= iy0 && y < iy1;
            int row = y * w * 4;
            for (int x = x0; x < x1; x++)
            {
                if (interiorRow && x == ix0) { x = ix1 - 1; continue; }

                int i = row + x * 4;
                float b = px[i] / 255f, g = px[i + 1] / 255f, r = px[i + 2] / 255f;
                sumR += r; sumG += g; sumB += b;
                count++;

                if (mode == SyncMode.Average) continue;

                float max = MathF.Max(r, MathF.Max(g, b));
                float min = MathF.Min(r, MathF.Min(g, b));
                float sat = max <= 0 ? 0 : (max - min) / max;

                if (mode == SyncMode.Vibrant)
                {
                    float weight = sat * sat * max + 0.01f;
                    wR += r * weight; wG += g * weight; wB += b * weight;
                    wSum += weight;
                }
                else if (sat > 0.2f && max > 0.12f)
                {
                    int bin = HueBin(r, g, b, max, max - min);
                    float weight = sat * max;
                    bins[bin * 4] += weight;
                    bins[bin * 4 + 1] += r * weight;
                    bins[bin * 4 + 2] += g * weight;
                    bins[bin * 4 + 3] += b * weight;
                }
            }
        }

        if (count == 0) return default;
        var average = new Rgb((float)(sumR / count), (float)(sumG / count), (float)(sumB / count));

        switch (mode)
        {
            case SyncMode.Vibrant:
                return MatchBrightness(new Rgb((float)(wR / wSum), (float)(wG / wSum), (float)(wB / wSum)), average);

            case SyncMode.Dominant:
                int best = -1;
                double bestWeight = 0;
                for (int i = 0; i < HueBins; i++)
                {
                    // Include neighbours so a hue straddling two bins isn't split.
                    double weight = bins[i * 4] + 0.5 * (bins[(i + HueBins - 1) % HueBins * 4] + bins[(i + 1) % HueBins * 4]);
                    if (weight > bestWeight) { bestWeight = weight; best = i; }
                }
                if (best < 0 || bestWeight < count * 0.02) return average; // nothing colorful on screen
                double r = 0, g = 0, bl = 0, total = 0;
                foreach (int i in new[] { (best + HueBins - 1) % HueBins, best, (best + 1) % HueBins })
                {
                    total += bins[i * 4];
                    r += bins[i * 4 + 1]; g += bins[i * 4 + 2]; bl += bins[i * 4 + 3];
                }
                return MatchBrightness(new Rgb((float)(r / total), (float)(g / total), (float)(bl / total)), average);

            default:
                return average;
        }
    }

    /// <summary>Keeps the hue of <paramref name="color"/> but follows the overall scene brightness.</summary>
    static Rgb MatchBrightness(Rgb color, Rgb average)
    {
        float max = color.Max;
        if (max < 1e-4f) return average;
        float target = 0.65f * average.Max + 0.35f * max;
        return color * (target / max);
    }

    static int HueBin(float r, float g, float b, float max, float delta)
    {
        float hue;
        if (max == r) hue = (g - b) / delta;
        else if (max == g) hue = (b - r) / delta + 2f;
        else hue = (r - g) / delta + 4f;
        hue *= 60f;
        if (hue < 0) hue += 360f;
        return Math.Min(HueBins - 1, (int)(hue / (360f / HueBins)));
    }

    /// <summary>Trims letterbox / pillarbox bars (up to 30% per side) so they don't darken the result.</summary>
    static void FindContentBounds(byte[] px, int w, int h, ref int x0, ref int y0, ref int x1, ref int y1)
    {
        int maxBarY = h * 3 / 10, maxBarX = w * 3 / 10;
        while (y0 < maxBarY && IsDarkLine(px, w, y0, 0, 1, 0, w)) y0++;
        while (h - y1 < maxBarY && IsDarkLine(px, w, y1 - 1, 0, 1, 0, w)) y1--;
        while (x0 < maxBarX && IsDarkLine(px, w, y0, x0, 0, 1, y1 - y0)) x0++;
        while (w - x1 < maxBarX && IsDarkLine(px, w, y0, x1 - 1, 0, 1, y1 - y0)) x1--;
    }

    /// <summary>Walks <paramref name="length"/> pixels from (x, y); (dx, dy) = (1, 0) for a row or (0, 1) for a column.</summary>
    static bool IsDarkLine(byte[] px, int w, int y, int x, int dx, int dy, int length)
    {
        int bright = 0;
        for (int n = 0; n < length; n++)
        {
            int i = ((y + n * dy) * w + x + n * dx) * 4;
            if (px[i] > BarThreshold || px[i + 1] > BarThreshold || px[i + 2] > BarThreshold) bright++;
        }
        return bright <= length / 50;
    }
}
