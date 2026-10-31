using Vortice.Direct3D11;
using Vortice.DXGI;

namespace LightSync.Capture;

/// <summary>
/// Shrinks a full-resolution desktop texture to ~100×60 pixels on the GPU (through a mip chain) and
/// reads it back as 8-bit sRGB BGRA. Handles 8-bit, 10-bit and 16-bit float (HDR / scRGB) desktops.
/// </summary>
public sealed unsafe class GpuDownsampler(ID3D11Device device, ID3D11DeviceContext context, float sdrWhiteLevel) : IDisposable
{
    const int MaxSampleWidth = 128;

    static readonly Format[] SupportedFormats =
    [
        Format.B8G8R8A8_UNorm, Format.B8G8R8A8_UNorm_SRgb, Format.R8G8B8A8_UNorm, Format.R8G8B8A8_UNorm_SRgb,
        Format.R10G10B10A2_UNorm, Format.R16G16B16A16_Float,
    ];

    // Linear 0..1 → 8-bit sRGB, for scRGB (HDR) desktops.
    static readonly byte[] LinearToSrgb = BuildSrgbTable(4096);

    readonly float _whiteScale = 1f / Math.Max(0.1f, sdrWhiteLevel);
    ID3D11Texture2D? _mipTexture;
    ID3D11ShaderResourceView? _mipView;
    ID3D11Texture2D? _staging;
    uint _sourceWidth, _sourceHeight;
    int _mipLevel, _sampleWidth, _sampleHeight;

    public Format Format { get; private set; }
    public bool HasFrame => _mipTexture != null;
    public bool IsHdr => Format == Format.R16G16B16A16_Float;

    public void Process(ID3D11Texture2D source, CaptureFrame frame)
    {
        EnsureResources(source.Description);
        context.CopySubresourceRegion(_mipTexture!, 0, 0, 0, 0, source, 0, null);
        context.GenerateMips(_mipView!);
        context.CopySubresourceRegion(_staging!, 0, 0, 0, 0, _mipTexture!, (uint)_mipLevel, null);

        context.Map(_staging!, 0, MapMode.Read, Vortice.Direct3D11.MapFlags.None, out MappedSubresource map).CheckError();
        try
        {
            frame.Resize(_sampleWidth, _sampleHeight);
            fixed (byte* dst = frame.Pixels)
            {
                for (int y = 0; y < _sampleHeight; y++)
                    ConvertRow((byte*)map.DataPointer + (long)y * map.RowPitch, dst + y * _sampleWidth * 4, _sampleWidth);
            }
        }
        finally
        {
            context.Unmap(_staging!, 0);
        }
    }

    void ConvertRow(byte* src, byte* dst, int width)
    {
        switch (Format)
        {
            case Format.B8G8R8A8_UNorm or Format.B8G8R8A8_UNorm_SRgb:
                Buffer.MemoryCopy(src, dst, width * 4, width * 4);
                break;

            case Format.R8G8B8A8_UNorm or Format.R8G8B8A8_UNorm_SRgb:
                for (int x = 0; x < width; x++, src += 4, dst += 4)
                {
                    dst[0] = src[2]; dst[1] = src[1]; dst[2] = src[0]; dst[3] = 255;
                }
                break;

            case Format.R10G10B10A2_UNorm:
                for (int x = 0; x < width; x++, src += 4, dst += 4)
                {
                    uint v = *(uint*)src;
                    dst[0] = Ten((v >> 20) & 0x3FF);
                    dst[1] = Ten((v >> 10) & 0x3FF);
                    dst[2] = Ten(v & 0x3FF);
                    dst[3] = 255;
                }
                break;

            case Format.R16G16B16A16_Float:
                // scRGB: linear light, 1.0 = 80 nits. Normalise so SDR white = 1.0, clip HDR highlights
                // and out-of-gamut (negative) values, then encode as sRGB like an SDR desktop.
                Half* h = (Half*)src;
                for (int x = 0; x < width; x++, h += 4, dst += 4)
                {
                    dst[0] = Encode((float)h[2]);
                    dst[1] = Encode((float)h[1]);
                    dst[2] = Encode((float)h[0]);
                    dst[3] = 255;
                }
                break;
        }
    }

    static byte Ten(uint v) => (byte)((v * 255 + 511) / 1023);

    byte Encode(float linear)
    {
        float v = linear * _whiteScale;
        if (!(v > 0f)) return 0; // also catches NaN
        if (v >= 1f) return 255;
        return LinearToSrgb[(int)(v * (LinearToSrgb.Length - 1) + 0.5f)];
    }

    static byte[] BuildSrgbTable(int size)
    {
        var table = new byte[size];
        for (int i = 0; i < size; i++)
        {
            double c = i / (double)(size - 1);
            double s = c <= 0.0031308 ? 12.92 * c : 1.055 * Math.Pow(c, 1 / 2.4) - 0.055;
            table[i] = (byte)Math.Clamp((int)Math.Round(s * 255), 0, 255);
        }
        return table;
    }

    void EnsureResources(Texture2DDescription source)
    {
        if (_mipTexture != null && source.Width == _sourceWidth && source.Height == _sourceHeight && source.Format == Format)
            return;
        if (Array.IndexOf(SupportedFormats, source.Format) < 0 ||
            !device.CheckFormatSupport(source.Format).HasFlag(FormatSupport.MipAutogen))
            throw new NotSupportedException($"Unsupported desktop pixel format {source.Format}.");

        ReleaseResources();
        _sourceWidth = source.Width;
        _sourceHeight = source.Height;
        Format = source.Format;

        _mipLevel = 0;
        while ((_sourceWidth >> _mipLevel) > MaxSampleWidth) _mipLevel++;
        _sampleWidth = (int)Math.Max(1, _sourceWidth >> _mipLevel);
        _sampleHeight = (int)Math.Max(1, _sourceHeight >> _mipLevel);

        _mipTexture = device.CreateTexture2D(new Texture2DDescription
        {
            Width = _sourceWidth,
            Height = _sourceHeight,
            MipLevels = (uint)_mipLevel + 1,
            ArraySize = 1,
            Format = Format,
            SampleDescription = new SampleDescription(1, 0),
            Usage = ResourceUsage.Default,
            BindFlags = BindFlags.ShaderResource | BindFlags.RenderTarget,
            CPUAccessFlags = CpuAccessFlags.None,
            MiscFlags = ResourceOptionFlags.GenerateMips,
        });
        _mipView = device.CreateShaderResourceView(_mipTexture);
        _staging = device.CreateTexture2D(new Texture2DDescription
        {
            Width = (uint)_sampleWidth,
            Height = (uint)_sampleHeight,
            MipLevels = 1,
            ArraySize = 1,
            Format = Format,
            SampleDescription = new SampleDescription(1, 0),
            Usage = ResourceUsage.Staging,
            BindFlags = BindFlags.None,
            CPUAccessFlags = CpuAccessFlags.Read,
            MiscFlags = ResourceOptionFlags.None,
        });
    }

    void ReleaseResources()
    {
        _staging?.Dispose(); _staging = null;
        _mipView?.Dispose(); _mipView = null;
        _mipTexture?.Dispose(); _mipTexture = null;
    }

    public void Dispose() => ReleaseResources();
}
