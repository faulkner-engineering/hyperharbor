using System.Text.Json.Serialization;

namespace HyperHarbor.Shared.Contracts.Vms;

/// <summary>An open administrator window in a Windows guest. Schema: GuestAdminWindow.</summary>
/// <param name="AccountName">The User's local administrator account in the guest, for example "hha-owner".</param>
/// <param name="Passphrase">The one-time passphrase to type into the guest's User Account Control prompt.</param>
/// <param name="ExpiresAt">When the window ends and the account is disabled.</param>
public sealed record GuestAdminWindow(string AccountName, string Passphrase, DateTimeOffset ExpiresAt)
{
    /// <summary>Keeps the passphrase out of logs and exception messages.</summary>
    public override string ToString() => $"GuestAdminWindow {{ AccountName = {AccountName}, ExpiresAt = {ExpiresAt:u} }}";
}

/// <summary>The calling User's administrator window in a VM, without the passphrase. Schema: GuestAdminStatus.</summary>
/// <param name="ExpiresAt">When the open window ends; null when none is open.</param>
/// <param name="ClosePending">A window ended while the guest could not be reached; the account is disabled when it answers.</param>
public sealed record GuestAdminStatus(
    string AccountName,
    bool Open,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.Never)] DateTimeOffset? ExpiresAt,
    bool ClosePending);

/// <summary>How long an administrator window stays open. Schema: GuestAdminSettings.</summary>
public sealed record GuestAdminSettings([property: JsonRequired] int WindowMinutes)
{
    public const int MinimumMinutes = 2;
    public const int MaximumMinutes = 30;
    public const int DefaultMinutes = 5;
}
