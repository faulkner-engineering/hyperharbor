using HyperHarbor.Host.Core.Backup.Kopia;

namespace HyperHarbor.Host.Service.Backup;

/// <summary>
/// Extracts and verifies the bundled kopia.exe on first use rather than at startup, so a host without backups configured
/// never writes it, and a damaged copy fails the backup (with a clear message) instead of the host.
/// </summary>
internal sealed class LazyKopiaRunner(KopiaToolLocator locator) : IKopiaRunner
{
    private readonly object _gate = new();
    private IKopiaRunner? _inner;

    public Task<KopiaResult> RunAsync(KopiaInvocation invocation, CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            _inner ??= new KopiaProcessRunner(locator.Ensure());
        }

        return _inner.RunAsync(invocation, cancellationToken);
    }
}

/// <summary>Creates the default User's account in a restored guest with the administrator credential given for the restore.</summary>
internal sealed class RestoreProvisioner(
    HyperHarbor.Host.Core.Provisioning.ProvisioningService provisioning,
    HyperHarbor.Host.Core.Users.UserStore users,
    HyperHarbor.Host.Core.Unattend.IRemoteAccessProbe probe) : HyperHarbor.Host.Core.Backup.Restore.IRestoreProvisioner
{
    public async Task ProvisionAsync(Guid vmId, HyperHarbor.Shared.Contracts.Vms.ProvisionVmRequest administrator, CancellationToken cancellationToken) =>
        await provisioning.ProvisionAsync(vmId, users.GetOrCreateDefault().UserId, administrator, cancellationToken, probe).ConfigureAwait(false);
}
