using HyperHarbor.Host.Core.Lifecycle;
using HyperHarbor.Host.Core.Power;
using HyperHarbor.Host.Core.Provisioning;
using HyperHarbor.Shared.Contracts;
using HyperHarbor.Shared.Contracts.Profiles;
using HyperHarbor.Shared.Contracts.Vms;
using Microsoft.Extensions.Logging;

namespace HyperHarbor.Host.Core.Profiles;

/// <summary>
/// Lists the provisioned Appx packages of a running Windows VM, rated from the Appx catalog and compared with
/// the clean baseline for its build, and records clean baselines.
/// </summary>
public sealed class AppxInventoryService(
    IVmInventory inventory,
    VmCredentialStore credentials,
    IGuestProfileReader reader,
    AppxBaselineStore baselines,
    Catalogs catalogs,
    TimeProvider time,
    ILogger<AppxInventoryService> logger)
{
    /// <exception cref="VmNotFoundException">No such VM.</exception>
    /// <exception cref="LifecycleConflictException">Not a running Windows guest (vmNotRunning), or no administrator credential (credentialRequired).</exception>
    public async Task<VmAppxInventory> ListAsync(Guid vmId, CancellationToken cancellationToken)
    {
        var (_, guest) = await ReadAsync(vmId, cancellationToken).ConfigureAwait(false);
        var found = baselines.Find(guest.CurrentBuild, guest.Edition);
        HashSet<string>? clean = found is { } match ? new(match.Baseline.Packages, StringComparer.OrdinalIgnoreCase) : null;

        var packages = guest.Packages
            .OrderBy(package => package.Name, StringComparer.OrdinalIgnoreCase)
            .Select(package => Describe(package.Name, package.Version, clean?.Contains(package.Name)))
            .ToList();
        var removed = found is { } baseline
            ? AppxDiff.Compare(baseline.Baseline.Packages, guest.Packages.Select(package => package.Name)).Removed.Select(name => Describe(name, "", true)).ToList()
            : [];

        return new VmAppxInventory(vmId, guest.FullBuild, guest.Edition, found is { } info ? Info(info.Baseline, info.Approximate) : null, packages, removed);
    }

    /// <summary>Records the VM's provisioned packages as the clean baseline for its build and edition.</summary>
    /// <returns>The baseline, or null when <paramref name="onlyIfMissing"/> and one existed.</returns>
    public async Task<AppxBaselineInfo?> RecordBaselineAsync(Guid vmId, AppxBaselineSource source, bool onlyIfMissing, CancellationToken cancellationToken)
    {
        var (vm, guest) = await ReadAsync(vmId, cancellationToken).ConfigureAwait(false);
        var baseline = new AppxBaseline(
            AppxBaseline.KeyOf(guest.CurrentBuild, guest.Edition),
            guest.FullBuild,
            guest.Edition,
            time.GetUtcNow(),
            new AppxBaselineOrigin(source, vmId, vm.Name),
            guest.Packages.Select(package => package.Name).Distinct(StringComparer.OrdinalIgnoreCase).Order(StringComparer.OrdinalIgnoreCase).ToList());
        if (!baselines.Save(baseline, onlyIfMissing))
        {
            return null;
        }

        logger.LogInformation("Recorded the clean Appx baseline {Key} ({Count} packages) from {Name}.", baseline.Key, baseline.Packages.Count, vm.Name);
        return Info(baseline, false);
    }

    /// <summary>A package with its catalog name, publisher, and rating.</summary>
    public AppxPackage Describe(string name, string version, bool? inBaseline)
    {
        var known = catalogs.AppxIndex.GetValueOrDefault(name);
        return new AppxPackage(
            name,
            known?.FriendlyName ?? name,
            version,
            known?.Publisher ?? PublisherOf(name),
            known?.Rating ?? AppxRating.Unrated,
            known?.Note,
            inBaseline);
    }

    /// <summary>The publisher from the package name's first part, with Microsoft's several prefixes folded together.</summary>
    public static string PublisherOf(string name)
    {
        var prefix = name.Split('.', 2)[0];
        return prefix.StartsWith("Microsoft", StringComparison.OrdinalIgnoreCase) || prefix.Equals("Windows", StringComparison.OrdinalIgnoreCase)
            ? "Microsoft"
            : name.Contains('.', StringComparison.Ordinal) ? prefix : "Other";
    }

    private async Task<(Vm Vm, GuestAppxInventory Guest)> ReadAsync(Guid vmId, CancellationToken cancellationToken)
    {
        var vm = await inventory.GetAsync(vmId, cancellationToken).ConfigureAwait(false) ?? throw new VmNotFoundException(vmId);
        if (vm.State != VmState.Running || vm.GuestOs?.Family != GuestOsFamily.Windows)
        {
            throw new LifecycleConflictException($"{vm.Name} must be a running Windows guest that has finished starting.", ContractInfo.ProblemCodes.VmNotRunning);
        }

        var admin = credentials.Find(vmId)
            ?? throw new LifecycleConflictException(
                $"HyperHarbor has no administrator credential for {vm.Name}. Set it up for Remote Desktop first.",
                ContractInfo.ProblemCodes.CredentialRequired);
        try
        {
            return (vm, await reader.ReadAppxAsync(vmId, admin, cancellationToken).ConfigureAwait(false));
        }
        catch (Exception ex) when (GuestErrors.IsGuestError(ex))
        {
            throw GuestErrors.Sanitize(ex, admin.Password);
        }
    }

    private static AppxBaselineInfo Info(AppxBaseline baseline, bool approximate) =>
        new(baseline.Key, baseline.Build, baseline.Edition, baseline.RecordedAt, baseline.Source.How, baseline.Source.VmName, approximate);
}
