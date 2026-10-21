using System.Globalization;
using System.Text.RegularExpressions;
using HyperHarbor.Host.Core.Backup;
using HyperHarbor.Host.Core.Backup.Restore;
using HyperHarbor.Host.Core.Lifecycle;
using HyperHarbor.Shared.Contracts;
using HyperHarbor.Shared.Contracts.Hosts;
using HyperHarbor.Shared.Contracts.Vms;

namespace HyperHarbor.Host.Core.Transfer;

/// <summary>What the receiving host knows about itself when it checks a proposed transfer.</summary>
/// <param name="ExistingVms">Every VM on this host, by ID and name.</param>
/// <param name="DestinationRoot">The folder the VM's own folder is created in.</param>
/// <param name="ExistingFolders">Names of the folders already in <paramref name="DestinationRoot"/>.</param>
/// <param name="AvailableSpaceMb">Free space on the destination's drive; null when unknown.</param>
public sealed record TransferTargetFacts(
    IReadOnlyList<(Guid Id, string Name)> ExistingVms,
    string DestinationRoot,
    IReadOnlyList<string> ExistingFolders,
    long? AvailableSpaceMb,
    HostCapacity Capacity,
    long HostMemoryReserveMb,
    HostVersionCapabilities Versions,
    IReadOnlyList<VirtualSwitch> Switches,
    string? LocalGuardianId = null,
    TpmCapability? Tpm = null);

/// <summary>
/// Decides whether this host can receive a VM, and where and how it goes. Pure: the caller gathers the facts.
/// Nothing in the preview is trusted as a path on this host: only the source's file names are used, and they
/// are checked.
/// </summary>
public static partial class TransferPreflight
{
    public const string DiskFolderName = "Virtual Hard Disks";

    /// <summary>Configuration files, the guest state file, and room to spare, beyond the disks themselves.</summary>
    public const long SpaceMarginMb = 1024;

    public const string CopySuffix = "copy";

    public const string MoveSuffix = "moved";

    /// <summary>
    /// Whether deciding about the VM's TPM needs <see cref="ITpmCapabilityProbe"/>: always for a fresh TPM, and for keeping
    /// one unless this host owns its key (a VM going back to the host that created it).
    /// </summary>
    public static bool NeedsTpmProbe(TransferPreview preview, TransferTpmMode tpmMode, string? localGuardianId) =>
        preview.TpmEnabled
        && (tpmMode == TransferTpmMode.Fresh
            || localGuardianId is null
            || preview.TpmKeyHolders is not { } holders
            || !string.Equals(holders.Owner, localGuardianId, StringComparison.OrdinalIgnoreCase));

