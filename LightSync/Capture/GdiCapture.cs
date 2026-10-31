using System.Runtime.InteropServices;

namespace LightSync.Capture;

/// <summary>
/// Compatibility capture through GDI: StretchBlt with HALFTONE averages the monitor down to a
/// small bitmap in one call. Slower than DXGI and blind to exclusive-fullscreen games, but works
/// everywhere (remote desktop, unusual drivers).
/// </summary>
public sealed unsafe class GdiCapture : IScreenCapture
{
    const int SampleWidth = 96;
    const int HALFTONE = 4;
    const int SRCCOPY = 0x00CC0020;

    readonly System.Drawing.Rectangle _bounds;
    readonly int _width, _height;
    readonly IntPtr _memoryDc, _bitmap, _previousBitmap;
    readonly byte* _bits;

    public string Name => "GDI";

    public GdiCapture(System.Drawing.Rectangle bounds)
    {
        _bounds = bounds;
        _width = SampleWidth;
        _height = Math.Max(1, (int)Math.Round(SampleWidth * (double)bounds.Height / bounds.Width));

        IntPtr screen = GetDC(IntPtr.Zero);
        _memoryDc = CreateCompatibleDC(screen);
        ReleaseDC(IntPtr.Zero, screen);

        var header = new BITMAPINFOHEADER
        {
            biSize = Marshal.SizeOf<BITMAPINFOHEADER>(),
            biWidth = _width,
            biHeight = -_height, // top-down rows
            biPlanes = 1,
            biBitCount = 32,
        };
        _bitmap = CreateDIBSection(_memoryDc, ref header, 0, out IntPtr bits, IntPtr.Zero, 0);
        if (_bitmap == IntPtr.Zero)
        {
            DeleteDC(_memoryDc);
            throw new InvalidOperationException("Could not allocate the GDI capture bitmap.");
        }
        _bits = (byte*)bits;
        _previousBitmap = SelectObject(_memoryDc, _bitmap);
        SetStretchBltMode(_memoryDc, HALFTONE);
        SetBrushOrgEx(_memoryDc, 0, 0, IntPtr.Zero);
    }

    public bool TryCapture(int timeoutMs, CaptureFrame frame)
    {
        IntPtr screen = GetDC(IntPtr.Zero);
        try
        {
            if (!StretchBlt(_memoryDc, 0, 0, _width, _height, screen, _bounds.X, _bounds.Y, _bounds.Width, _bounds.Height, SRCCOPY))
                throw new CaptureLostException("GDI screen capture failed (lock screen or secure desktop?).");
        }
        finally
        {
            ReleaseDC(IntPtr.Zero, screen);
        }
        GdiFlush();
        frame.Resize(_width, _height);
        new ReadOnlySpan<byte>(_bits, _width * _height * 4).CopyTo(frame.Pixels);
        return true;
    }

    public void Dispose()
    {
        SelectObject(_memoryDc, _previousBitmap);
        DeleteObject(_bitmap);
        DeleteDC(_memoryDc);
    }

    [StructLayout(LayoutKind.Sequential)]
    struct BITMAPINFOHEADER
    {
        public int biSize;
        public int biWidth;
        public int biHeight;
        public short biPlanes;
        public short biBitCount;
        public int biCompression;
        public int biSizeImage;
        public int biXPelsPerMeter;
        public int biYPelsPerMeter;
        public int biClrUsed;
        public int biClrImportant;
    }

    [DllImport("user32.dll")] static extern IntPtr GetDC(IntPtr hwnd);
    [DllImport("user32.dll")] static extern int ReleaseDC(IntPtr hwnd, IntPtr hdc);
    [DllImport("gdi32.dll")] static extern IntPtr CreateCompatibleDC(IntPtr hdc);
    [DllImport("gdi32.dll")] static extern bool DeleteDC(IntPtr hdc);
    [DllImport("gdi32.dll")] static extern IntPtr CreateDIBSection(IntPtr hdc, ref BITMAPINFOHEADER header, uint usage, out IntPtr bits, IntPtr section, uint offset);
    [DllImport("gdi32.dll")] static extern IntPtr SelectObject(IntPtr hdc, IntPtr obj);
    [DllImport("gdi32.dll")] static extern bool DeleteObject(IntPtr obj);
    [DllImport("gdi32.dll")] static extern int SetStretchBltMode(IntPtr hdc, int mode);
    [DllImport("gdi32.dll")] static extern bool SetBrushOrgEx(IntPtr hdc, int x, int y, IntPtr previous);
    [DllImport("gdi32.dll")] static extern bool StretchBlt(IntPtr dst, int dx, int dy, int dw, int dh, IntPtr src, int sx, int sy, int sw, int sh, int rop);
    [DllImport("gdi32.dll")] static extern bool GdiFlush();
}
