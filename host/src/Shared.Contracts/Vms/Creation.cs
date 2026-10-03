using System.Text.Json.Serialization;

namespace HyperHarbor.Shared.Contracts.Vms;

/// <summary>
/// Creates a Generation 2 VM that boots from an ISO in the host's library. Schema: CreateVmRequest.
/// Secure Boot uses the Microsoft Windows template, the new disk is a dynamic VHDX, and the DVD drive is
/// first in the boot order.
/// </summary>
/// <param name="IsoName">An image name from GET /isos.</param>
/// <param name="MaximumMemoryMb">Used with dynamic memory; ignored otherwise.</param>
/// <param name="SwitchId">A switch from GET /switches. Null: the Default Switch, or no network adapter if there is none.</param>
/// <param name="EnableTpm">Add a virtual TPM (Windows 11 needs one).</param>
/// <param name="AcknowledgeWarnings">Proceed despite host resource warnings.</param>
public sealed record CreateVmRequest(
    [property: JsonRequired] string Name,
    [property: JsonRequired] string IsoName,
    [property: JsonRequired] int DiskSizeGb,
    [property: JsonRequired] int ProcessorCount,
    [property: JsonRequired] long StartupMemoryMb,
    [property: JsonRequired] long MaximumMemoryMb,
    [property: JsonRequired] bool DynamicMemory,
    string? SwitchId = null,
    bool EnableTpm = true,
    bool AcknowledgeWarnings = false);
