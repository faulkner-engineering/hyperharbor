using System.Text.Json;
using HyperHarbor.Host.Core.Security;
using HyperHarbor.Shared.Contracts;
using HyperHarbor.Shared.Contracts.Unattend;

namespace HyperHarbor.Host.Core.Unattend;

/// <summary>
/// An unattended install in progress or finished. Kept on disk, so a host restart does not lose track of
/// a VM that is still installing. Holds no secrets: the one-time administrator password is in
/// <see cref="Provisioning.VmCredentialStore"/>.
/// </summary>
/// <param name="UserId">The User whose account is set up when the guest is ready.</param>
/// <param name="SeedPath">The answer file ISO, deleted when the install ends.</param>
/// <param name="Attempts">Failed attempts to set up the User's account; the watcher gives up after a few.</param>
/// <param name="NextAttemptAt">When the watcher may try again after a failed attempt.</param>
/// <param name="RunningSeconds">How long the VM has run during the install; time while it is off does not count.</param>
public sealed record UnattendedInstall(
    Guid VmId,
    Guid UserId,
    string ProfileId,
    InstallOs Os,
    bool InstallDesktop,
    string SeedPath,
    UnattendedInstallState State,
    string Step,
    DateTimeOffset StartedAt,
    DateTimeOffset UpdatedAt,
    string? Error = null,
    int Attempts = 0,
    DateTimeOffset? NextAttemptAt = null,
    long RunningSeconds = 0)
{
    public bool IsActive => State is not (UnattendedInstallState.Ready or UnattendedInstallState.Failed or UnattendedInstallState.Canceled);
}

/// <summary>Unattended install records by VM, in unattended-installs.json with the ProtectedFile ACL.</summary>
public sealed class UnattendedInstallStore
{
    public const string FileName = "unattended-installs.json";
    private static readonly JsonSerializerOptions JsonOptions = new(ContractJson.Options) { WriteIndented = true };

    private readonly string _path;
    private readonly object _gate = new();
    private Dictionary<Guid, UnattendedInstall>? _installs;

    public UnattendedInstallStore(string dataDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(dataDirectory);
        _path = Path.Combine(dataDirectory, FileName);
    }

    public UnattendedInstall? Find(Guid vmId)
    {
        lock (_gate)
        {
            return Installs().GetValueOrDefault(vmId);
        }
    }

    public IReadOnlyList<UnattendedInstall> List()
    {
        lock (_gate)
        {
            return Installs().Values.ToList();
        }
    }

    public void Save(UnattendedInstall install)
    {
        lock (_gate)
        {
            Installs()[install.VmId] = install;
            Write();
        }
    }

    public bool Remove(Guid vmId)
    {
        lock (_gate)
        {
            if (!Installs().Remove(vmId))
            {
                return false;
            }

            Write();
            return true;
        }
    }

    private Dictionary<Guid, UnattendedInstall> Installs()
    {
        if (_installs is null)
        {
            _installs = File.Exists(_path)
                ? JsonSerializer.Deserialize<List<UnattendedInstall>>(File.ReadAllText(_path), JsonOptions)?.ToDictionary(install => install.VmId)
                    ?? throw new InvalidDataException($"The install file '{_path}' is invalid.")
                : [];
        }

        return _installs;
    }

    private void Write() => ProtectedFile.WriteAllBytes(_path, JsonSerializer.SerializeToUtf8Bytes(Installs().Values.ToList(), JsonOptions));
}
