using System.Text.Json.Serialization;
using HyperHarbor.Shared.Contracts.Vms;

namespace HyperHarbor.Shared.Contracts.Transfers;

/// <summary>A network adapter of a VM to transfer. Schema: TransferAdapter.</summary>
/// <param name="SwitchName">Matched by name on the receiving host; the Default Switch or no connection otherwise.</param>
/// <param name="MacAddress">Twelve hexadecimal digits; a move keeps it as a static address.</param>
public sealed record TransferAdapter(
    [property: JsonIgnore(Condition = JsonIgnoreCondition.Never)] string? SwitchId,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.Never)] string? SwitchName,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.Never)] string? MacAddress);

/// <summary>A virtual hard disk the source host sends. Schema: TransferDisk.</summary>
/// <param name="Path">The disk's path on the source host; the receiving host uses only its file name.</param>
/// <param name="ControllerType">"scsi" or "ide".</param>
public sealed record TransferDisk(
    [property: JsonRequired] string Path,
    [property: JsonRequired] long SizeBytes,
    [property: JsonRequired] string ControllerType,
    [property: JsonRequired] int ControllerNumber,
    [property: JsonRequired] int Lun);

/// <summary>A VM's processors and memory, as the receiving host checks them. Schema: TransferCompute.</summary>
public sealed record TransferCompute(
    [property: JsonRequired] int ProcessorCount,
    [property: JsonRequired] long StartupMemoryMb,
    [property: JsonRequired] long MaximumMemoryMb,
    [property: JsonRequired] bool DynamicMemory,
    [property: JsonRequired] bool NestedVirtualization,
    [property: JsonRequired] bool MacAddressSpoofing);

/// <summary>Schema: TransferBlocker.</summary>
public sealed record TransferBlocker([property: JsonRequired] TransferBlockerCode Code, [property: JsonRequired] string Message);

/// <summary>
/// What the source host reports about a VM before a transfer. The client shows it and passes it to the receiving host
/// unchanged. Schema: TransferPreview.
/// </summary>
/// <param name="ConfigurationVersion">Hyper-V's configuration version, such as "12.0"; null when it could not be read.</param>
/// <param name="TotalBytes">The disks' total size: what crosses the network.</param>
/// <param name="Blockers">Must be empty for the transfer to start.</param>
/// <param name="Warnings">What changes for the VM on the way, in sentences for the dialog.</param>
public sealed record TransferPreview(
    [property: JsonRequired] Guid VmId,
    [property: JsonRequired] string Name,
    [property: JsonRequired] VmState State,
    [property: JsonRequired] int Generation,
    [property: JsonRequired, JsonIgnore(Condition = JsonIgnoreCondition.Never)] string? ConfigurationVersion,
    [property: JsonRequired] TransferCompute Compute,
    [property: JsonRequired] bool SecureBoot,
    [property: JsonRequired, JsonIgnore(Condition = JsonIgnoreCondition.Never)] string? SecureBootTemplateId,
    [property: JsonRequired] bool TpmEnabled,
    [property: JsonRequired] bool HasGpuPartition,
    [property: JsonRequired] IReadOnlyList<TransferAdapter> Adapters,
    [property: JsonRequired] IReadOnlyList<TransferDisk> Disks,
    [property: JsonRequired] long TotalBytes,
    [property: JsonRequired] int CheckpointCount,
    [property: JsonRequired] IReadOnlyList<TransferBlocker> Blockers,
    [property: JsonRequired] IReadOnlyList<string> Warnings);

/// <summary>Asks the receiving host to take a VM. Schema: IncomingTransferRequest.</summary>
/// <param name="Preview">The source host's preview, as it answered.</param>
/// <param name="SourceFingerprint">The SHA-256 fingerprint of the source host's certificate, as the client pinned it.</param>
/// <param name="AcknowledgeWarnings">Set after the user has seen the warnings of a first attempt.</param>
public sealed record IncomingTransferRequest(
    [property: JsonRequired] TransferPreview Preview,
    [property: JsonRequired] TransferMode Mode,
    [property: JsonRequired] string SourceFingerprint,
    bool AcknowledgeWarnings = false);

/// <summary>The receiving host's acceptance. ToString never includes the ticket. Schema: TransferOffer.</summary>
/// <param name="Ticket">One-time ticket for the source host's data requests. Pass it to the source host and nowhere else.</param>
/// <param name="VmId">The ID the VM keeps (a move); null for a copy, which gets a new one.</param>
/// <param name="VmFolder">Where the VM goes on the receiving host.</param>
/// <param name="Guardian">The receiving host's key protector guardian (public certificates), when the VM has a TPM.</param>
public sealed record TransferOffer(
    Guid TransferId,
    string Ticket,
    string Name,
    string VmFolder,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.Never)] Guid? VmId,
    IReadOnlyList<ValidationIssue> Warnings,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.Never)] byte[]? Guardian)
{
    public override string ToString() => $"TransferOffer {{ TransferId = {TransferId}, Name = {Name} }}";
}

