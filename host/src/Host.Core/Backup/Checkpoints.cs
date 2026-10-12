using System.Globalization;
using HyperHarbor.Host.Core.HyperV;
using HyperHarbor.Host.Core.Power;
using Microsoft.Management.Infrastructure;

namespace HyperHarbor.Host.Core.Backup;

public enum CheckpointKind
{
    /// <summary>Application consistent (VSS in the guest); a copy backup, so the guest's log chains are not truncated.</summary>
    Production,

    /// <summary>Crash consistent; used when the guest cannot take a production checkpoint.</summary>
    Standard,
}

public sealed record CheckpointInfo(string Id, string Name, DateTimeOffset? Created)
{
    /// <summary>Backup checkpoints are named with this prefix, so a stale one from a failed run can be found and removed.</summary>
    public const string BackupPrefix = "HyperHarbor backup ";

    public bool IsHyperHarborBackup => Name.StartsWith(BackupPrefix, StringComparison.Ordinal);
}

public sealed class CheckpointException(string message, Exception? inner = null) : Exception(message, inner);

/// <summary>Hyper-V checkpoints for the backup job: create one, list them, remove one (merging it away).</summary>
public interface IHyperVCheckpoints
{
    Task<IReadOnlyList<CheckpointInfo>> ListAsync(Guid vmId, CancellationToken cancellationToken);

    /// <exception cref="CheckpointException">The checkpoint could not be taken (for example no integration services for a production one).</exception>
    Task<CheckpointInfo> CreateAsync(Guid vmId, string name, CheckpointKind kind, CancellationToken cancellationToken);

    /// <summary>Removes the checkpoint; Hyper-V merges its differencing disk into the parent.</summary>
    Task RemoveAsync(Guid vmId, string checkpointId, CancellationToken cancellationToken);
}

/// <summary><see cref="IHyperVCheckpoints"/> through Msvm_VirtualSystemSnapshotService.</summary>
public sealed class CimHyperVCheckpoints : IHyperVCheckpoints
{
    private const string SnapshotTypePrefix = "Microsoft:Hyper-V:Snapshot:";
    private const ushort FullSnapshot = 2;

    // Msvm_VirtualSystemSnapshotSettingData.ConsistencyLevel and GuestBackupType.
    private const ushort ApplicationConsistent = 2;
    private const ushort CrashConsistent = 3;
    private const ushort CopyBackup = 3;

    public Task<IReadOnlyList<CheckpointInfo>> ListAsync(Guid vmId, CancellationToken cancellationToken) =>
        HyperVCim.RunAsync<IReadOnlyList<CheckpointInfo>>(session =>
        {
            var found = Snapshots(session, vmId);
            try
            {
                return Task.FromResult<IReadOnlyList<CheckpointInfo>>(found.Select(Info).ToList());
            }
            finally
            {
                Dispose(found);
            }
        }, cancellationToken);

