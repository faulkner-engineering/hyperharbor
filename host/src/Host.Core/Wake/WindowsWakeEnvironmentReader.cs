using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using Microsoft.Management.Infrastructure;
using Microsoft.Win32;

namespace HyperHarbor.Host.Core.Wake;

public interface IWakeEnvironmentReader
{
    Task<WakeEnvironment> ReadAsync(CancellationToken cancellationToken);
}

/// <summary>
/// Reads Wake-on-LAN state without administrator rights: adapters and addresses from CIM
/// (root\StandardCimv2), driver keywords from the registry, wake-armed devices from powercfg,
/// and sleep capabilities from the power management API.
/// </summary>
public sealed class WindowsWakeEnvironmentReader : IWakeEnvironmentReader
{
    private const string NetworkNamespace = @"root\StandardCimv2";
    private const string NetworkClassKey = @"SYSTEM\CurrentControlSet\Control\Class\{4d36e972-e325-11ce-bfc1-08002be10318}";
    private const string PowerKey = @"SYSTEM\CurrentControlSet\Control\Session Manager\Power";

    // NDIS_PHYSICAL_MEDIUM values.
    private const uint Medium802_3 = 14;

    // NET_IF_MEDIA_CONNECT_STATE.
    private const uint MediaConnected = 1;

    public Task<WakeEnvironment> ReadAsync(CancellationToken cancellationToken) =>
        Task.Run(Read, cancellationToken);

    private static WakeEnvironment Read() =>
        new(ReadAdapters(), ReadWakeArmedDevices(), PowerSettings.Read());

    private static List<NetworkAdapterState> ReadAdapters()
    {
        using var session = CimSession.Create(null);

        var addresses = session.QueryInstances(NetworkNamespace, "WQL", "SELECT InterfaceIndex, IPAddress, PrefixLength FROM MSFT_NetIPAddress WHERE AddressFamily = 2")
            .Select(instance =>
            {
                using (instance)
                {
                    var index = Convert.ToUInt32(instance.CimInstanceProperties["InterfaceIndex"].Value);
                    return IPAddress.TryParse(instance.CimInstanceProperties["IPAddress"].Value as string, out var ip)
                        ? (Index: index, Assignment: new Ipv4Assignment(ip, Convert.ToInt32(instance.CimInstanceProperties["PrefixLength"].Value)))
                        : (Index: index, Assignment: (Ipv4Assignment?)null);
                }
            })
            .Where(entry => entry.Assignment is not null && entry.Assignment.Address.AddressFamily == AddressFamily.InterNetwork)
            .ToLookup(entry => entry.Index, entry => entry.Assignment!);

        var wakeKeywords = ReadWakeKeywords();
        var adapters = new List<NetworkAdapterState>();
        foreach (var instance in session.QueryInstances(NetworkNamespace, "WQL", "SELECT * FROM MSFT_NetAdapter"))
        {
            using (instance)
            {
                string? Text(string name) => instance.CimInstanceProperties[name]?.Value as string;
                bool Flag(string name) => instance.CimInstanceProperties[name]?.Value is true;
                uint Number(string name) => Convert.ToUInt32(instance.CimInstanceProperties[name]?.Value ?? 0u);

                var mac = (Text("PermanentAddress") ?? Text("MacAddress") ?? string.Empty).Replace("-", string.Empty).ToUpperInvariant();
                if (mac.Length != 12)
                {
                    continue;
                }

                var interfaceGuid = Text("InterfaceGuid") ?? string.Empty;
                var medium = Number("NdisPhysicalMedium");
                var isPhysical = Flag("HardwareInterface") && !Flag("Virtual");

                adapters.Add(new NetworkAdapterState(
                    Text("Name") ?? string.Empty,
                    Text("InterfaceDescription") ?? string.Empty,
                    mac,
                    isPhysical,
                    IsWired: medium == Medium802_3,
                    IsConnected: Number("MediaConnectState") == MediaConnected,
                    addresses[Number("InterfaceIndex")].ToList(),
                    wakeKeywords.GetValueOrDefault(interfaceGuid.ToUpperInvariant())));
            }
        }

        return adapters;
    }

    /// <summary>The standardized *WakeOnMagicPacket keyword ("1" enabled, "0" disabled) by NetCfgInstanceId.</summary>
    private static Dictionary<string, bool?> ReadWakeKeywords()
    {
        var result = new Dictionary<string, bool?>(StringComparer.OrdinalIgnoreCase);
        using var classKey = Registry.LocalMachine.OpenSubKey(NetworkClassKey);
        if (classKey is null)
        {
            return result;
        }

        foreach (var name in classKey.GetSubKeyNames())
        {
            try
            {
                using var adapterKey = classKey.OpenSubKey(name);
                if (adapterKey?.GetValue("NetCfgInstanceId") is not string instanceId)
                {
                    continue;
                }

                result[instanceId.ToUpperInvariant()] = adapterKey.GetValue("*WakeOnMagicPacket") switch
                {
                    "1" => true,
                    "0" => false,
                    _ => null,
                };
            }
            catch (System.Security.SecurityException)
            {
                // Some subkeys (for example "Properties") are not readable without administrator rights.
            }
        }

        return result;
    }