/// <summary>The receiving host as the client knows it. Schema: TransferTarget.</summary>
/// <param name="Addresses">Host names or IP addresses, tried in order.</param>
/// <param name="Fingerprint">The SHA-256 fingerprint of the receiving host's certificate, as the client pinned it.</param>
public sealed record TransferTarget(
    [property: JsonRequired] Guid HostId,
    [property: JsonRequired] string Name,
    [property: JsonRequired] IReadOnlyList<string> Addresses,
    [property: JsonRequired] int Port,
    [property: JsonRequired] string Fingerprint);

/// <summary>Starts sending a VM to the receiving host that accepted it. ToString never includes the ticket. Schema: StartTransferRequest.</summary>
/// <param name="TransferId">From the receiving host's offer.</param>
/// <param name="Ticket">From the receiving host's offer.</param>
/// <param name="Guardian">From the receiving host's offer; required when the VM has a TPM.</param>
/// <param name="ConfirmName">For a move, which deletes the VM here once the receiving host has it: the VM's name exactly.</param>
public sealed record StartTransferRequest(
    [property: JsonRequired] TransferMode Mode,
    [property: JsonRequired] TransferTarget Target,
    [property: JsonRequired] Guid TransferId,
    [property: JsonRequired] string Ticket,
    byte[]? Guardian = null,
    string? ConfirmName = null)
{
    public override string ToString() => $"StartTransferRequest {{ Mode = {Mode}, TransferId = {TransferId}, Target = {Target.Name} }}";
}

/// <summary>What a transferVm job did. Schema: TransferResult.</summary>
/// <param name="SourceRemoved">For a move: whether the VM was deleted from the source host. Always false for a copy.</param>
/// <param name="Warnings">Things the user must know or do, such as deleting a copy that could not be removed.</param>
public sealed record TransferResult(Guid TargetVmId, string TargetName, bool SourceRemoved, IReadOnlyList<string> Warnings);

/// <summary>One file of an incoming transfer. Schema: IncomingTransferFile.</summary>
/// <param name="Key">"disk:&lt;index&gt;" or "definition:&lt;file name&gt;".</param>
/// <param name="Received">Bytes received and verified.</param>
public sealed record IncomingTransferFile(string Key, long Size, long Received);

/// <summary>Schema: IncomingTransferStatus.</summary>
public sealed record IncomingTransferStatus(Guid TransferId, IncomingTransferState State, IReadOnlyList<IncomingTransferFile> Files);

/// <summary>The VM the receiving host imported. Schema: TransferImported.</summary>
/// <param name="Warnings">What the user must know, such as an account whose User the receiving host does not have.</param>
public sealed record TransferImported(Guid VmId, string Name, IReadOnlyList<string> Warnings);

/// <summary>A User's account in the guest, matched on the receiving host by account name. Schema: TransferAccount.</summary>
public sealed record TransferAccount(
    [property: JsonRequired] string AccountName,
    [property: JsonRequired] GuestOsFamily GuestOs,
    [property: JsonRequired, JsonIgnore(Condition = JsonIgnoreCondition.Never)] string? SshHostKey,
    [property: JsonRequired] DateTimeOffset ProvisionedAt,
    [property: JsonRequired] DateTimeOffset VerifiedAt);

/// <summary>HyperHarbor's administrator account in the guest. ToString never includes the password. Schema: TransferCredential.</summary>
public sealed record TransferCredential([property: JsonRequired] string UserName, [property: JsonRequired] string Password)
{
    public override string ToString() => $"TransferCredential {{ UserName = {UserName} }}";
}

/// <summary>The VM's own game-aware action. Schema: TransferGamePolicy.</summary>
public sealed record TransferGamePolicy([property: JsonRequired] Hosts.GameVmAction Action, [property: JsonRequired] bool AlwaysPause);

/// <summary>What the sending host knows about the VM beyond its files. ToString never includes the credential. Schema: TransferRecords.</summary>
public sealed record TransferRecords(
    [property: JsonRequired] IReadOnlyList<TransferAccount> Accounts,
    [property: JsonRequired, JsonIgnore(Condition = JsonIgnoreCondition.Never)] TransferCredential? AdminCredential,
    [property: JsonRequired, JsonIgnore(Condition = JsonIgnoreCondition.Never)] TransferGamePolicy? GamePolicy)
{
    public override string ToString() =>
        $"TransferRecords {{ Accounts = {Accounts.Count}, AdminCredential = {(AdminCredential is null ? "none" : "set")}, GamePolicy = {GamePolicy?.Action.ToString() ?? "none"} }}";
}

/// <summary>Schema: IncomingTransferCanceled.</summary>
/// <param name="RolledBack">True when the transfer was undone; false when the VM had already been imported and was kept.</param>
public sealed record IncomingTransferCanceled(bool RolledBack);
