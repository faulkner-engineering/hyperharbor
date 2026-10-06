using HyperHarbor.Host.Core.Lifecycle;
using HyperHarbor.Host.Core.Power;
using HyperHarbor.Host.Core.Provisioning;
using HyperHarbor.Host.Core.Unattend;
using HyperHarbor.Host.Core.Users;
using HyperHarbor.Shared.Contracts;
using HyperHarbor.Shared.Contracts.Profiles;
using HyperHarbor.Shared.Contracts.Unattend;
using HyperHarbor.Shared.Contracts.Vms;
using Microsoft.Extensions.Logging;

namespace HyperHarbor.Host.Core.Profiles;

/// <summary>
/// Applies one of a User's setup profiles to a running Windows VM as an applySetupProfile job, the way the install
/// watcher does after a new install: packages, removals, settings (also into the User's account when it has a
/// profile in the VM), and one restart when something needs it and the caller allows it.
/// </summary>
public sealed class SetupProfileJobs(
    IVmInventory inventory,
    VmCredentialStore credentials,
    SetupProfileStore profiles,
    UserStore users,
    UnattendedInstallStore installs,
    SetupProfileApplication application,
    GuestRestart restart,
    VmJobStore jobs,
    TimeProvider time,
    ILogger<SetupProfileJobs> logger)
{
    /// <exception cref="VmNotFoundException">The VM does not exist.</exception>
    /// <exception cref="SetupProfileNotFoundException">The User has no such setup profile.</exception>
    /// <exception cref="LifecycleConflictException">
    /// The VM is not a running Windows guest, its unattended install is still running, or no administrator credential
    /// is stored (code credentialRequired).
    /// </exception>
    /// <exception cref="LifecycleValidationException">The profile's file on the host does not read.</exception>
    /// <exception cref="VmBusyException">Another job or power action holds the VM.</exception>
    public async Task<VmJobSnapshot> StartAsync(
        Guid vmId,
        Guid userId,
        ApplySetupProfileRequest request,
        Action<VmJobSnapshot>? onFinished,
        CancellationToken cancellationToken)
    {
        var vm = await inventory.GetAsync(vmId, cancellationToken).ConfigureAwait(false) ?? throw new VmNotFoundException(vmId);
        var profile = profiles.Get(userId, request.ProfileId).Profile;
        if (vm.State != VmState.Running || vm.GuestOs?.Family != GuestOsFamily.Windows)
        {
            throw new LifecycleConflictException($"{vm.Name} must be a running Windows guest that has finished starting.");
        }

        if (installs.Find(vmId) is { IsActive: true })
        {
            throw new LifecycleConflictException($"{vm.Name} is still being installed. Apply the profile once it is ready.");
        }

        var admin = credentials.Find(vmId)
            ?? throw new LifecycleConflictException(
                $"HyperHarbor has no administrator credential for {vm.Name}. Set it up for Remote Desktop first.",
                ContractInfo.ProblemCodes.CredentialRequired);
        var account = users.Find(userId)?.VmAccountName;

        return jobs.Start(VmJobKind.ApplySetupProfile, vmId, userId, $"Applying {profile.Name}",
            context => RunAsync(vm.Name, vmId, admin, account, profile, request.RestartIfNeeded, context), onFinished);
    }

    private async Task RunAsync(string vmName, Guid vmId, GuestCredential admin, string? account, SetupProfile profile, bool restartIfNeeded, VmJobContext context)
    {
        var progress = 0;
        var outcome = await application.ApplyAsync(
            vmId,
            admin,
            profile,
            step => context.Report(step, progress = Math.Min(progress + 25, 75)),
            context.Stopping,
            account).ConfigureAwait(false);

        var restarted = false;
        var problems = outcome.Problems;
        if (outcome.RestartNeeded && restartIfNeeded)
        {
            context.Report($"Applying {profile.Name}: restarting to finish", 80);
            restarted = await restart.RestartAndWaitAsync(vmId, context.Stopping).ConfigureAwait(false);
            if (!restarted)
            {
                problems = [.. problems, $"The VM did not answer Remote Desktop within {restart.Timeout} of a restart. Restart it yourself to finish."];
            }
        }

        var result = new SetupProfileResult(outcome.Applied, problems, restarted, time.GetUtcNow(), RestartPending: outcome.RestartNeeded && !restartIfNeeded);
        context.ReportSetupResult(result);
        logger.LogInformation(
            "Applied the setup profile {Profile} to {Name} ({VmId}): {Applied} items, {Problems} problems, restarted {Restarted}, restart pending {Pending}.",
            profile.Name, vmName, vmId, result.Applied, result.Problems.Count, result.Restarted, result.RestartPending);
    }
}
