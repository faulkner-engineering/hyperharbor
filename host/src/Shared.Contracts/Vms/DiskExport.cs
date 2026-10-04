namespace HyperHarbor.Shared.Contracts.Vms;

/// <summary>Copies an off VM's virtual hard disks. Schema: ExportDisksRequest.</summary>
/// <param name="DestinationFolder">A fully qualified folder on the host; null uses the backup folder set in the tray.</param>
public sealed record ExportDisksRequest(string? DestinationFolder = null);
