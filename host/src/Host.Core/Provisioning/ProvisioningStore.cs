using System.Text.Json;
using System.Text.Json.Serialization;
using HyperHarbor.Host.Core.Security;
using HyperHarbor.Shared.Contracts.Vms;

namespace HyperHarbor.Host.Core.Provisioning;

/// <summary>A User's verified local account on a VM.</summary>
/// <param name="GuestOs">Records written before Linux support have no value and are Windows guests.</param>
/// <param name="SshHostKey">Linux: the pinned SHA-256 fingerprint of the guest's SSH host key.</param>
public sealed record ProvisionedAccount(
    Guid VmId,
    Guid UserId,
    string AccountName,
    DateTimeOffset ProvisionedAt,
    DateTimeOffset VerifiedAt,
    GuestOsFamily GuestOs = GuestOsFamily.Windows,
    string? SshHostKey = null)
{
    /// <summary>How to reach the guest; Linux guests are reached over SSH at <paramref name="address"/>.</summary>
    public GuestTarget ToTarget(string? address) => new(VmId, GuestOs, address, SshHostKey);
}

/// <summary>Records which (VM, User) pairs have a verified account. Contains no secrets.</summary>
public sealed class ProvisioningStore
{
    private const string FileName = "vm-provisioning.json";
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
    };

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

    /// <summary>Every User's account on the VM (sent with it when it moves to another host).</summary>
    public IReadOnlyList<ProvisionedAccount> ForVm(Guid vmId)
    {
        lock (_gate)
        {
            return Accounts().Where(account => account.VmId == vmId).ToList();
        }
    }

    /// <summary>Whether any User has an account on the VM (recorded in a backup's manifest).</summary>
    public bool AnyAccount(Guid vmId)
    {
        lock (_gate)
        {
            return Accounts().Any(account => account.VmId == vmId);
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

    /// <summary>Forgets every User's account on a deleted VM.</summary>
    /// <returns>Whether any account was recorded.</returns>
    public bool Remove(Guid vmId)
    {
        lock (_gate)
        {
            var accounts = Accounts();
            if (accounts.RemoveAll(account => account.VmId == vmId) == 0)
            {
                return false;
            }

            ProtectedFile.WriteAllBytes(_path, JsonSerializer.SerializeToUtf8Bytes(accounts, JsonOptions));
            return true;
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
