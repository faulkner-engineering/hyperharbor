using Microsoft.Management.Infrastructure;
using Microsoft.Win32;

namespace HyperHarbor.Host.Core.RemoteDesktop;

/// <summary>
/// <see cref="IRemoteDesktopSettings"/> from the registry and Windows Firewall; changes go through
/// Win32_TerminalServiceSetting, which sets fDenyTSConnections and the firewall rule group together.
/// </summary>
public sealed class WindowsRemoteDesktopSettings : IRemoteDesktopSettings
{
    public const int DefaultPort = 3389;

    /// <summary>The locale-independent name of the "Remote Desktop" firewall rule group.</summary>
    public const string FirewallRuleGroup = "@FirewallAPI.dll,-28752";

    private const string TerminalServerKey = @"SYSTEM\CurrentControlSet\Control\Terminal Server";
    private const string RdpTcpKey = TerminalServerKey + @"\WinStations\RDP-Tcp";
    private const string CurrentVersionKey = @"SOFTWARE\Microsoft\Windows NT\CurrentVersion";
    private const string TerminalServicesNamespace = @"root\cimv2\TerminalServices";

    public RemoteDesktopState Read()
    {
        using var machine = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);
        using var server = machine.OpenSubKey(TerminalServerKey);
        using var rdpTcp = machine.OpenSubKey(RdpTcpKey);
        using var version = machine.OpenSubKey(CurrentVersionKey);

        var editionId = version?.GetValue("EditionID") as string ?? string.Empty;
        var deny = server?.GetValue("fDenyTSConnections") is int value ? value : 1;
        var port = rdpTcp?.GetValue("PortNumber") is int number && number is > 0 and <= 65535 ? number : DefaultPort;
        return new RemoteDesktopState(editionId, ProductName(version), deny == 0, port, FirewallOpen());
    }

    public void Allow()
    {
        using var session = CimSession.Create(null);
        var setting = session.QueryInstances(TerminalServicesNamespace, "WQL", "SELECT * FROM Win32_TerminalServiceSetting").FirstOrDefault()
            ?? throw new InvalidOperationException("The Remote Desktop settings (Win32_TerminalServiceSetting) were not found.");
        using (setting)
        {
            using var parameters = new CimMethodParametersCollection
            {
                CimMethodParameter.Create("AllowTSConnections", 1u, CimType.UInt32, CimFlags.In),
                CimMethodParameter.Create("ModifyFirewallException", 1u, CimType.UInt32, CimFlags.In),
            };
            using var result = session.InvokeMethod(TerminalServicesNamespace, setting, "SetAllowTSConnections", parameters);
            if (result.ReturnValue?.Value is uint code && code != 0)
            {
                throw new InvalidOperationException($"Turning on Remote Desktop failed (SetAllowTSConnections returned {code}).");
            }
        }
    }

    /// <summary>Windows 11 still reports "Windows 10" in ProductName; the build number tells them apart.</summary>
    private static string ProductName(RegistryKey? version)
    {
        var name = version?.GetValue("ProductName") as string ?? "Windows";
        if (int.TryParse(version?.GetValue("CurrentBuildNumber") as string, out var build) && build >= 22000)
        {
            name = name.Replace("Windows 10", "Windows 11", StringComparison.Ordinal);
        }

        return name;
    }

    private static bool FirewallOpen()
    {
        try
        {
            var type = Type.GetTypeFromProgID("HNetCfg.FwPolicy2");
            if (type is null)
            {
                return false;
            }

            dynamic policy = Activator.CreateInstance(type)!;
            return (bool)policy.IsRuleGroupCurrentlyEnabled(FirewallRuleGroup);
        }
        catch (Exception ex) when (ex is System.Runtime.InteropServices.COMException or Microsoft.CSharp.RuntimeBinder.RuntimeBinderException or UnauthorizedAccessException)
        {
            return false;
        }
    }
}
