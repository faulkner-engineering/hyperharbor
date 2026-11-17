using System.Text.Json;
using System.Text.Json.Serialization;
using HyperHarbor.Host.Core.Security;
using HyperHarbor.Shared.Contracts.Vms;

namespace HyperHarbor.Host.Core.Provisioning;

/// <summary>What the guest answered when the host last signed in with a VM's stored administrator credential.</summary>
/// <param name="PasswordExpiresAt">Windows: when the account's password expires; null when it never does or was not read.</param>
/// <param name="Detail">The guest's reason when it refused the credential; cleaned of secrets before it is stored.</param>
public sealed record AdminCredentialStatus(
    Guid VmId,
    VmAdminCredentialState State,
    DateTimeOffset CheckedAt,
    DateTimeOffset? PasswordExpiresAt = null,
    string? Detail = null);

/// <summary>
/// Records, per VM, whether the guest accepted the stored administrator credential when the host last used it and when
/// the account's password expires (vm-admin-credential-status.json). The VM list reads it for its warning, so nothing
/// has to ask the guest to show one. Contains no secrets.
/// </summary>
public sealed class VmAdminCredentialStatusStore
{
    private const string FileName = "vm-admin-credential-status.json";
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
    };

    private readonly string _path;
    private readonly object _gate = new();
    private Dictionary<Guid, AdminCredentialStatus>? _statuses;

    public VmAdminCredentialStatusStore(string dataDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(dataDirectory);
        _path = Path.Combine(dataDirectory, FileName);
    }

    public AdminCredentialStatus? Find(Guid vmId)
    {
        lock (_gate)
        {
            return Statuses().GetValueOrDefault(vmId);
        }
    }

    public void Save(AdminCredentialStatus status)
    {
        ArgumentNullException.ThrowIfNull(status);
        lock (_gate)
        {
            var statuses = Statuses();
            if (statuses.GetValueOrDefault(status.VmId) == status)
            {
                return;
            }

            statuses[status.VmId] = status;
            Write(statuses);
        }
    }

    /// <summary>
    /// The guest accepted the credential. <paramref name="administrator"/> is what it said about the account in that
    /// session; when it was not asked, the expiry date read earlier is kept.
    /// </summary>
    public AdminCredentialStatus RecordWorking(Guid vmId, GuestAdministrator? administrator, DateTimeOffset now)
    {
        lock (_gate)
        {
            var expires = administrator is null ? Statuses().GetValueOrDefault(vmId)?.PasswordExpiresAt : administrator.PasswordExpiresAt;
            var status = new AdminCredentialStatus(vmId, VmAdminCredentialState.Working, now, expires);
            Save(status);
            return status;
        }
    }

    /// <summary>The guest refused the credential. The expiry date read earlier is kept: it usually says why.</summary>
    public AdminCredentialStatus RecordRejected(Guid vmId, string detail, DateTimeOffset now)
    {
        lock (_gate)
        {
            var status = new AdminCredentialStatus(vmId, VmAdminCredentialState.Rejected, now, Statuses().GetValueOrDefault(vmId)?.PasswordExpiresAt, detail);
            Save(status);
            return status;
        }
    }

    /// <summary>Forgets a deleted VM.</summary>
    public bool Remove(Guid vmId)
    {
        lock (_gate)
        {
            var statuses = Statuses();
            if (!statuses.Remove(vmId))
            {
                return false;
            }

            Write(statuses);
            return true;
        }
    }

    private Dictionary<Guid, AdminCredentialStatus> Statuses()
    {
        _statuses ??= File.Exists(_path)
            ? JsonSerializer.Deserialize<List<AdminCredentialStatus>>(File.ReadAllText(_path), JsonOptions)?.ToDictionary(status => status.VmId)
                ?? throw new InvalidDataException($"The administrator credential status file '{_path}' is invalid.")
            : [];
        return _statuses;
    }

    private void Write(Dictionary<Guid, AdminCredentialStatus> statuses) =>
        ProtectedFile.WriteAllBytes(_path, JsonSerializer.SerializeToUtf8Bytes(statuses.Values.ToList(), JsonOptions));
}