    /// <summary>Device names from "powercfg /devicequery wake_armed".</summary>
    private static HashSet<string> ReadWakeArmedDevices()
    {
        var start = new ProcessStartInfo("powercfg.exe", "/devicequery wake_armed")
        {
            RedirectStandardOutput = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };

        using var process = Process.Start(start)!;
        var output = process.StandardOutput.ReadToEnd();
        process.WaitForExit();

        return output
            .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>Sleep capabilities and power policy from powrprof.dll and the registry.</summary>
    internal static class PowerSettings
    {
        // Subgroup "No subgroup" and setting "Networking connectivity in Standby".
        private static readonly Guid NoSubgroup = new("fea3413e-7e05-4911-9a71-700331f1c294");
        private static readonly Guid ConnectivityInStandby = new("f15576e8-98b7-4186-b944-eafa664402d9");
        private const int SystemPowerCapabilitiesLevel = 4;

        public static PowerState Read()
        {
            var capabilities = default(SystemPowerCapabilities);
            var status = CallNtPowerInformation(
                SystemPowerCapabilitiesLevel,
                IntPtr.Zero,
                0,
                out capabilities,
                (uint)Marshal.SizeOf<SystemPowerCapabilities>());

            uint? connectivity = null;
            if (PowerGetActiveScheme(IntPtr.Zero, out var schemePointer) == 0)
            {
                try
                {
                    var scheme = Marshal.PtrToStructure<Guid>(schemePointer);
                    var subgroup = NoSubgroup;
                    var setting = ConnectivityInStandby;
                    if (PowerReadACValueIndex(IntPtr.Zero, ref scheme, ref subgroup, ref setting, out var value) == 0)
                    {
                        connectivity = value;
                    }
                }
                finally
                {
                    LocalFree(schemePointer);
                }
            }

            using var powerKey = Registry.LocalMachine.OpenSubKey(PowerKey);
            bool? fastStartup = powerKey?.GetValue("HiberbootEnabled") is int hiberboot ? hiberboot != 0 : null;

            return status == 0
                ? new PowerState(capabilities.SystemS3, capabilities.AoAc, capabilities.AoAcConnectivitySupported, connectivity, fastStartup)
                : new PowerState(false, false, false, connectivity, fastStartup);
        }

        /// <summary>SYSTEM_POWER_CAPABILITIES (76 bytes). Only the leading fields are read.</summary>
        [StructLayout(LayoutKind.Sequential)]
        private struct SystemPowerCapabilities
        {
            [MarshalAs(UnmanagedType.U1)] public bool PowerButtonPresent;
            [MarshalAs(UnmanagedType.U1)] public bool SleepButtonPresent;
            [MarshalAs(UnmanagedType.U1)] public bool LidPresent;
            [MarshalAs(UnmanagedType.U1)] public bool SystemS1;
            [MarshalAs(UnmanagedType.U1)] public bool SystemS2;
            [MarshalAs(UnmanagedType.U1)] public bool SystemS3;
            [MarshalAs(UnmanagedType.U1)] public bool SystemS4;
            [MarshalAs(UnmanagedType.U1)] public bool SystemS5;
            [MarshalAs(UnmanagedType.U1)] public bool HiberFilePresent;
            [MarshalAs(UnmanagedType.U1)] public bool FullWake;
            [MarshalAs(UnmanagedType.U1)] public bool VideoDimPresent;
            [MarshalAs(UnmanagedType.U1)] public bool ApmPresent;
            [MarshalAs(UnmanagedType.U1)] public bool UpsPresent;
            [MarshalAs(UnmanagedType.U1)] public bool ThermalControl;
            [MarshalAs(UnmanagedType.U1)] public bool ProcessorThrottle;
            public byte ProcessorMinThrottle;
            public byte ProcessorMaxThrottle;
            [MarshalAs(UnmanagedType.U1)] public bool FastSystemS4;
            [MarshalAs(UnmanagedType.U1)] public bool Hiberboot;
            [MarshalAs(UnmanagedType.U1)] public bool WakeAlarmPresent;
            [MarshalAs(UnmanagedType.U1)] public bool AoAc;
            [MarshalAs(UnmanagedType.U1)] public bool DiskSpinDown;
            public byte HiberFileType;
            [MarshalAs(UnmanagedType.U1)] public bool AoAcConnectivitySupported;
            [MarshalAs(UnmanagedType.ByValArray, SizeConst = 6)] public byte[] Spare3;
            [MarshalAs(UnmanagedType.U1)] public bool SystemBatteriesPresent;
            [MarshalAs(UnmanagedType.U1)] public bool BatteriesAreShortTerm;
            [MarshalAs(UnmanagedType.ByValArray, SizeConst = 6)] public uint[] BatteryScale;
            public int AcOnLineWake;
            public int SoftLidWake;
            public int RtcWake;
            public int MinDeviceWakeState;
            public int DefaultLowLatencyWake;
        }

        [DllImport("powrprof.dll")]
        private static extern uint CallNtPowerInformation(int level, IntPtr inputBuffer, uint inputLength, out SystemPowerCapabilities output, uint outputLength);

        [DllImport("powrprof.dll")]
        private static extern uint PowerGetActiveScheme(IntPtr rootPowerKey, out IntPtr activePolicyGuid);

        [DllImport("powrprof.dll")]
        private static extern uint PowerReadACValueIndex(IntPtr rootPowerKey, ref Guid scheme, ref Guid subgroup, ref Guid setting, out uint value);

        [DllImport("kernel32.dll")]
        private static extern IntPtr LocalFree(IntPtr memory);
    }
}
