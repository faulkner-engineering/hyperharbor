using HyperHarbor.Host.Core.Backup;
using HyperHarbor.Host.Core.Lifecycle;
using Domain = HyperHarbor.Host.Core.Transfer;
using Wire = HyperHarbor.Shared.Contracts.Transfers;

namespace HyperHarbor.Host.Service.Api;

/// <summary>Converts the transfer records of Host.Core to the transfer schemas of docs/api.yaml and back.</summary>
public static class TransferMapping
{
    public static Wire.TransferPreview ToContract(this Domain.TransferPreview preview) => new(
        preview.VmId,
        preview.Name,
        preview.State,
        preview.Generation,
        preview.ConfigurationVersion,
        new Wire.TransferCompute(
            preview.Compute.ProcessorCount,
            preview.Compute.StartupMemoryMb,
            preview.Compute.MaximumMemoryMb,
            preview.Compute.DynamicMemory,
            preview.Compute.NestedVirtualization,
            preview.Compute.MacAddressSpoofing),
        preview.SecureBoot,
        preview.SecureBootTemplateId,
        preview.TpmEnabled,
        preview.HasGpuPartition,
        preview.Adapters.Select(adapter => new Wire.TransferAdapter(adapter.SwitchId, adapter.SwitchName, adapter.MacAddress)).ToList(),
        preview.Disks.Select(disk => new Wire.TransferDisk(disk.Path, disk.SizeBytes, disk.ControllerType, disk.ControllerNumber, disk.Lun)).ToList(),
        preview.TotalBytes,
        preview.CheckpointCount,
        preview.Blockers.Select(blocker => new Wire.TransferBlocker(blocker.Code, blocker.Message)).ToList(),
        preview.Warnings);

    /// <summary>The preview as the source host sent it; the receiving host checks it again in its preflight.</summary>
    public static Domain.TransferPreview ToCore(this Wire.TransferPreview preview) => new(
        preview.VmId,
        preview.Name,
        preview.State,
        preview.Generation,
        preview.ConfigurationVersion,
        new ComputeState(
            preview.Compute.ProcessorCount,
            preview.Compute.StartupMemoryMb,
            preview.Compute.MaximumMemoryMb,
            preview.Compute.DynamicMemory,
            preview.Compute.NestedVirtualization,
            preview.Compute.MacAddressSpoofing,
            preview.Adapters.Count),
        preview.SecureBoot,
        preview.SecureBootTemplateId,
        preview.TpmEnabled,
        preview.HasGpuPartition,
        preview.Adapters.Select(adapter => new VmAdapterDefinition(adapter.SwitchId, adapter.SwitchName, adapter.MacAddress)).ToList(),
        preview.Disks.Select(disk => new Domain.TransferDisk(disk.Path, disk.SizeBytes, disk.ControllerType, disk.ControllerNumber, disk.Lun)).ToList(),
        preview.CheckpointCount,
        preview.Blockers.Select(blocker => new Domain.TransferBlocker(blocker.Code, blocker.Message)).ToList(),
        preview.Warnings);

    public static Domain.IncomingTransferRequest ToCore(this Wire.IncomingTransferRequest request) =>
        new(request.Preview.ToCore(), request.Mode, request.SourceFingerprint, request.AcknowledgeWarnings);

    public static Wire.TransferOffer ToContract(this Domain.IncomingTransferOffer offer) =>
        new(offer.TransferId, offer.Ticket, offer.Plan.Name, offer.Plan.VmFolder, offer.Plan.VmId, offer.Plan.Warnings, offer.Guardian);

    public static Domain.TransferSendRequest ToCore(this Wire.StartTransferRequest request, Guid vmId) => new(
        vmId,
        request.Mode,
        request.TransferId,
        request.Ticket,
        new Domain.PeerEndpoint(request.Target.HostId, request.Target.Name, request.Target.Addresses, request.Target.Port, request.Target.Fingerprint),
        request.Guardian);

    public static Wire.IncomingTransferStatus ToContract(this Domain.IncomingTransferStatus status) =>
        new(status.TransferId, status.State, status.Files.Select(ToContract).ToList());

    public static Wire.IncomingTransferFile ToContract(this Domain.IncomingFile file) => new(file.Key, file.Size, file.Received);

    public static Wire.TransferImported ToContract(this Domain.TransferImportResult result) => new(result.VmId, result.Name, result.Warnings);
}
