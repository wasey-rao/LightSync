using System.Runtime.InteropServices;

namespace LightSync.Capture;

/// <summary>Queries the display configuration API for per-monitor HDR information.</summary>
public static class DisplayConfig
{
    const uint QDC_ONLY_ACTIVE_PATHS = 2;
    const int DISPLAYCONFIG_DEVICE_INFO_GET_SOURCE_NAME = 1;
    const int DISPLAYCONFIG_DEVICE_INFO_GET_SDR_WHITE_LEVEL = 11;

    /// <summary>
    /// Returns the brightness Windows uses for SDR white on this monitor, as an scRGB multiplier
    /// (1.0 = 80 nits). On an HDR desktop, dividing captured values by this maps SDR white back to 1.0.
    /// Returns null if the monitor isn't found or the API isn't available.
    /// </summary>
    public static float? GetSdrWhiteLevel(string gdiDeviceName)
    {
        try
        {
            if (GetDisplayConfigBufferSizes(QDC_ONLY_ACTIVE_PATHS, out uint pathCount, out uint modeCount) != 0) return null;
            var paths = new DISPLAYCONFIG_PATH_INFO[pathCount];
            var modes = new DISPLAYCONFIG_MODE_INFO[modeCount];
            if (QueryDisplayConfig(QDC_ONLY_ACTIVE_PATHS, ref pathCount, paths, ref modeCount, modes, IntPtr.Zero) != 0) return null;

            for (int i = 0; i < pathCount; i++)
            {
                var source = new DISPLAYCONFIG_SOURCE_DEVICE_NAME
                {
                    header = Header(DISPLAYCONFIG_DEVICE_INFO_GET_SOURCE_NAME, Marshal.SizeOf<DISPLAYCONFIG_SOURCE_DEVICE_NAME>(),
                        paths[i].sourceInfo.adapterId, paths[i].sourceInfo.id),
                };
                if (DisplayConfigGetDeviceInfo(ref source) != 0 ||
                    !string.Equals(source.viewGdiDeviceName, gdiDeviceName, StringComparison.OrdinalIgnoreCase))
                    continue;

                var white = new DISPLAYCONFIG_SDR_WHITE_LEVEL
                {
                    header = Header(DISPLAYCONFIG_DEVICE_INFO_GET_SDR_WHITE_LEVEL, Marshal.SizeOf<DISPLAYCONFIG_SDR_WHITE_LEVEL>(),
                        paths[i].targetInfo.adapterId, paths[i].targetInfo.id),
                };
                if (DisplayConfigGetDeviceInfo(ref white) != 0 || white.SDRWhiteLevel == 0) return null;
                return white.SDRWhiteLevel / 1000f;
            }
        }
        catch (Exception ex)
        {
            AppLog.Write($"Could not read the SDR white level: {ex.Message}");
        }
        return null;
    }

    static DISPLAYCONFIG_DEVICE_INFO_HEADER Header(int type, int size, LUID adapter, uint id) =>
        new() { type = type, size = (uint)size, adapterId = adapter, id = id };

    [StructLayout(LayoutKind.Sequential)]
    struct LUID { public uint LowPart; public int HighPart; }

    [StructLayout(LayoutKind.Sequential)]
    struct DISPLAYCONFIG_PATH_SOURCE_INFO { public LUID adapterId; public uint id; public uint modeInfoIdx; public uint statusFlags; }

    [StructLayout(LayoutKind.Sequential)]
    struct DISPLAYCONFIG_PATH_TARGET_INFO
    {
        public LUID adapterId;
        public uint id;
        public uint modeInfoIdx;
        public uint outputTechnology;
        public uint rotation;
        public uint scaling;
        public uint refreshRateNumerator;
        public uint refreshRateDenominator;
        public uint scanLineOrdering;
        public int targetAvailable;
        public uint statusFlags;
    }

    [StructLayout(LayoutKind.Sequential)]
    struct DISPLAYCONFIG_PATH_INFO { public DISPLAYCONFIG_PATH_SOURCE_INFO sourceInfo; public DISPLAYCONFIG_PATH_TARGET_INFO targetInfo; public uint flags; }

    [StructLayout(LayoutKind.Sequential, Size = 64)]
    struct DISPLAYCONFIG_MODE_INFO { public uint infoType; public uint id; public LUID adapterId; }

    [StructLayout(LayoutKind.Sequential)]
    struct DISPLAYCONFIG_DEVICE_INFO_HEADER { public int type; public uint size; public LUID adapterId; public uint id; }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    struct DISPLAYCONFIG_SOURCE_DEVICE_NAME
    {
        public DISPLAYCONFIG_DEVICE_INFO_HEADER header;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string viewGdiDeviceName;
    }

    [StructLayout(LayoutKind.Sequential)]
    struct DISPLAYCONFIG_SDR_WHITE_LEVEL { public DISPLAYCONFIG_DEVICE_INFO_HEADER header; public uint SDRWhiteLevel; }

    [DllImport("user32.dll")] static extern int GetDisplayConfigBufferSizes(uint flags, out uint numPathArrayElements, out uint numModeInfoArrayElements);
    [DllImport("user32.dll")] static extern int QueryDisplayConfig(uint flags, ref uint numPathArrayElements, [Out] DISPLAYCONFIG_PATH_INFO[] pathArray,
        ref uint numModeInfoArrayElements, [Out] DISPLAYCONFIG_MODE_INFO[] modeInfoArray, IntPtr currentTopologyId);
    [DllImport("user32.dll")] static extern int DisplayConfigGetDeviceInfo(ref DISPLAYCONFIG_SOURCE_DEVICE_NAME request);
    [DllImport("user32.dll")] static extern int DisplayConfigGetDeviceInfo(ref DISPLAYCONFIG_SDR_WHITE_LEVEL request);
}
