using HyperHarbor.Host.Core.Lifecycle;
using HyperHarbor.Host.Core.Power;
using HyperHarbor.Shared.Contracts;
using HyperHarbor.Shared.Contracts.Transfers;
using HyperHarbor.Shared.Contracts.Vms;

namespace HyperHarbor.Host.Core.Transfer;

/// <summary>Starts and cancels transferVm jobs on the sending host.</summary>
public sealed class TransferJobs
{
    public const int MaxAddresses = 16;

    private readonly IVmInventory _inventory;
    private readonly TransferSender _sender;
    private readonly VmJobStore _jobs;

    public TransferJobs(IVmInventory inventory, TransferSender sender, VmJobStore jobs)
    {
        _inventory = inventory;
        _sender = sender;
        _jobs = jobs;
    }

    /// <exception cref="VmNotFoundException">No VM has this ID.</exception>
    /// <exception cref="LifecycleValidationException">The request is incomplete, or a move's confirmation name does not match.</exception>
    /// <exception cref="VmBusyException">Another operation holds the VM.</exception>
    public async Task<VmJobSnapshot> StartAsync(
        Guid vmId,
        Guid userId,
        TransferSendRequest request,
        string? confirmName,
        Action<VmJobSnapshot>? onFinished,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var name = await _inventory.FindNameAsync(vmId, cancellationToken).ConfigureAwait(false) ?? throw new VmNotFoundException(vmId);
        var errors = Validate(request);
        if (request.Mode == TransferMode.Move && !string.Equals(confirmName, name, StringComparison.Ordinal))
        {
            errors.Add(new ValidationIssue("confirmName", $"A move deletes the VM from this host. Type \"{name}\" to confirm."));
        }

        if (errors.Count > 0)
        {
            throw new LifecycleValidationException(errors[0].Message, errors);
        }

        return _jobs.Start(VmJobKind.TransferVm, vmId, userId, "Checking the virtual machine", async context =>
        {
            var result = await _sender.RunAsync(request, context.Report, context.Stopping).ConfigureAwait(false);
            context.ReportTransferResult(new TransferResult(result.TargetVmId, result.TargetName, result.SourceRemoved, result.Warnings));
        }, onFinished);
    }

    /// <summary>Cancels the User's running transfer of the VM. Once the other host has imported it, the job finishes anyway.</summary>
    /// <returns>False when no transfer of the VM is running for this User.</returns>
    public bool Cancel(Guid vmId, Guid userId) =>
        _jobs.FindRunning(vmId, VmJobKind.TransferVm, userId) is { } job && _jobs.Cancel(job.Id, userId);

    private static List<ValidationIssue> Validate(TransferSendRequest request)
    {
        var errors = new List<ValidationIssue>();
        var target = request.Target;
        if (target.Addresses.Count is 0 or > MaxAddresses || target.Addresses.Any(address => string.IsNullOrWhiteSpace(address) || address.Length > 255))
        {
            errors.Add(new ValidationIssue("target.addresses", $"Give 1 to {MaxAddresses} addresses of the other host."));
        }

        if (target.Port is < 1 or > 65535)
        {
            errors.Add(new ValidationIssue("target.port", "Use the other host's port, 1 to 65535."));
        }

        if (TransferReceiver.NormalizeFingerprint(target.Fingerprint) is null)
        {
            errors.Add(new ValidationIssue("target.fingerprint", "Use the SHA-256 fingerprint of the other host's certificate."));
        }

        if (string.IsNullOrEmpty(request.Ticket) || request.Ticket.Length > 128)
        {
            errors.Add(new ValidationIssue("ticket", "Use the ticket from the other host's offer."));
        }

        if (request.TargetGuardian is { Length: > KeyProtectorHandover.MaxGuardianExportBytes })
        {
            errors.Add(new ValidationIssue("guardian", "The other host's guardian is too large."));
        }

        return errors;
    }
}
