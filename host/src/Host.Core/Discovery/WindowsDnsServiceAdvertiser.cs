using System.ComponentModel;
using System.Runtime.InteropServices;
using Microsoft.Extensions.Logging;

namespace HyperHarbor.Host.Core.Discovery;

/// <summary>
/// Publishes DNS-SD records through the Windows DNS client (DnsServiceRegister, Windows 10 1809 and later).
/// Using the operating system's responder avoids competing with it for UDP port 5353.
/// </summary>
public sealed class WindowsDnsServiceAdvertiser : IServiceAdvertiser
{
    private readonly ILogger<WindowsDnsServiceAdvertiser> _logger;

    public WindowsDnsServiceAdvertiser(ILogger<WindowsDnsServiceAdvertiser> logger)
    {
        _logger = logger;
    }

    public async Task<IAsyncDisposable> AdvertiseAsync(ServiceAdvertisement advertisement, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(advertisement);

        var registration = new Registration(advertisement, _logger);
        try
        {
            await registration.RegisterAsync(cancellationToken).ConfigureAwait(false);
            return registration;
        }
        catch
        {
            await registration.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    /// <summary>
    /// Owns the native instance, request block, and callback for one registration.
    /// </summary>
    private sealed class Registration : IAsyncDisposable
    {
        private static readonly TimeSpan DeregisterTimeout = TimeSpan.FromSeconds(5);

        private readonly ServiceAdvertisement _advertisement;
        private readonly ILogger _logger;
        private readonly NativeMethods.RegisterCompleteCallback _callback;
        private IntPtr _instance;
        private IntPtr _request;
        private TaskCompletionSource<uint> _completion = NewCompletion();
        private bool _registered;

        public Registration(ServiceAdvertisement advertisement, ILogger logger)
        {
            _advertisement = advertisement;
            _logger = logger;

            // Held in a field so the delegate is not collected while native code can still call it.
            _callback = OnComplete;
        }

        public async Task RegisterAsync(CancellationToken cancellationToken)
        {
            var keys = _advertisement.Properties.Keys.ToArray();
            var values = keys.Select(key => _advertisement.Properties[key]).ToArray();

            _instance = NativeMethods.DnsServiceConstructInstance(
                _advertisement.FullInstanceName,
                _advertisement.FullHostName,
                IntPtr.Zero,
                IntPtr.Zero,
                _advertisement.Port,
                0,
                0,
                (uint)keys.Length,
                keys,
                values);
            if (_instance == IntPtr.Zero)
            {
                throw new InvalidOperationException("DnsServiceConstructInstance failed.");
            }

            _request = Marshal.AllocHGlobal(Marshal.SizeOf<NativeMethods.DnsServiceRegisterRequest>());
            Marshal.StructureToPtr(
                new NativeMethods.DnsServiceRegisterRequest
                {
                    Version = NativeMethods.DnsQueryRequestVersion1,
                    InterfaceIndex = 0,
                    ServiceInstance = _instance,
                    RegisterCompletionCallback = Marshal.GetFunctionPointerForDelegate(_callback),
                    QueryContext = IntPtr.Zero,
                    Credentials = IntPtr.Zero,
                    UnicastEnabled = 0,
                },
                _request,
                fDeleteOld: false);

            var status = NativeMethods.DnsServiceRegister(_request, IntPtr.Zero);
            if (status != NativeMethods.DnsRequestPending)
            {
                throw new Win32Exception((int)status, $"DnsServiceRegister failed with status {status}.");
            }

            var result = await _completion.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
            if (result != 0)
            {
                throw new Win32Exception((int)result, $"mDNS registration of {_advertisement.FullInstanceName} failed with status {result}.");
            }

            _registered = true;
            _logger.LogInformation(
                "Advertising {Instance} on port {Port} via mDNS.",
                _advertisement.FullInstanceName,
                _advertisement.Port);
        }

        public async ValueTask DisposeAsync()
        {
            if (_registered)
            {
                _completion = NewCompletion();
                var status = NativeMethods.DnsServiceDeRegister(_request, IntPtr.Zero);
                if (status == NativeMethods.DnsRequestPending)
                {
                    try
                    {
                        await _completion.Task.WaitAsync(DeregisterTimeout).ConfigureAwait(false);
                    }
                    catch (TimeoutException)
                    {
                        _logger.LogWarning("Timed out withdrawing the mDNS advertisement for {Instance}.", _advertisement.FullInstanceName);
                    }
                }
                else
                {
                    _logger.LogWarning("DnsServiceDeRegister returned status {Status}.", status);
                }

                _registered = false;
                _logger.LogInformation("Withdrew mDNS advertisement for {Instance}.", _advertisement.FullInstanceName);
            }

            if (_request != IntPtr.Zero)
            {
                Marshal.FreeHGlobal(_request);
                _request = IntPtr.Zero;
            }

            if (_instance != IntPtr.Zero)
            {
                NativeMethods.DnsServiceFreeInstance(_instance);
                _instance = IntPtr.Zero;
            }

            GC.KeepAlive(_callback);
        }

        private void OnComplete(uint status, IntPtr context, IntPtr instance)
        {
            // The callback receives its own copy of the instance, which the caller must free.
            if (instance != IntPtr.Zero)
            {
                NativeMethods.DnsServiceFreeInstance(instance);
            }

            _completion.TrySetResult(status);
        }

        private static TaskCompletionSource<uint> NewCompletion() =>
            new(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    private static class NativeMethods
    {
        public const uint DnsQueryRequestVersion1 = 1;
        public const uint DnsRequestPending = 9506;

        [UnmanagedFunctionPointer(CallingConvention.Winapi)]
        public delegate void RegisterCompleteCallback(uint status, IntPtr queryContext, IntPtr instance);

        [StructLayout(LayoutKind.Sequential)]
        public struct DnsServiceRegisterRequest
        {
            public uint Version;
            public uint InterfaceIndex;
            public IntPtr ServiceInstance;
            public IntPtr RegisterCompletionCallback;
            public IntPtr QueryContext;
            public IntPtr Credentials;
            public int UnicastEnabled;
        }

        [DllImport("dnsapi.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
        public static extern IntPtr DnsServiceConstructInstance(
            string serviceName,
            string hostName,
            IntPtr ip4,
            IntPtr ip6,
            ushort port,
            ushort priority,
            ushort weight,
            uint propertiesCount,
            [MarshalAs(UnmanagedType.LPArray, ArraySubType = UnmanagedType.LPWStr)] string[] keys,
            [MarshalAs(UnmanagedType.LPArray, ArraySubType = UnmanagedType.LPWStr)] string[] values);

        [DllImport("dnsapi.dll", ExactSpelling = true)]
        public static extern uint DnsServiceRegister(IntPtr request, IntPtr cancel);

        [DllImport("dnsapi.dll", ExactSpelling = true)]
        public static extern uint DnsServiceDeRegister(IntPtr request, IntPtr cancel);

        [DllImport("dnsapi.dll", ExactSpelling = true)]
        public static extern void DnsServiceFreeInstance(IntPtr instance);
    }
}
