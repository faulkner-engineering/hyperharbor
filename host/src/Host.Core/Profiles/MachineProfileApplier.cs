using System.Globalization;
using HyperHarbor.Host.Core.Backup;
using HyperHarbor.Host.Core.Lifecycle;
using HyperHarbor.Host.Core.Performance;
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
/// Applies a machine profile to an existing VM (docs/machine-profiles.md, 26d). <see cref="PreviewAsync"/> compares the
/// profile with the VM; <see cref="StartAsync"/> applies the parts the User chose as one job: the backup policy, then the
/// settings that need the VM off (shutting it down first when asked, and starting it again), then the software and
/// guest display settings in the running guest, then the guest part of Performance mode when it was turned on.
/// Nothing is removed that a profile cannot put back: a TPM is never removed, a disk never shrinks, and Secure Boot and
/// the switch are left as they are.
/// </summary>
public sealed class MachineProfileApplier(
    MachineProfileStore profiles,
    IVmInventory inventory,
    IHyperVCompute compute,
    IHyperVDefinitionReader definitions,
    IHyperVBuilder builder,
    VmPerformanceService performance,
    IMachineBackupPolicies? backups,
    ProfileValidator software,
    VmCredentialStore credentials,
    UserStore users,
    UnattendedInstallStore installs,
    SetupProfileApplication application,
    GuestRestart restart,
    IHyperVPowerInvoker power,
    VmJobStore jobs,
    LifecycleOptions options,
    TimeProvider time,
    ILogger<MachineProfileApplier> logger,
    VmConnectionDefaultsStore? connectionDefaults = null,
    ProfileImageStore? images = null,
    IGuestFileCopier? files = null)
{
    private static readonly TimeSpan ShutdownPoll = TimeSpan.FromSeconds(2);

    /// <summary>What the profile would change on the VM, and how.</summary>
    internal sealed record Plan(
        Vm Vm,
        MachineProfile Profile,
        List<MachineProfileChange> Changes,
        List<MachineProfileNote> Notes,
        List<ValidationIssue> Warnings,
        ComputeChange? Compute,
        ComputeState CurrentCompute,
        bool? MacSpoofing,
        bool AddTpm,
        (PerformanceSettings Settings, PerformancePlan Plan)? Performance,
        bool RemovePerformance,
        string? BackupTarget,
        SetupProfile? Setup,
        string? WallpaperPath = null);

    /// <exception cref="VmNotFoundException">No such VM.</exception>
    /// <exception cref="MachineProfileNotFoundException">No such profile.</exception>
    public async Task<MachineProfilePreview> PreviewAsync(Guid userId, Guid vmId, string profileId, CancellationToken cancellationToken)
    {
        var plan = await PlanAsync(userId, vmId, profileId, cancellationToken).ConfigureAwait(false);
        return new MachineProfilePreview(vmId, plan.Vm.Name, plan.Vm.State, profileId, plan.Profile.Name, plan.Changes, plan.Notes, plan.Warnings);
    }

    /// <exception cref="VmNotFoundException">No such VM.</exception>
    /// <exception cref="MachineProfileNotFoundException">No such profile.</exception>
    /// <exception cref="LifecycleValidationException">Nothing in the chosen parts differs from the VM.</exception>
    /// <exception cref="LifecycleConflictException">
    /// The VM must be off for a chosen part (code requiresShutdown), its install is still running, or the software needs
    /// an administrator credential (code credentialRequired).
    /// </exception>
    public async Task<VmJobSnapshot> StartAsync(Guid userId, Guid vmId, ApplyMachineProfileRequest request, Action<VmJobSnapshot>? onFinished, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var plan = await PlanAsync(userId, vmId, request.ProfileId, cancellationToken).ConfigureAwait(false);
        var parts = request.Parts.ToHashSet();
        var chosen = plan.Changes.Where(change => parts.Contains(change.Part)).ToList();
        if (chosen.Count == 0)
        {
            throw new LifecycleValidationException("Nothing in the chosen parts differs from this VM.", [new ValidationIssue("parts", "Nothing in the chosen parts differs from this VM.")]);
        }

        var vm = plan.Vm;
        var needsOff = chosen.Any(change => change.NeedsOff);
        if (vm.State is not (VmState.Off or VmState.Running))
        {
            throw new LifecycleConflictException($"A machine profile applies to a VM that is off or running; {vm.Name} is {vm.State.ToString().ToLowerInvariant()}.");
        }

        if (needsOff && vm.State == VmState.Running && !request.ShutDownToApply)
        {
            throw new LifecycleConflictException(
                $"{vm.Name} must be off for {string.Join(", ", chosen.Where(change => change.NeedsOff).Select(change => change.Setting.ToLowerInvariant()))}. Shut it down first, or choose to shut down, apply, and restart.",
                ContractInfo.ProblemCodes.RequiresShutdown);
        }

        if (installs.Find(vmId) is { IsActive: true })
        {
            throw new LifecycleConflictException($"{vm.Name} is still being installed. Apply the profile once it is ready.");
        }

        GuestCredential? admin = null;
        if (parts.Contains(MachinePart.Software) && plan.Setup is not null || plan.Performance is not null && parts.Contains(MachinePart.Gpu))
        {
            admin = credentials.Find(vmId);
            if (admin is null && parts.Contains(MachinePart.Software) && plan.Setup is not null)
            {
                throw new LifecycleConflictException(
                    $"HyperHarbor has no administrator credential for {vm.Name}. Set it up for Remote Desktop first.",
                    ContractInfo.ProblemCodes.CredentialRequired);
            }
        }

        var account = users.Find(userId)?.VmAccountName;
        return jobs.Start(VmJobKind.ApplyMachineProfile, vmId, userId, $"Applying {plan.Profile.Name}",
            context => RunAsync(plan, parts, admin, account, context), onFinished);
    }

    private async Task RunAsync(Plan plan, HashSet<MachinePart> parts, GuestCredential? admin, string? account, VmJobContext context)
    {
        var vm = plan.Vm;
        var applied = 0;
        var problems = new List<string>();
        var restarted = false;
        bool Chosen(MachinePart part) => parts.Contains(part) && plan.Changes.Any(change => change.Part == part);

        if (Chosen(MachinePart.Connection) && plan.Profile.Display?.Connection is { } connection)
        {
            connectionDefaults!.Save(new VmConnectionDefaults(vm.Id, connection, plan.Profile.Name, time.GetUtcNow()));
            applied++;
        }

        if (Chosen(MachinePart.Backup) && plan.BackupTarget is not null)
        {
            context.Report("Setting the backup policy", 5);
            backups!.Set(vm.Id, plan.BackupTarget, plan.Profile.Backup!);
            applied++;
        }

        var offWork = plan.Changes.Any(change => change.NeedsOff && parts.Contains(change.Part));
        var wasRunning = vm.State == VmState.Running;
        var running = wasRunning;
        if (offWork)
        {
            if (running)
            {
                await ShutDownAsync(vm, context).ConfigureAwait(false);
                running = false;
            }

            var change = Chosen(MachinePart.Compute) ? plan.Compute : null;
            if (Chosen(MachinePart.Network) && plan.MacSpoofing is { } spoofing)
            {
                change = (change ?? new ComputeChange()) with { MacAddressSpoofing = spoofing };
            }

            if (change is not null)
            {
                context.Report("Changing processors, memory, and adapters", 40);
                await compute.ApplyAsync(vm.Id, change, Desired(plan.CurrentCompute, change), context.Stopping).ConfigureAwait(false);
                applied++;
            }

            if (Chosen(MachinePart.Firmware) && plan.AddTpm)
            {
                context.Report("Adding a virtual TPM", 50);
                await builder.EnableTpmAsync(vm.Id, context.Stopping).ConfigureAwait(false);
                applied++;
            }

            if (Chosen(MachinePart.Gpu))
            {
                if (plan.Performance is { } performancePlan)
                {
                    context.Report("Adding the GPU partition", 55);
                    await performance.ApplyWithinJobAsync(vm.Name, vm.Id, performancePlan.Settings, performancePlan.Plan, context).ConfigureAwait(false);
                    applied++;
                }
                else if (plan.RemovePerformance)
                {
                    context.Report("Turning Performance mode off", 55);
                    await performance.RemoveWithinJobAsync(vm.Id, context.Stopping).ConfigureAwait(false);
                    applied++;
                }
            }
        }
        else if (Chosen(MachinePart.Network) && plan.MacSpoofing is { } spoofing)
        {
            var change = new ComputeChange(MacAddressSpoofing: spoofing);
            await compute.ApplyAsync(vm.Id, change, Desired(plan.CurrentCompute, change), context.Stopping).ConfigureAwait(false);
            applied++;
        }

        var softwareChosen = Chosen(MachinePart.Software) && plan.Setup is not null;
        var guestPerformance = Chosen(MachinePart.Gpu) && plan.Performance is not null;
        var guestWork = softwareChosen || guestPerformance && admin is not null;
        if (guestPerformance && admin is null)
        {
            problems.Add("Performance mode is on, but HyperHarbor has no administrator credential for the guest, so the GPU driver was not set up. Run Set up the guest in the Performance dialog.");
        }
        if (!running && (wasRunning || guestWork))
        {
            context.Report("Starting the virtual machine", 65);
            await power.InvokeAsync(vm.Id, VmAction.Start, context.Stopping).ConfigureAwait(false);
            running = true;
        }

        if (guestWork)
        {
            context.Report("Waiting for the guest to answer", 70);
            if (!await restart.WaitUntilAnswersAsync(vm.Id, context.Stopping).ConfigureAwait(false))
            {
                problems.Add($"{vm.Name} did not answer Remote Desktop within {restart.Timeout}, so the software and the guest part of Performance mode were not applied. Apply the profile again once it runs.");
            }
            else
            {
                if (softwareChosen && plan.WallpaperPath is { } wallpaper && files is not null)
                {
                    try
                    {
                        context.Report($"Copying the wallpaper {Path.GetFileName(wallpaper)}", 75);
                        await files.CopyAsync(vm.Id, admin!, wallpaper, ProfileImageStore.GuestFolder, context.Stopping).ConfigureAwait(false);
                    }
                    catch (Exception ex) when (ex is GuestOperationException or GuestUnavailableException or IOException)
                    {
                        problems.Add($"The wallpaper image was not copied into the guest: {ex.Message}");
                    }
                }

                if (softwareChosen)
                {
                    var outcome = await application.ApplyAsync(vm.Id, admin!, plan.Setup!, step => context.Report(step, 80), context.Stopping, account).ConfigureAwait(false);
                    applied += outcome.Applied;
                    problems.AddRange(outcome.Problems);
                    if (outcome.RestartNeeded)
                    {
                        context.Report("Restarting to finish", 90);
                        restarted = await restart.RestartAndWaitAsync(vm.Id, context.Stopping).ConfigureAwait(false);
                        if (!restarted)
                        {
                            problems.Add($"The VM did not answer Remote Desktop within {restart.Timeout} of a restart. Restart it yourself to finish.");
                        }
                    }
                }

                if (guestPerformance)
                {
                    try
                    {
                        context.Report("Setting up the GPU driver in the guest", 92);
                        await performance.GuestSetupWithinJobAsync(vm.Id, vm.Name, context).ConfigureAwait(false);
                    }
                    catch (Exception ex) when (ex is LifecycleConflictException or GuestOperationException or GuestUnavailableException)
                    {
                        problems.Add($"The GPU driver was not set up in the guest: {ex.Message} Run Set up the guest in the Performance dialog.");
                    }
                }
            }
        }

        context.ReportSetupResult(new SetupProfileResult(applied, problems, restarted, time.GetUtcNow()));
        logger.LogInformation(
            "Applied the machine profile {Profile} to {Name} ({VmId}): parts {Parts}, {Applied} items, {Problems} problems.",
            plan.Profile.Name, vm.Name, vm.Id, string.Join(",", parts), applied, problems.Count);
    }

    private async Task ShutDownAsync(Vm vm, VmJobContext context)
    {
        context.Report("Shutting down the guest", 10);
        await power.InvokeAsync(vm.Id, VmAction.Shutdown, context.Stopping).ConfigureAwait(false);
        var deadline = time.GetUtcNow() + TimeSpan.FromSeconds(options.ShutdownTimeoutSeconds);
        while ((await inventory.GetAsync(vm.Id, context.Stopping).ConfigureAwait(false))?.State != VmState.Off)
        {
            if (time.GetUtcNow() >= deadline)
            {
                throw new LifecycleConflictException(
                    $"{vm.Name} did not shut down within {options.ShutdownTimeoutSeconds} seconds, so nothing that needs it off was changed. It was not forced off.");
            }

            await Task.Delay(ShutdownPoll, time, context.Stopping).ConfigureAwait(false);
        }
    }

    private static ComputeState Desired(ComputeState current, ComputeChange change) => current with
    {
        ProcessorCount = change.ProcessorCount ?? current.ProcessorCount,
        StartupMemoryMb = change.StartupMemoryMb ?? current.StartupMemoryMb,
        MaximumMemoryMb = change.MaximumMemoryMb ?? current.MaximumMemoryMb,
        DynamicMemory = change.DynamicMemory ?? current.DynamicMemory,
        NestedVirtualization = change.NestedVirtualization ?? current.NestedVirtualization,
        MacAddressSpoofing = change.MacAddressSpoofing ?? current.MacAddressSpoofing,
    };

    internal async Task<Plan> PlanAsync(Guid userId, Guid vmId, string profileId, CancellationToken cancellationToken)
    {
        var vm = await inventory.GetAsync(vmId, cancellationToken).ConfigureAwait(false) ?? throw new VmNotFoundException(vmId);
        var profile = profiles.Get(userId, profileId).Profile;
        var changes = new List<MachineProfileChange>();
        var notes = new List<MachineProfileNote>();
        var warnings = new List<ValidationIssue>();
        var windows = profile.Os == InstallOs.Windows;

        var guestOs = vm.GuestOs?.Family;
        if (guestOs is GuestOsFamily.Windows && !windows || guestOs is GuestOsFamily.Linux && windows)
        {
            warnings.Add(new("os", $"The profile is for {profile.Os}, but {vm.Name} runs {guestOs}."));
        }

        var current = await compute.ReadAsync(vmId, cancellationToken).ConfigureAwait(false);
        var vmPerformance = await performance.GetAsync(vmId, cancellationToken).ConfigureAwait(false);
        var gpuWanted = windows ? profile.Gpu : null;

        // Performance mode sets processors and fixed memory itself, so compute leaves them to it when it is applied.
        var performanceApplies = gpuWanted?.Enabled == true;
        ComputeChange? computeChange = null;
        if (profile.Compute is { } computeWanted)
        {
            computeChange = new ComputeChange();
            if (computeWanted.Processors is { } processors && processors != current.ProcessorCount)
            {
                changes.Add(new(MachinePart.Compute, "Virtual processors", Text(current.ProcessorCount), Text(processors), true, false));
                computeChange = computeChange with { ProcessorCount = performanceApplies ? null : processors };
            }

            if (computeWanted.Memory is { } memory && !performanceApplies)
            {
                var maximum = memory.Dynamic ? memory.MaximumMb ?? Math.Max(current.MaximumMemoryMb, memory.StartupMb) : memory.StartupMb;
                if (memory.StartupMb != current.StartupMemoryMb || memory.Dynamic != current.DynamicMemory || maximum != current.MaximumMemoryMb)
                {
                    changes.Add(new(MachinePart.Compute, "Memory", Memory(current.StartupMemoryMb, current.MaximumMemoryMb, current.DynamicMemory), Memory(memory.StartupMb, maximum, memory.Dynamic), true, false));
                    computeChange = computeChange with { StartupMemoryMb = memory.StartupMb, MaximumMemoryMb = maximum, DynamicMemory = memory.Dynamic };
                }
            }

            if (computeWanted.NestedVirtualization is { } nested && nested != current.NestedVirtualization)
            {
                changes.Add(new(MachinePart.Compute, "Nested virtualization", OnOff(current.NestedVirtualization), OnOff(nested), true, false));
                computeChange = computeChange with { NestedVirtualization = nested };
            }

            if (computeWanted.DiskSizeGb is not null)
            {
                notes.Add(new("compute", "The disk size is used when creating a VM; this VM's disk is left as it is."));
            }

            if (computeChange == new ComputeChange())
            {
                computeChange = null;
            }
        }

        bool? macSpoofing = null;
        if (profile.Network is { } network)
        {
            if (network.MacAddressSpoofing is { } spoofing && spoofing != current.MacAddressSpoofing)
            {
                changes.Add(new(MachinePart.Network, "MAC address spoofing", OnOff(current.MacAddressSpoofing), OnOff(spoofing), false, false));
                macSpoofing = spoofing;
            }

            if (network.Switch is not null)
            {
                notes.Add(new("network", "The VM stays on its switch; the profile's switch is used when creating a VM."));
            }
        }

        var addTpm = false;
        if (profile.Firmware is { } firmware)
        {
            if (firmware.Tpm is { } tpm)
            {
                var has = (await definitions.ReadAsync(vmId, cancellationToken).ConfigureAwait(false)).TpmEnabled;
                if (tpm && !has)
                {
                    changes.Add(new(MachinePart.Firmware, "Virtual TPM", "Off", "On", true, false));
                    addTpm = true;
                }
                else if (!tpm && has)
                {
                    notes.Add(new("firmware", "The VM keeps its TPM: a profile never removes one, since what it protects would be lost."));
                }
            }

            if (firmware.SecureBoot is not null)
            {
                notes.Add(new("firmware", "Secure Boot is left as it is on an existing VM."));
            }
        }

        (PerformanceSettings, PerformancePlan)? performancePlan = null;
        var removePerformance = false;
        if (!windows && profile.Gpu?.Enabled == true)
        {
            notes.Add(new("gpu", "Performance mode is for Windows VMs."));
        }
        else if (gpuWanted is { Enabled: true } gpu)
        {
            var share = gpu.Share ?? new MachineGpuShare();
            var settings = new PerformanceSettings(
                profile.Compute?.Processors ?? current.ProcessorCount,
                profile.Compute?.Memory?.StartupMb ?? current.StartupMemoryMb,
                new GpuPartitionShare(null, share.VramPercent, share.EncodePercent, share.DecodePercent, share.ComputePercent),
                null,
                null,
                new PerformanceRdpSettings(gpu.HardwareEncoding ?? false),
                AcknowledgeWarnings: true);
            var currentShare = vmPerformance.Settings?.Gpu;
            var same = vmPerformance.Enabled && currentShare is not null
                && currentShare.VramPercent == share.VramPercent && currentShare.EncodePercent == share.EncodePercent
                && currentShare.DecodePercent == share.DecodePercent && currentShare.ComputePercent == share.ComputePercent
                && vmPerformance.Settings!.ProcessorCount == settings.ProcessorCount && vmPerformance.Settings.MemoryMb == settings.MemoryMb;
            if (!same)
            {
                try
                {
                    if (gpu.Adapter is { } adapter)
                    {
                        var found = await performance.FindGpuAsync(adapter, cancellationToken).ConfigureAwait(false)
                            ?? throw new LifecycleConflictException($"This host has no partitionable GPU named like \"{adapter}\".");
                        settings = settings with { Gpu = settings.Gpu! with { InstancePath = found.InstancePath } };
                    }

                    performancePlan = await performance.CheckAsync(settings, cancellationToken).ConfigureAwait(false);
                    changes.Add(new(
                        MachinePart.Gpu,
                        vmPerformance.Enabled ? "GPU share" : "Performance mode",
                        vmPerformance.Enabled ? Share(currentShare, vmPerformance.Settings) : "Off",
                        $"On: {share.VramPercent}% video memory, {settings.ProcessorCount} processors, {Gb(settings.MemoryMb)} fixed",
                        true,
                        false));
                }
                catch (Exception ex) when (ex is LifecycleConflictException or LifecycleValidationException)
                {
                    warnings.Add(new("gpu", ex.Message));
                }
            }
        }
        else if (gpuWanted is { Enabled: false } && vmPerformance.Enabled)
        {
            changes.Add(new(MachinePart.Gpu, "Performance mode", "On", "Off (memory stays fixed, checkpoints stay off)", true, false));
            removePerformance = true;
        }

        string? backupTarget = null;
        if (profile.Backup is { Enabled: true, Target: { } targetName })
        {
            backupTarget = backups?.FindTarget(targetName);
            if (backupTarget is null)
            {
                warnings.Add(new("backup.target", $"This host has no backup target named \"{targetName}\"."));
            }
            else
            {
                changes.Add(new(MachinePart.Backup, "Backup policy", null, $"To {targetName}", false, false));
            }
        }
        else if (profile.Backup is { Enabled: false })
        {
            notes.Add(new("backup", "Backups are not turned off from a profile; remove the VM's policy in Backups."));
        }

        SetupProfile? setup = null;
        string? wallpaper = null;
        if (windows)
        {
            wallpaper = MachineProfileVmCreation.WallpaperSource(images, userId, profile);
            setup = MachineProfileVmCreation.SetupSnapshot(software, profile, wallpaper, warnings);
            if (setup is not null)
            {
                var removals = (setup.Remove?.Appx?.Count ?? 0) + (setup.Remove?.Capabilities?.Count ?? 0) + (setup.Remove?.Features?.Count ?? 0);
                changes.Add(new(
                    MachinePart.Software,
                    "Software and display",
                    null,
                    $"{Count(setup.Install?.Count ?? 0, "package")}, {Count(removals, "removal")}, {Count(setup.Tweaks?.Count ?? 0, "setting")}{(setup.Browser is null ? "" : $", {setup.Browser.App.Name ?? setup.Browser.App.Id}")}",
                    false,
                    true));
            }
        }

        if (profile.Account is not null)
        {
            notes.Add(new("account", "Account settings are used when the OS is installed, so they apply when creating a VM."));
        }

        if (profile.Display?.Connection is { } wanted && connectionDefaults is not null)
        {
            var given = connectionDefaults.Find(vmId)?.Connection;
            if (given is null || Describe(given) != Describe(wanted))
            {
                changes.Add(new(MachinePart.Connection, "Remote Desktop defaults", given is null ? null : Describe(given), Describe(wanted), false, false));
            }
        }

        return new Plan(vm, profile, changes, notes, warnings, computeChange, current, macSpoofing, addTpm, performancePlan, removePerformance, backupTarget, setup,
            setup is null ? null : wallpaper);
    }

    /// <summary>"All monitors, full screen, clipboard shared, camera not shared"; settings left to the client are not named.</summary>
    internal static string Describe(MachineConnection connection)
    {
        var parts = new List<string>();
        if (connection.Monitors is { } monitors)
        {
            parts.Add(monitors == ConnectionMonitors.All ? "all monitors" : "one monitor");
        }

        if (connection.Window is { } window)
        {
            parts.Add(window == ConnectionWindow.FullScreen ? "full screen" : "windowed");
        }

        foreach (var (name, value) in new (string, bool?)[]
        {
            ("clipboard", connection.Redirect?.Clipboard),
            ("printers", connection.Redirect?.Printers),
            ("microphone", connection.Redirect?.Microphone),
            ("camera", connection.Redirect?.Camera),
        })
        {
            if (value is { } shared)
            {
                parts.Add(shared ? $"{name} shared" : $"{name} not shared");
            }
        }

        var text = parts.Count == 0 ? "The device's defaults" : string.Join(", ", parts);
        return char.ToUpperInvariant(text[0]) + text[1..];
    }

    private static string Text(int value) => value.ToString(CultureInfo.InvariantCulture);

    private static string OnOff(bool value) => value ? "On" : "Off";

    private static string Count(int count, string noun) => $"{count} {noun}{(count == 1 ? "" : "s")}";

    private static string Gb(long megabytes) => $"{(megabytes / 1024.0).ToString("0.#", CultureInfo.InvariantCulture)} GB";

    private static string Memory(long startup, long maximum, bool dynamic) =>
        dynamic ? $"{Gb(startup)} dynamic, up to {Gb(maximum)}" : $"{Gb(startup)} fixed";

    private static string Share(GpuPartitionShare? share, PerformanceSettings? settings) =>
        share is null || settings is null ? "On" : $"On: {share.VramPercent}% video memory, {settings.ProcessorCount} processors, {Gb(settings.MemoryMb)} fixed";
}
