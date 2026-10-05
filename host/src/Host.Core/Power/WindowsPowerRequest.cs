using System.ComponentModel;
using System.Runtime.InteropServices;

namespace HyperHarbor.Host.Core.Power;

/// <summary>
/// <see cref="IPowerRequest"/> through PowerCreateRequest and PowerRequestSystemRequired, which keeps Windows from
/// sleeping when idle, including the short "unattended" timeout that applies after a Wake-on-LAN wake.
/// </summary>
public sealed class WindowsPowerRequest : IPowerRequest, IDisposable
{
    private const int PowerRequestSystemRequired = 1;
    private const uint PowerRequestContextVersion = 0;
    private const uint PowerRequestContextSimpleString = 0x1;

    private readonly object _gate = new();
    private IntPtr _handle;
    private string? _reason;

    public void Set(string reason)
    {
        lock (_gate)
        {
            if (_handle != IntPtr.Zero && _reason == reason)
            {
                return;
            }

            // The reason is fixed when the request is created, so a new reason needs a new request.
            var handle = Create(reason);
            if (!PowerSetRequest(handle, PowerRequestSystemRequired))
            {
                var error = Marshal.GetLastWin32Error();
                CloseHandle(handle);
                throw new Win32Exception(error, "The power request could not be set.");
            }

            ReleaseCurrent();
            _handle = handle;
            _reason = reason;
        }
    }

    public void Clear()
    {
        lock (_gate)
        {
            ReleaseCurrent();
        }
    }

    public void Dispose() => Clear();

    private void ReleaseCurrent()
    {
        if (_handle == IntPtr.Zero)
        {
            return;
        }

        PowerClearRequest(_handle, PowerRequestSystemRequired);
        CloseHandle(_handle);
        _handle = IntPtr.Zero;
        _reason = null;
    }

    private static IntPtr Create(string reason)
    {
        var text = Marshal.StringToHGlobalUni(reason);
        try
        {
            var context = new ReasonContext { Version = PowerRequestContextVersion, Flags = PowerRequestContextSimpleString, SimpleReasonString = text };
            var handle = PowerCreateRequest(ref context);
            if (handle == IntPtr.Zero || handle == new IntPtr(-1))
            {
                throw new Win32Exception(Marshal.GetLastWin32Error(), "The power request could not be created.");
            }

            return handle;
        }
        finally
        {
            Marshal.FreeHGlobal(text);
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ReasonContext
    {
        public uint Version;
        public uint Flags;
        public IntPtr SimpleReasonString;
        // The union's detailed form (a module, a resource ID, and strings) is larger; pad to its size.
        public IntPtr Padding1;
        public IntPtr Padding2;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr PowerCreateRequest(ref ReasonContext context);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool PowerSetRequest(IntPtr powerRequest, int requestType);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool PowerClearRequest(IntPtr powerRequest, int requestType);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseHandle(IntPtr handle);
}

/// <summary><see cref="IRemoteSessions"/> through the Remote Desktop Services API (WTSEnumerateSessions).</summary>
public sealed class WindowsRemoteSessions : IRemoteSessions
{
    private const int WtsActive = 0;
    private const int WtsClientProtocolType = 16;
    private const ushort RdpProtocol = 2;

    public bool AnyActive()
    {
        if (!WTSEnumerateSessionsW(IntPtr.Zero, 0, 1, out var sessions, out var count))
        {
            return false;
        }

        try
        {
            var size = Marshal.SizeOf<SessionInfo>();
            for (var i = 0; i < count; i++)
            {
                var session = Marshal.PtrToStructure<SessionInfo>(sessions + (i * size));
                if (session.State == WtsActive && IsRemote(session.SessionId))
                {
                    return true;
                }
            }

            return false;
        }
        finally
        {
            WTSFreeMemory(sessions);
        }
    }

    private static bool IsRemote(int sessionId)
    {
        if (!WTSQuerySessionInformationW(IntPtr.Zero, sessionId, WtsClientProtocolType, out var buffer, out var bytes))
        {
            return false;
        }

        try
        {
            return bytes >= sizeof(ushort) && (ushort)Marshal.ReadInt16(buffer) == RdpProtocol;
        }
        finally
        {
            WTSFreeMemory(buffer);
        }
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct SessionInfo
    {
        public int SessionId;
        public IntPtr WinStationName;
        public int State;
    }

    [DllImport("wtsapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool WTSEnumerateSessionsW(IntPtr server, int reserved, int version, out IntPtr sessionInfo, out int count);

    [DllImport("wtsapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool WTSQuerySessionInformationW(IntPtr server, int sessionId, int infoClass, out IntPtr buffer, out int bytesReturned);

    [DllImport("wtsapi32.dll")]
    private static extern void WTSFreeMemory(IntPtr memory);
}
