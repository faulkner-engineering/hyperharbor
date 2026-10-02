using System.Text.Json;
using HyperHarbor.Host.Core.Security;

namespace HyperHarbor.Host.Core.Provisioning;

/// <summary>A User's verified local account on a VM.</summary>
public sealed record ProvisionedAccount(Guid VmId, Guid UserId, string AccountName, DateTimeOffset ProvisionedAt, DateTimeOffset VerifiedAt);

/// <summary>Records which (VM, User) pairs have a verified account. Contains no secrets.</summary>
public sealed class ProvisioningStore
{
    private const string FileName = "vm-provisioning.json";
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    private readonly string _path;
    private readonly object _gate = new();
    private List<ProvisionedAccount>? _accounts;

    public ProvisioningStore(string dataDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(dataDirectory);
        _path = Path.Combine(dataDirectory, FileName);
    }

    public ProvisionedAccount? Find(Guid vmId, Guid userId)
    {
        lock (_gate)
        {
            return Accounts().FirstOrDefault(account => account.VmId == vmId && account.UserId == userId);
        }
    }

    public void Save(ProvisionedAccount account)
    {
        ArgumentNullException.ThrowIfNull(account);
        lock (_gate)
        {
            var accounts = Accounts();
            accounts.RemoveAll(existing => existing.VmId == account.VmId && existing.UserId == account.UserId);
            accounts.Add(account);
            ProtectedFile.WriteAllBytes(_path, JsonSerializer.SerializeToUtf8Bytes(accounts, JsonOptions));
        }
    }

    private List<ProvisionedAccount> Accounts()
    {
        _accounts ??= File.Exists(_path)
            ? JsonSerializer.Deserialize<List<ProvisionedAccount>>(File.ReadAllText(_path), JsonOptions)
                ?? throw new InvalidDataException($"The provisioning file '{_path}' is invalid.")
            : [];
        return _accounts;
    }
}
