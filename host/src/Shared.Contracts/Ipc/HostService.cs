namespace HyperHarbor.Shared.Contracts.Ipc;

/// <summary>The Windows service that Install-HyperHarbor.ps1 registers.</summary>
public static class HostService
{
    public const string Name = "HyperHarborHost";
    public const string DisplayName = "HyperHarbor Host";

    /// <summary>The service's parameters key under HKEY_LOCAL_MACHINE, writable only by administrators.</summary>
    public const string ParametersKey = @"SYSTEM\CurrentControlSet\Services\" + Name + @"\Parameters";

    /// <summary>
    /// The SID of the user who installed the host. The service runs as LocalSystem, so this user's tray
    /// is admitted to the pipe and may read the logs and the audit trail.
    /// </summary>
    public const string TrayUserSidValue = "TrayUserSid";
}
