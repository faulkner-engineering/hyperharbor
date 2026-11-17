using System.Text.Json.Serialization;

namespace HyperHarbor.Shared.Contracts.Vms;

/// <summary>Whether the guest accepted the stored administrator credential when the host last used it. Schema: VmAdminCredentialState.</summary>
public enum VmAdminCredentialState
{
    Unchecked,
    Working,
    Rejected,
}

/// <summary>The VM's stored administrator credential as the host last saw it; never the password. Schema: VmAdminCredential.</summary>
/// <param name="UserName">The account the host signs in to the guest with; null when none is stored.</param>
/// <param name="Managed">HyperHarbor created the account during an unattended install, so the host keeps its password from expiring.</param>
/// <param name="CheckedAt">When the host last signed in to the guest with the credential, or tried to.</param>
/// <param name="PasswordExpiresAt">When the account's password expires in a Windows guest; null when it never does or is not known.</param>
/// <param name="Detail">Why the guest refused the credential, when the state is rejected.</param>
public sealed record VmAdminCredential(
    [property: JsonIgnore(Condition = JsonIgnoreCondition.Never)] string? UserName,
    bool Managed,
    VmAdminCredentialState State,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.Never)] DateTimeOffset? CheckedAt,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.Never)] DateTimeOffset? PasswordExpiresAt,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.Never)] string? Detail);

/// <summary>Why a stored administrator credential needs attention. Schema: VmAdminCredentialAlertKind.</summary>
public enum VmAdminCredentialAlertKind
{
    Rejected,
    Expiring,
}

/// <summary>Set on a VM whose stored administrator credential needs attention. Schema: VmAdminCredentialAlert.</summary>
/// <param name="PasswordExpiresAt">When the account's password expires or expired, when the host knows.</param>
public sealed record VmAdminCredentialAlert(
    VmAdminCredentialAlertKind Kind,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.Never)] DateTimeOffset? PasswordExpiresAt);

/// <summary>Another administrator credential for a VM. Schema: SetVmAdminCredentialRequest.</summary>
/// <param name="Password">Stored on the host with DPAPI once the guest accepts it. Never logged.</param>
/// <param name="PasswordNeverExpires">Windows guests only: also turn off password expiry for the account.</param>
public sealed record SetVmAdminCredentialRequest(
    [property: JsonRequired] string UserName,
    [property: JsonRequired] string Password,
    bool PasswordNeverExpires = false)
{
    public const int MaxLength = 256;

    /// <summary>Keeps the password out of logs and exception messages.</summary>
    public override string ToString() => $"SetVmAdminCredentialRequest {{ UserName = {UserName}, PasswordNeverExpires = {PasswordNeverExpires} }}";
}

/// <summary>A check of the stored administrator credential. Schema: CheckVmAdminCredentialRequest.</summary>
/// <param name="PasswordNeverExpires">Windows guests only: also turn off password expiry for the stored account.</param>
public sealed record CheckVmAdminCredentialRequest(bool PasswordNeverExpires = false);
