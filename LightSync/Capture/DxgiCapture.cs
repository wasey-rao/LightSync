using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.DXGI;

namespace LightSync.Capture;

/// <summary>
/// Captures a monitor with DXGI Desktop Duplication (works with games and hardware-accelerated
/// video) and downscales it on the GPU through a mip chain, so only ~100×60 pixels are ever
/// copied back to the CPU.
/// </summary>
public sealed unsafe class DxgiCapture : IScreenCapture
{
    const int MaxSampleWidth = 128;
    const int WaitTimeout = unchecked((int)0x887A0027); // DXGI_ERROR_WAIT_TIMEOUT
    const int AccessLost = unchecked((int)0x887A0026);  // DXGI_ERROR_ACCESS_LOST

    readonly ID3D11Device _device;
    readonly ID3D11DeviceContext _context;
    readonly IDXGIOutputDuplication _duplication;
    ID3D11Texture2D? _mipTexture;
    ID3D11ShaderResourceView? _mipView;
    ID3D11Texture2D? _staging;
    uint _sourceWidth, _sourceHeight;
    Format _format;
    int _mipLevel, _sampleWidth, _sampleHeight;

    public string Name => "DirectX";

    DxgiCapture(ID3D11Device device, ID3D11DeviceContext context, IDXGIOutputDuplication duplication)
    {
        _device = device;
        _context = context;
        _duplication = duplication;
    }

    public static DxgiCapture Create(string monitorDeviceName)
    {
        using var factory = DXGI.CreateDXGIFactory1<IDXGIFactory1>();
        for (uint a = 0; factory.EnumAdapters1(a, out IDXGIAdapter1? adapter).Success; a++)
        {
            using (adapter)
            {
                for (uint o = 0; adapter.EnumOutputs(o, out IDXGIOutput? output).Success; o++)
                {
                    using (output)
                    {
                        if (!string.Equals(output.Description.DeviceName, monitorDeviceName, StringComparison.OrdinalIgnoreCase))
                            continue;

                        using var output1 = output.QueryInterface<IDXGIOutput1>();
                        // The device must live on the adapter that drives this output.
                        D3D11.D3D11CreateDevice(adapter, DriverType.Unknown, DeviceCreationFlags.BgraSupport,
                            [FeatureLevel.Level_11_0, FeatureLevel.Level_10_1, FeatureLevel.Level_10_0],
                            out ID3D11Device? device, out ID3D11DeviceContext? context).CheckError();
                        try
                        {
                            return new DxgiCapture(device!, context!, output1.DuplicateOutput(device));
                        }
                        catch
                        {
                            context?.Dispose();
                            device?.Dispose();
                            throw;
                        }
                    }
                }
            }
        }
        throw new InvalidOperationException($"{monitorDeviceName} was not found on any graphics adapter.");
    }

    public bool TryCapture(int timeoutMs, CaptureFrame frame)
    {
        var result = _duplication.AcquireNextFrame((uint)Math.Max(0, timeoutMs), out OutduplFrameInfo info, out IDXGIResource? resource);
        if (result.Code == WaitTimeout) return false;
        if (result.Code == AccessLost) throw new CaptureLostException("Screen capture was reset (display change, fullscreen switch or lock screen).");
        result.CheckError();

        try
        {
            // Only the mouse pointer moved: the desktop image is unchanged.
            if (info.LastPresentTime == 0 && _mipTexture != null) return false;

            using var texture = resource!.QueryInterface<ID3D11Texture2D>();
            EnsureResources(texture.Description);
            _context.CopySubresourceRegion(_mipTexture!, 0, 0, 0, 0, texture, 0, null);
        }
        finally
        {
            resource?.Dispose();
            _duplication.ReleaseFrame();
        }

        _context.GenerateMips(_mipView!);
        _context.CopySubresourceRegion(_staging!, 0, 0, 0, 0, _mipTexture!, (uint)_mipLevel, null);

        _context.Map(_staging!, 0, MapMode.Read, Vortice.Direct3D11.MapFlags.None, out MappedSubresource map).CheckError();
        try
        {
            frame.Resize(_sampleWidth, _sampleHeight);
            int rowBytes = _sampleWidth * 4;
            fixed (byte* dst = frame.Pixels)
            {
                for (int y = 0; y < _sampleHeight; y++)
                {
                    byte* src = (byte*)map.DataPointer + (long)y * map.RowPitch;
                    Buffer.MemoryCopy(src, dst + y * rowBytes, rowBytes, rowBytes);
                }
            }
        }
        finally
        {
            _context.Unmap(_staging!, 0);
        }
        return true;
    }

    void EnsureResources(Texture2DDescription source)
    {
        if (_mipTexture != null && source.Width == _sourceWidth && source.Height == _sourceHeight && source.Format == _format)
            return;
        if (source.Format is not (Format.B8G8R8A8_UNorm or Format.B8G8R8A8_UNorm_SRgb))
            throw new NotSupportedException($"Unsupported desktop pixel format {source.Format}.");

        ReleaseResources();
        _sourceWidth = source.Width;
        _sourceHeight = source.Height;
        _format = source.Format;

        _mipLevel = 0;
        while ((_sourceWidth >> _mipLevel) > MaxSampleWidth) _mipLevel++;
        _sampleWidth = (int)Math.Max(1, _sourceWidth >> _mipLevel);
        _sampleHeight = (int)Math.Max(1, _sourceHeight >> _mipLevel);

        _mipTexture = _device.CreateTexture2D(new Texture2DDescription
        {
            Width = _sourceWidth,
            Height = _sourceHeight,
            MipLevels = (uint)_mipLevel + 1,
            ArraySize = 1,
            Format = _format,
            SampleDescription = new SampleDescription(1, 0),
            Usage = ResourceUsage.Default,
            BindFlags = BindFlags.ShaderResource | BindFlags.RenderTarget,
            CPUAccessFlags = CpuAccessFlags.None,
            MiscFlags = ResourceOptionFlags.GenerateMips,
        });
        _mipView = _device.CreateShaderResourceView(_mipTexture);
        _staging = _device.CreateTexture2D(new Texture2DDescription
        {
            Width = (uint)_sampleWidth,
            Height = (uint)_sampleHeight,
            MipLevels = 1,
            ArraySize = 1,
            Format = _format,
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

    public void Dispose()
    {
        ReleaseResources();
        _duplication.Dispose();
        _context.Dispose();
        _device.Dispose();
    }
}