    public static TransferPlan Evaluate(TransferPreview preview, TransferMode mode, TransferTargetFacts facts, TransferTpmMode tpmMode = TransferTpmMode.Keep)
    {
        ArgumentNullException.ThrowIfNull(preview);
        ArgumentNullException.ThrowIfNull(facts);
        var errors = new List<ValidationIssue>();
        var warnings = new List<ValidationIssue>();
        var (effectiveTpm, grantNeeded) = DecideTpm(preview, tpmMode, facts, errors, warnings);

        if (preview.Blockers.Count > 0)
        {
            errors.Add(new("vm", preview.Blockers[0].Message));
        }

        if (mode == TransferMode.Move && facts.ExistingVms.Any(vm => vm.Id == preview.VmId))
        {
            errors.Add(new("vmId", $"This host already has a VM with the ID of {preview.Name}. Copy it instead, or delete the VM here first."));
        }

        CheckVersion(preview, facts.Versions, errors);
        CheckCompute(preview, facts, errors, warnings);

        var desired = preview.Name.Trim();
        VmCreationService.CheckName(desired, errors);
        var name = desired;
        if (errors.All(error => error.Field != "name"))
        {
            var taken = facts.ExistingVms.Where(vm => !(mode == TransferMode.Move && vm.Id == preview.VmId)).Select(vm => vm.Name).Concat(facts.ExistingFolders);
            name = RestoreNaming.UniqueName(desired, taken, mode == TransferMode.Copy ? CopySuffix : MoveSuffix);
            if (!string.Equals(name, desired, StringComparison.Ordinal))
            {
                warnings.Add(new("name", $"A VM or folder named \"{desired}\" already exists on this host, so this one is named \"{name}\"."));
            }
        }

        var vmFolder = Path.Combine(facts.DestinationRoot, name);
        var disks = PlanDisks(preview.Disks, Path.Combine(vmFolder, DiskFolderName), errors);

        var neededMb = CeilingMb(preview.TotalBytes) + SpaceMarginMb;
        if (facts.AvailableSpaceMb is { } freeMb && freeMb < neededMb)
        {
            errors.Add(new("space", $"The VM needs about {VmSettingsValidator.Mb(neededMb)} on this host, but {facts.DestinationRoot} has {VmSettingsValidator.Mb(freeMb)} free."));
        }

        var network = SwitchMapper.Map(
            preview.Adapters.Select(adapter => new ManifestAdapter(adapter.SwitchName, adapter.SwitchId, adapter.MacAddress)).ToList(),
            facts.Switches);
        warnings.AddRange(network.Warnings.Select(warning => new ValidationIssue("network", warning)));
        var adapters = new List<TransferAdapterPlan>();
        for (var i = 0; i < preview.Adapters.Count; i++)
        {
            var mac = mode == TransferMode.Move ? NormalizeMac(preview.Adapters[i].MacAddress) : null;
            if (mode == TransferMode.Move && mac is null)
            {
                warnings.Add(new("network", $"Network adapter {i + 1} has no MAC address to keep; it gets a new one on this host."));
            }

            adapters.Add(new TransferAdapterPlan(network.Adapters[i].SwitchId, mac));
        }

        if (mode == TransferMode.Copy)
        {
            warnings.Add(new("identity",
                "The copy gets a new VM ID and new MAC addresses, but the guest keeps its computer name. Rename it before running both copies on one network."));
        }

        warnings.AddRange(preview.Warnings.Select(warning => new ValidationIssue("vm", warning)));

        // A rebuilt VM is a new VM, so even a move gets a new ID; its MAC addresses are kept all the same.
        var keepsId = mode == TransferMode.Move && effectiveTpm == TransferTpmMode.Keep;
        return new TransferPlan(mode, keepsId ? preview.VmId : null, name, vmFolder, disks, adapters, errors, warnings, effectiveTpm, grantNeeded);
    }

    /// <summary>
    /// Keep: this host owns the key (going home), already holds it, or is granted it by the source, which must own it; the
    /// last two need this host to open granted keys. Fresh: this host must be able to turn on a TPM. A VM without a TPM
    /// keeps nothing and needs nothing.
    /// </summary>
    private static (TransferTpmMode Mode, bool GrantNeeded) DecideTpm(
        TransferPreview preview, TransferTpmMode tpmMode, TransferTargetFacts facts, List<ValidationIssue> errors, List<ValidationIssue> warnings)
    {
        if (!preview.TpmEnabled)
        {
            return (TransferTpmMode.Keep, false);
        }

        var detail = facts.Tpm?.Detail is { Length: > 0 } said ? $" Windows says: {said}" : string.Empty;
        if (tpmMode == TransferTpmMode.Fresh)
        {
            if (facts.Tpm?.CanEnableTpm != true)
            {
                errors.Add(new("tpm", $"This host cannot give the VM a new TPM: Hyper-V here refuses to turn one on.{detail}"));
            }

            warnings.Add(new("tpm",
                $"{preview.Name} gets a new, empty TPM here and a new VM ID. BitLocker asks for its recovery key at the first start, and anything else kept in the TPM (Windows Hello PINs, for example) must be set up again."));
            return (TransferTpmMode.Fresh, false);
        }

        var local = facts.LocalGuardianId;
        var holders = preview.TpmKeyHolders;
        if (local is not null && holders is not null && string.Equals(holders.Owner, local, StringComparison.OrdinalIgnoreCase))
        {
            return (TransferTpmMode.Keep, false);
        }

        if (facts.Tpm?.CanOpenGrantedKeys != true)
        {
            errors.Add(new("tpm", $"This host cannot open a TPM key that another host gives it. Give {preview.Name} a fresh TPM instead.{detail}"));
            return (TransferTpmMode.Keep, false);
        }

        if (local is not null && holders is not null && holders.Holds(local))
        {
            return (TransferTpmMode.Keep, false);
        }

        if (holders is not null && !preview.TpmOwnedBySource)
        {
            errors.Add(new("tpm",
                $"Only the host that created {preview.Name} can give its TPM key to another host. Send it from that host, or give it a fresh TPM."));
            return (TransferTpmMode.Keep, false);
        }

        return (TransferTpmMode.Keep, true);
    }

