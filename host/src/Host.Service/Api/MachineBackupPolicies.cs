using HyperHarbor.Host.Core.Backup;
using HyperHarbor.Host.Core.Profiles;
using HyperHarbor.Shared.Contracts.Profiles;
using Wire = HyperHarbor.Shared.Contracts.Backup;

namespace HyperHarbor.Host.Service.Api;

/// <summary>Backup policies for VMs made from machine profiles, through the same mapping as PUT /backup/policies.</summary>
public sealed class MachineBackupPolicies(BackupManagementService backups) : IMachineBackupPolicies
{
    public string? FindTarget(string name) =>
        backups.Load().Targets.FirstOrDefault(target => string.Equals(target.Name, name, StringComparison.OrdinalIgnoreCase))?.Id;

    public void Set(Guid vmId, string targetId, MachineBackup backup)
    {
        var policy = new Wire.BackupPolicy(
            vmId,
            targetId,
            backup.Enabled,
            backup.Retention,
            null,
            backup.Conditions,
            backup.Triggers,
            backup.GpuMode ?? Wire.BackupGpuMode.RequireOff);
        backups.SetPolicy(policy.ToCore(backups.Find(targetId)));
    }
}
