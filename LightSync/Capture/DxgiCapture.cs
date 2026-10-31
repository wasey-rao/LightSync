using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.DXGI;

namespace LightSync.Capture;

/// <summary>
/// Captures a monitor with DXGI Desktop Duplication (works with games and hardware-accelerated
/// video, SDR or HDR) and downscales it on the GPU, so only ~100×60 pixels reach the CPU.
/// </summary>
public sealed class DxgiCapture : IScreenCapture
{
    const int WaitTimeout = unchecked((int)0x887A0027); // DXGI_ERROR_WAIT_TIMEOUT
    const int AccessLost = unchecked((int)0x887A0026);  // DXGI_ERROR_ACCESS_LOST

    readonly ID3D11Device _device;
    readonly ID3D11DeviceContext _context;
    readonly IDXGIOutputDuplication _duplication;
    readonly GpuDownsampler _downsampler;
    readonly float _sdrWhiteLevel;
    bool _formatLogged;

    public string Name => _downsampler.IsHdr ? "DirectX HDR" : "DirectX";

    DxgiCapture(ID3D11Device device, ID3D11DeviceContext context, IDXGIOutputDuplication duplication, float sdrWhiteLevel)
    {
        _device = device;
        _context = context;
        _duplication = duplication;
        _sdrWhiteLevel = sdrWhiteLevel;
        _downsampler = new GpuDownsampler(device, context, sdrWhiteLevel);
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
                            float white = DisplayConfig.GetSdrWhiteLevel(monitorDeviceName) ?? 1f;
                            return new DxgiCapture(device!, context!, output1.DuplicateOutput(device), white);
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
            if (info.LastPresentTime == 0 && _downsampler.HasFrame) return false;

            using var texture = resource!.QueryInterface<ID3D11Texture2D>();
            _downsampler.Process(texture, frame);
        }
        finally
        {
            resource?.Dispose();
            _duplication.ReleaseFrame();
        }

        if (!_formatLogged)
        {
            _formatLogged = true;
            if (_downsampler.IsHdr)
                AppLog.Write($"HDR desktop detected; SDR white is {_sdrWhiteLevel * 80:0} nits.");
            else if (_downsampler.Format != Format.B8G8R8A8_UNorm)
                AppLog.Write($"Desktop pixel format: {_downsampler.Format}.");
        }
        return true;
    }

    public void Dispose()
    {
        _downsampler.Dispose();
        _duplication.Dispose();
        _context.Dispose();
        _device.Dispose();
    }
}