    /// <summary>Twelve upper-case hexadecimal digits, or null for anything that is not a usable unicast address.</summary>
    internal static string? NormalizeMac(string? address)
    {
        if (address is null)
        {
            return null;
        }

        var digits = address.Replace(":", string.Empty, StringComparison.Ordinal).Replace("-", string.Empty, StringComparison.Ordinal).ToUpperInvariant();
        if (!MacDigits().IsMatch(digits) || digits == "000000000000")
        {
            return null;
        }

        // The low bit of the first octet marks a multicast address, which a network adapter cannot have.
        return (Convert.ToByte(digits[..2], 16) & 1) == 0 ? digits : null;
    }

    private static void CheckVersion(TransferPreview preview, HostVersionCapabilities versions, List<ValidationIssue> errors)
    {
        if (preview.ConfigurationVersion is not { Length: > 0 } version)
        {
            errors.Add(new("configurationVersion", $"The source host did not report the configuration version of {preview.Name}."));
        }
        else if (!versions.Supports(version))
        {
            errors.Add(new("configurationVersion", versions.Newest is { } newest
                ? $"{preview.Name} has configuration version {version}, which this host's Hyper-V cannot run (it supports up to {newest}). Update Windows on this host."
                : $"{preview.Name} has configuration version {version}, which this host's Hyper-V does not list as supported."));
        }
    }

    private static void CheckCompute(TransferPreview preview, TransferTargetFacts facts, List<ValidationIssue> errors, List<ValidationIssue> warnings)
    {
        var compute = preview.Compute;
        VmSettingsValidator.Check(
            new ComputeRequest(compute.ProcessorCount, compute.StartupMemoryMb, compute.MaximumMemoryMb, compute.DynamicMemory, compute.NestedVirtualization),
            facts.Capacity,
            facts.HostMemoryReserveMb,
            errors,
            warnings);
    }

    /// <summary>Each disk goes in the VM's own disk folder under its source file name, numbered when two share a name.</summary>
    private static List<TransferDiskPlan> PlanDisks(IReadOnlyList<TransferDisk> disks, string diskFolder, List<ValidationIssue> errors)
    {
        var used = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var plans = new List<TransferDiskPlan>();
        foreach (var disk in disks)
        {
            var file = disk.FileName;
            if (!IsPlainDiskFileName(file) || disk.SizeBytes <= 0)
            {
                errors.Add(new("disks", $"The disk name \"{file}\" cannot be used on this host."));
                continue;
            }

            var target = file;
            var stem = Path.GetFileNameWithoutExtension(file);
            var extension = Path.GetExtension(file);
            for (var n = 2; !used.Add(target); n++)
            {
                target = $"{stem} ({n.ToString(CultureInfo.InvariantCulture)}){extension}";
            }

            plans.Add(new TransferDiskPlan(disk.Path, Path.Combine(diskFolder, target), disk.SizeBytes));
        }

        return plans;
    }

    /// <summary>A .vhdx or .vhd file name with no folder part. Checkpoint disks never travel (checkpoints block a transfer).</summary>
    private static bool IsPlainDiskFileName(string file) =>
        file.Length is > 0 and <= 200
        && file.IndexOfAny(Path.GetInvalidFileNameChars()) < 0
        && !file.EndsWith('.')
        && Path.GetExtension(file).ToLowerInvariant() is ".vhdx" or ".vhd";

    private static long CeilingMb(long bytes) => (bytes + (1024 * 1024) - 1) / (1024 * 1024);

    [GeneratedRegex("^[0-9A-F]{12}$")]
    private static partial Regex MacDigits();
}
