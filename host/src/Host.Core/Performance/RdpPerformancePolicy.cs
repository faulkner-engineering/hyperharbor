namespace HyperHarbor.Host.Core.Performance;

/// <summary>One DWORD registry value written in the guest.</summary>
/// <param name="Key">A PowerShell registry path, for example HKLM:\SOFTWARE\Policies\....</param>
/// <param name="Policy">The TerminalServer.admx policy that owns the value, or null for values outside any ADMX.</param>
public sealed record GuestRegistryValue(string Key, string Name, int Value, string? Policy);

/// <summary>
/// The Remote Desktop Session Host policy values Performance mode writes in the guest. Value names and
/// values come from TerminalServer.admx (Windows 11); RdpPerformancePolicyTests check them against the
/// ADMX on the machine running the tests.
/// </summary>
public static class RdpPerformancePolicy
{
    public const string PolicyKey = @"HKLM:\SOFTWARE\Policies\Microsoft\Windows NT\Terminal Services";
    public const string WinStationsKey = @"HKLM:\SYSTEM\CurrentControlSet\Control\Terminal Server\WinStations";

    /// <summary>TS_SERVER_IMAGE_QUALITY enum: Lossless 1, High 2, Medium 3, Low 4.</summary>
    public const int ImageQualityHigh = 2;

    /// <summary>TS_SERVER_COMPRESSOR enum: 0 is "Do not use an RDP compression algorithm".</summary>
    public const int NoCompression = 0;

    /// <summary>KB 2885213: the frame interval in units that give 60 fps at 15 (the default limit is 30 fps).</summary>
    public const int DwmFrameInterval60Fps = 15;

    /// <param name="hardwareEncoding">Experimental: prefer the GPU for H.264/AVC encoding. Off writes the disabled value (0).</param>
    public static IReadOnlyList<GuestRegistryValue> Values(bool hardwareEncoding) =>
    [
        // "Use hardware graphics adapters for all Remote Desktop Services sessions".
        new(PolicyKey, "bEnumerateHWBeforeSW", 1, "TS_DX_USE_FULL_HWGPU"),

        // "Prioritize H.264/AVC 444 graphics mode for Remote Desktop Connections".
        new(PolicyKey, "AVC444ModePreferred", 1, "TS_SERVER_AVC444_MODE_PREFERRED"),

        // "Configure H.264/AVC hardware encoding for Remote Desktop Connections".
        new(PolicyKey, "AVCHardwareEncodePreferred", hardwareEncoding ? 1 : 0, "TS_SERVER_AVC_HW_ENCODE_PREFERRED"),

        // "Configure image quality for RemoteFX Adaptive Graphics": High.
        new(PolicyKey, "ImageQuality", ImageQualityHigh, "TS_SERVER_IMAGE_QUALITY"),

        // "Configure compression for RemoteFX data": Do not use an RDP compression algorithm.
        new(PolicyKey, "MaxCompressionLevel", NoCompression, "TS_SERVER_COMPRESSOR"),

        // Not a policy: KB 2885213 raises the session frame rate limit to 60 fps.
        new(WinStationsKey, "DWMFRAMEINTERVAL", DwmFrameInterval60Fps, null),
    ];
}
