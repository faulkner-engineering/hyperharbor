using HyperHarbor.Host.Core.Provisioning;
using HyperHarbor.Shared.Contracts.Profiles;

namespace HyperHarbor.Host.Core.Profiles;

/// <summary>What happened to one item of a setup profile in the guest.</summary>
/// <param name="RestartNeeded">Windows needs a restart to finish it (a capability or feature removal).</param>
public sealed record ApplyItemResult(string Item, bool Ok, string? Error = null, bool RestartNeeded = false);

/// <summary>Changes a running Windows guest for a setup profile, as its administrator. Each step reports per item.</summary>
public interface IGuestProfileApplier
{
    Task<IReadOnlyList<ApplyItemResult>> InstallPackagesAsync(Guid vmId, GuestCredential admin, IReadOnlyList<PackageInstall> packages, CancellationToken cancellationToken);

    Task<IReadOnlyList<ApplyItemResult>> RemoveAsync(
        Guid vmId,
        GuestCredential admin,
        IReadOnlyList<ProfileItem> appx,
        IReadOnlyList<ProfileItem> capabilities,
        IReadOnlyList<ProfileItem> features,
        CancellationToken cancellationToken);

    Task<IReadOnlyList<ApplyItemResult>> WriteSettingsAsync(Guid vmId, GuestCredential admin, IReadOnlyList<RegistryWrite> writes, CancellationToken cancellationToken);
}

/// <summary>The outcome of applying a setup profile, before any restart.</summary>
public sealed record ApplyOutcome(int Applied, IReadOnlyList<string> Problems, bool RestartNeeded);

/// <summary>
/// Applies a setup profile to a running Windows guest in three steps (packages, removals, settings). One item failing
/// never stops the others; it becomes a problem. A step that cannot run at all (the guest stops answering) throws,
/// so the caller can retry the whole application, which is safe to repeat.
/// </summary>
public sealed class SetupProfileApplication(SetupProfilePlanner planner, IGuestProfileApplier applier)
{
    /// <param name="report">Called with what is happening now, for the install's Step text.</param>
    public async Task<ApplyOutcome> ApplyAsync(Guid vmId, GuestCredential admin, SetupProfile profile, Action<string> report, CancellationToken cancellationToken)
    {
        var plan = planner.Plan(profile);
        var results = new List<ApplyItemResult>();

        try
        {
            if (plan.Packages.Count > 0)
            {
                report($"Applying {profile.Name}: installing {Count(plan.Packages.Count, "package")}");
                results.AddRange(await applier.InstallPackagesAsync(vmId, admin, plan.Packages, cancellationToken).ConfigureAwait(false));
            }

            if (plan.Appx.Count + plan.Capabilities.Count + plan.Features.Count > 0)
            {
                report($"Applying {profile.Name}: removing apps and features");
                results.AddRange(await applier.RemoveAsync(vmId, admin, plan.Appx, plan.Capabilities, plan.Features, cancellationToken).ConfigureAwait(false));
            }

            if (plan.Registry.Count > 0)
            {
                report($"Applying {profile.Name}: writing settings");
                results.AddRange(await applier.WriteSettingsAsync(vmId, admin, plan.Registry, cancellationToken).ConfigureAwait(false));
            }
        }
        catch (Exception ex) when (GuestErrors.IsGuestError(ex))
        {
            throw GuestErrors.Sanitize(ex, admin.Password);
        }

        var problems = plan.Problems
            .Concat(results.Where(result => !result.Ok).Select(result => GuestErrors.Clean($"{result.Item}: {result.Error ?? "failed"}", admin.Password)))
            .ToList();
        return new ApplyOutcome(results.Count(result => result.Ok), problems, results.Any(result => result.RestartNeeded));
    }

    private static string Count(int count, string noun) => count == 1 ? $"1 {noun}" : $"{count} {noun}s";
}