    public Task<CheckpointInfo> CreateAsync(Guid vmId, string name, CheckpointKind kind, CancellationToken cancellationToken) =>
        HyperVCim.RunAsync(async session =>
        {
            using var service = HyperVCim.QuerySingle(session, "SELECT * FROM Msvm_VirtualSystemSnapshotService")
                ?? throw new HyperVUnavailableException("The Hyper-V checkpoint service was not found.");
            using var system = HyperVCim.QuerySingle(session, $"SELECT * FROM Msvm_ComputerSystem WHERE Name = '{vmId:D}'")
                ?? throw new VmNotFoundException(vmId);
            var before = ExistingIds(session, vmId);
            var settings = CimXml.Write("Msvm_VirtualSystemSnapshotSettingData",
            [
                new CimXml.Property("ConsistencyLevel", CimType.UInt16, kind == CheckpointKind.Production ? ApplicationConsistent : CrashConsistent),
                new CimXml.Property("GuestBackupType", CimType.UInt16, CopyBackup),
                new CimXml.Property("IgnoreNonSnapshottableDisks", CimType.Boolean, false),
            ]);
            var parameters = new CimMethodParametersCollection
            {
                CimMethodParameter.Create("AffectedSystem", system, CimType.Reference, CimFlags.In),
                CimMethodParameter.Create("SnapshotSettings", settings, CimType.String, CimFlags.In),
                CimMethodParameter.Create("SnapshotType", FullSnapshot, CimType.UInt16, CimFlags.In),
            };

            CimMethodResult result;
            try
            {
                result = await HyperVCim.InvokeAsync(session, service, "CreateSnapshot", parameters, "CreateSnapshot", cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is HyperVOperationException or HyperVJobFailedException)
            {
                throw new CheckpointException($"Hyper-V could not take a {kind.ToString().ToLowerInvariant()} checkpoint: {ex.Message}", ex);
            }

            result.Dispose();

            // CreateSnapshot runs as a job, which does not return the new checkpoint: it is the one that was not there before.
            var after = Snapshots(session, vmId);
            try
            {
                var created = after.FirstOrDefault(item => !before.Contains((string)item.CimInstanceProperties["InstanceID"].Value))
                    ?? throw new CheckpointException("Hyper-V did not report the checkpoint it created.");
                await CimVmSettings.ModifySystemAsync(session, created, cancellationToken, new CimXml.Property("ElementName", CimType.String, name)).ConfigureAwait(false);
                var id = (string)created.CimInstanceProperties["InstanceID"].Value;
                var renamed = Snapshots(session, vmId);
                try
                {
                    return Info(renamed.First(item => (string)item.CimInstanceProperties["InstanceID"].Value == id));
                }
                finally
                {
                    Dispose(renamed);
                }
            }
            finally
            {
                Dispose(after);
            }
        }, cancellationToken);

    public Task RemoveAsync(Guid vmId, string checkpointId, CancellationToken cancellationToken) =>
        HyperVCim.RunAsync(async session =>
        {
            using var service = HyperVCim.QuerySingle(session, "SELECT * FROM Msvm_VirtualSystemSnapshotService")
                ?? throw new HyperVUnavailableException("The Hyper-V checkpoint service was not found.");
            var found = Snapshots(session, vmId);
            try
            {
                var snapshot = found.FirstOrDefault(item => (string)item.CimInstanceProperties["InstanceID"].Value == checkpointId);
                if (snapshot is null)
                {
                    return;
                }

                var parameters = new CimMethodParametersCollection
                {
                    CimMethodParameter.Create("AffectedSnapshot", snapshot, CimType.Reference, CimFlags.In),
                };
                await HyperVCim.InvokeAsync(session, service, "DestroySnapshot", parameters, "DestroySnapshot", cancellationToken).ConfigureAwait(false);
            }
            finally
            {
                Dispose(found);
            }
        }, cancellationToken);

    private static HashSet<string> ExistingIds(CimSession session, Guid vmId)
    {
        var found = Snapshots(session, vmId);
        try
        {
            return found.Select(item => (string)item.CimInstanceProperties["InstanceID"].Value).ToHashSet(StringComparer.Ordinal);
        }
        finally
        {
            Dispose(found);
        }
    }

    private static List<CimInstance> Snapshots(CimSession session, Guid vmId) =>
        HyperVCim.Query(session, $"SELECT * FROM Msvm_VirtualSystemSettingData WHERE VirtualSystemIdentifier = '{vmId:D}'")
            .Where(item => (item.CimInstanceProperties["VirtualSystemType"]?.Value as string)?.StartsWith(SnapshotTypePrefix, StringComparison.Ordinal) == true)
            .ToList();

    private static CheckpointInfo Info(CimInstance snapshot) =>
        new((string)snapshot.CimInstanceProperties["InstanceID"].Value,
            (string)snapshot.CimInstanceProperties["ElementName"].Value,
            snapshot.CimInstanceProperties["CreationTime"]?.Value is DateTime created ? new DateTimeOffset(created.ToUniversalTime()) : null);

    private static void Dispose(IEnumerable<CimInstance> instances)
    {
        foreach (var instance in instances)
        {
            instance.Dispose();
        }
    }
}
