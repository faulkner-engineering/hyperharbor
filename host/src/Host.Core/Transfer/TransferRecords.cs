using HyperHarbor.Host.Core.GameAware;
using HyperHarbor.Host.Core.Provisioning;
using HyperHarbor.Host.Core.Users;
using HyperHarbor.Shared.Contracts.Vms;
using Microsoft.Extensions.Logging;
using Wire = HyperHarbor.Shared.Contracts.Transfers;

namespace HyperHarbor.Host.Core.Transfer;

/// <summary>A User's account on the VM, as the sending host recorded it. Matched on the receiving host by account name.</summary>
public sealed record TransferAccountRecord(
    string AccountName,
    GuestOsFamily GuestOs,
    string? SshHostKey,
    DateTimeOffset ProvisionedAt,
    DateTimeOffset VerifiedAt);

/// <summary>
/// What the host knows about a VM beyond its files, sent with it. The receiving host keeps the records in memory only until
/// the import, then stores them under the VM's ID there (a copy's new ID). ToString never includes the credential.
/// </summary>
/// <param name="AdminCredential">HyperHarbor's administrator account in the guest, used for setup and Performance mode.</param>
/// <param name="GamePolicy">The VM's game-aware action, when it has its own.</param>
public sealed record TransferRecords(IReadOnlyList<TransferAccountRecord> Accounts, GuestCredential? AdminCredential, GameVmPolicy? GamePolicy)
{
    public const int MaxAccounts = 64;

    public static TransferRecords None { get; } = new([], null, null);

    public override string ToString() =>
        $"TransferRecords {{ Accounts = {Accounts.Count}, AdminCredential = {(AdminCredential is null ? "none" : "set")}, GamePolicy = {GamePolicy?.Policy.ToString() ?? "none"} }}";
}

/// <summary>Converts <see cref="TransferRecords"/> to and from the TransferRecords schema of docs/api.yaml.</summary>
public static class TransferRecordsWire
{
    public static Wire.TransferRecords ToWire(this TransferRecords records) => new(
        records.Accounts.Select(account => new Wire.TransferAccount(account.AccountName, account.GuestOs, account.SshHostKey, account.ProvisionedAt, account.VerifiedAt)).ToList(),
        records.AdminCredential is { } credential ? new Wire.TransferCredential(credential.UserName, credential.Password) : null,
        records.GamePolicy is { } policy ? new Wire.TransferGamePolicy(GameAwareService.ToWire(policy.Policy), policy.AlwaysPause) : null);

    public static TransferRecords ToCore(this Wire.TransferRecords records) => new(
        records.Accounts.Select(account => new TransferAccountRecord(account.AccountName, account.GuestOs, account.SshHostKey, account.ProvisionedAt, account.VerifiedAt)).ToList(),
        records.AdminCredential is { } credential ? new GuestCredential(credential.UserName, credential.Password) : null,
        records.GamePolicy is { } policy ? new GameVmPolicy(GameAwareService.ToCore(policy.Action), policy.AlwaysPause) : null);
}

/// <summary>
/// Collects a VM's records on the sending host and stores them on the receiving host. Not sent: Performance mode (the GPU
/// partition is removed on the way; the preview warns), a finished unattended install (nothing reads it after setup), and
/// backup policies (the backup locations belong to the sending host; the preview warns).
/// </summary>
public sealed class TransferRecordService
{
    private readonly ProvisioningStore _accounts;
    private readonly VmCredentialStore _credentials;
    private readonly GameAwareStore _gameAware;
    private readonly UserStore _users;
    private readonly ILogger<TransferRecordService> _logger;

    public TransferRecordService(
        ProvisioningStore accounts, VmCredentialStore credentials, GameAwareStore gameAware, UserStore users, ILogger<TransferRecordService> logger)
    {
        _accounts = accounts;
        _credentials = credentials;
        _gameAware = gameAware;
        _users = users;
        _logger = logger;
    }

    public TransferRecords Collect(Guid vmId)
    {
        var accounts = _accounts.ForVm(vmId)
            .Select(account => new TransferAccountRecord(account.AccountName, account.GuestOs, account.SshHostKey, account.ProvisionedAt, account.VerifiedAt))
            .ToList();
        var policy = _gameAware.Load().VmPolicies.TryGetValue(vmId.ToString("D"), out var own) ? own : null;
        return new TransferRecords(accounts, _credentials.Find(vmId), policy);
    }

    /// <summary>Stores the records under <paramref name="vmId"/> on this host.</summary>
    /// <returns>Warnings for the user, such as an account whose User this host does not have.</returns>
    public IReadOnlyList<string> Apply(Guid vmId, TransferRecords records)
    {
        ArgumentNullException.ThrowIfNull(records);
        var warnings = new List<string>();
        var users = _users.List();
        foreach (var account in records.Accounts)
        {
            if (users.FirstOrDefault(user => string.Equals(user.VmAccountName, account.AccountName, StringComparison.OrdinalIgnoreCase)) is { } user)
            {
                _accounts.Save(new ProvisionedAccount(vmId, user.UserId, user.VmAccountName, account.ProvisionedAt, account.VerifiedAt, account.GuestOs, account.SshHostKey));
            }
            else
            {
                warnings.Add($"The VM's account {account.AccountName} belongs to a User this host does not have. Set the VM up again on this host to connect.");
            }
        }

        if (records.AdminCredential is { } credential)
        {
            _credentials.Save(vmId, credential);
        }

        if (records.GamePolicy is { } policy)
        {
            _gameAware.Update(settings => settings with
            {
                VmPolicies = new Dictionary<string, GameVmPolicy>(settings.VmPolicies) { [vmId.ToString("D")] = policy },
            });
        }

        _logger.LogInformation("Stored the records of transferred VM {VmId}: {Records}.", vmId, records);
        return warnings;
    }
}
