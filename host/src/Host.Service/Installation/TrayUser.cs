using System.Security.Principal;
using HyperHarbor.Shared.Contracts.Ipc;
using Microsoft.Win32;

namespace HyperHarbor.Host.Service.Installation;

/// <summary>
/// The user whose tray may connect when the host runs as an installed service. A console run admits the
/// account it runs as instead, so this is only needed for the service, which runs as LocalSystem.
/// </summary>
public static class TrayUser
{
    /// <summary>Configuration key that overrides the registry (tests, or a service run with another layout).</summary>
    public const string ConfigurationKey = "Tray:UserSid";

    /// <summary>
    /// The configured tray user: <see cref="ConfigurationKey"/>, else, for a Windows service, the value
    /// Install-HyperHarbor.ps1 wrote under <see cref="HostService.ParametersKey"/>. Null when neither is set.
    /// </summary>
    /// <exception cref="InvalidOperationException">The value is not a local or domain user account SID.</exception>
    public static SecurityIdentifier? Resolve(IConfiguration configuration, bool isWindowsService)
    {
        var value = configuration[ConfigurationKey];
        if (string.IsNullOrWhiteSpace(value) && isWindowsService)
        {
            using var key = Registry.LocalMachine.OpenSubKey(HostService.ParametersKey);
            value = key?.GetValue(HostService.TrayUserSidValue) as string;
        }

        return string.IsNullOrWhiteSpace(value) ? null : Parse(value);
    }

    /// <summary>
    /// Accepts only a user account SID. A group such as Users or Everyone would admit every local user to
    /// the pipe, where pairing PINs are shown.
    /// </summary>
    internal static SecurityIdentifier Parse(string value)
    {
        SecurityIdentifier sid;
        try
        {
            sid = new SecurityIdentifier(value.Trim());
        }
        catch (ArgumentException ex)
        {
            throw new InvalidOperationException($"The tray user \"{value}\" is not a SID.", ex);
        }

        if (!sid.IsAccountSid() || IsWellKnownGroup(sid))
        {
            throw new InvalidOperationException($"The tray user {sid} is not a user account.");
        }

        return sid;
    }

    private static bool IsWellKnownGroup(SecurityIdentifier sid) =>
        Enum.GetValues<WellKnownSidType>().Any(type =>
        {
            try
            {
                return sid.IsWellKnown(type);
            }
            catch (ArgumentException)
            {
                return false;
            }
        });
}
