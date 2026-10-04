using System.Text.Json.Serialization;

namespace HyperHarbor.Shared.Contracts.Vms;

/// <summary>One-time provisioning of the User's VM account. Schema: ProvisionVmRequest.</summary>
/// <param name="AdminUserName">A local administrator in the guest.</param>
/// <param name="AdminPassword">Stored on the host with DPAPI. Never logged.</param>
/// <param name="EnableRemoteDesktop">Windows: enable Remote Desktop and its firewall rule. Linux: install and enable xrdp.</param>
/// <param name="InstallDesktop">Linux only: install Xfce when the guest has no desktop environment.</param>
/// <param name="TrustNewHostKey">Linux only: accept an SSH host key that differs from the one pinned at the last setup.</param>
public sealed record ProvisionVmRequest(
    [property: JsonRequired] string AdminUserName,
    [property: JsonRequired] string AdminPassword,
    bool EnableRemoteDesktop = true,
    bool InstallDesktop = false,
    bool TrustNewHostKey = false)
{
    /// <summary>Keeps the admin password out of logs and exception messages.</summary>
    public override string ToString() =>
        $"ProvisionVmRequest {{ AdminUserName = {AdminUserName}, EnableRemoteDesktop = {EnableRemoteDesktop}, InstallDesktop = {InstallDesktop}, TrustNewHostKey = {TrustNewHostKey} }}";
}

/// <summary>Result of provisioning. Schema: VmProvisioning.</summary>
/// <param name="AccountName">The User's local account in the guest, for example hh-owner.</param>
public sealed record VmProvisioning(Guid VmId, string AccountName, DateTimeOffset ProvisionedAt);

/// <summary>Remote Desktop credentials. Schema: VmConnection.</summary>
/// <param name="UserName">The plain account name, for example "hh-owner"; it is the guest's local account.</param>
/// <param name="ExpiresAt">End of the reuse window.</param>
/// <param name="PerformanceMode">The client tunes the connection for a LAN (connection type LAN, auto-detection off).</param>
/// <param name="GuestOs">Linux guests use xrdp, which signs in over TLS instead of CredSSP.</param>
public sealed record VmConnection(string UserName, string Password, string Address, int Port, DateTimeOffset ExpiresAt, GuestOsFamily GuestOs, bool PerformanceMode = false)
{
    /// <summary>Keeps the password out of logs and exception messages.</summary>
    public override string ToString() => $"VmConnection {{ UserName = {UserName}, Address = {Address}, Port = {Port}, ExpiresAt = {ExpiresAt:u}, GuestOs = {GuestOs} }}";
}
