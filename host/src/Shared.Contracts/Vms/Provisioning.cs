using System.Text.Json.Serialization;

namespace HyperHarbor.Shared.Contracts.Vms;

/// <summary>One-time provisioning of the User's VM account. Schema: ProvisionVmRequest.</summary>
/// <param name="AdminUserName">A local administrator in the guest.</param>
/// <param name="AdminPassword">Stored on the host with DPAPI. Never logged.</param>
/// <param name="EnableRemoteDesktop">Also enable Remote Desktop and its firewall rule in the guest.</param>
public sealed record ProvisionVmRequest(
    [property: JsonRequired] string AdminUserName,
    [property: JsonRequired] string AdminPassword,
    bool EnableRemoteDesktop = true)
{
    /// <summary>Keeps the admin password out of logs and exception messages.</summary>
    public override string ToString() => $"ProvisionVmRequest {{ AdminUserName = {AdminUserName}, EnableRemoteDesktop = {EnableRemoteDesktop} }}";
}

/// <summary>Result of provisioning. Schema: VmProvisioning.</summary>
/// <param name="AccountName">The User's local account in the guest, for example hh-owner.</param>
public sealed record VmProvisioning(Guid VmId, string AccountName, DateTimeOffset ProvisionedAt);

/// <summary>Remote Desktop credentials. Schema: VmConnection.</summary>
/// <param name="UserName">For example ".\hh-owner".</param>
/// <param name="ExpiresAt">End of the reuse window.</param>
public sealed record VmConnection(string UserName, string Password, string Address, int Port, DateTimeOffset ExpiresAt)
{
    /// <summary>Keeps the password out of logs and exception messages.</summary>
    public override string ToString() => $"VmConnection {{ UserName = {UserName}, Address = {Address}, Port = {Port}, ExpiresAt = {ExpiresAt:u} }}";
}
