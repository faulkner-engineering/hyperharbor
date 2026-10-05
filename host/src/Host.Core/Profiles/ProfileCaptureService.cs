using HyperHarbor.Host.Core.Lifecycle;
using HyperHarbor.Host.Core.Power;
using HyperHarbor.Host.Core.Provisioning;
using HyperHarbor.Host.Core.Users;
using HyperHarbor.Shared.Contracts;
using HyperHarbor.Shared.Contracts.Profiles;
using HyperHarbor.Shared.Contracts.Vms;
using Microsoft.Extensions.Logging;

namespace HyperHarbor.Host.Core.Profiles;

/// <summary>
/// Captures a draft setup profile from a running Windows VM: winget export for what to install, the provisioned
/// Appx packages against the clean baseline for what was removed, the catalog tweaks that are set, and the store
/// extensions in the User's browsers. Read-only; three PowerShell Direct reads as the VM's administrator.
/// </summary>
public sealed class ProfileCaptureService(
    IVmInventory inventory,
    VmCredentialStore credentials,
    IGuestProfileReader reader,
    AppxInventoryService appx,
    ProvisioningStore accounts,
    UserStore users,
    Catalogs catalogs,
    ILogger<ProfileCaptureService> logger)
{
    /// <summary>winget ids that come with Windows or with other packages; listed, but not ticked.</summary>
    private static readonly string[] PartOfWindows =
    [
        "Microsoft.AppInstaller", "Microsoft.DesktopAppInstaller", "Microsoft.UI.Xaml", "Microsoft.VCLibs", "Microsoft.VCRedist",
        "Microsoft.Edge", "Microsoft.EdgeWebView2Runtime", "Microsoft.WindowsTerminal", "Microsoft.WindowsAppRuntime",
        "Microsoft.OneDrive", "Microsoft.Teams", "Microsoft.DotNet.DesktopRuntime", "Microsoft.DotNet.Runtime",
    ];

    /// <exception cref="VmNotFoundException">No such VM.</exception>
    /// <exception cref="LifecycleConflictException">Not a running Windows guest (vmNotRunning), or no administrator credential (credentialRequired).</exception>
    public async Task<ProfileDraft> CaptureAsync(Guid vmId, Guid userId, CancellationToken cancellationToken)
    {
        // Reading the Appx packages also checks the VM and the credential, with the same errors.
        var packages = await appx.ListAsync(vmId, cancellationToken).ConfigureAwait(false);
        var vm = (await inventory.GetAsync(vmId, cancellationToken).ConfigureAwait(false))!;
        var admin = credentials.Find(vmId)
            ?? throw new LifecycleConflictException($"HyperHarbor has no administrator credential for {vm.Name}.", ContractInfo.ProblemCodes.CredentialRequired);
        var account = accounts.Find(vmId, userId)?.AccountName ?? users.Find(userId)?.VmAccountName ?? "hh-owner";

        GuestInstalledSoftware installed;
        GuestBrowsersAndSettings browsers;
        var reads = catalogs.Tweaks.SelectMany(tweak => tweak.Values).Select(value => new GuestRegistryRead(value.Key, value.Name)).Distinct().ToList();
        try
        {
            installed = await reader.ReadInstalledAsync(vmId, admin, cancellationToken).ConfigureAwait(false);
            browsers = await reader.ReadBrowsersAndSettingsAsync(
                vmId, admin, account, catalogs.Browsers.Select(browser => (browser.Id, browser.UserData)).ToList(), reads, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (GuestErrors.IsGuestError(ex))
        {
            throw GuestErrors.Sanitize(ex, admin.Password);
        }

        var warnings = new List<string>();
        var listPrograms = installed.WingetPackages is not { Count: > 0 };
        if (installed.WingetPackages is null)
        {
            warnings.Add($"winget could not run in the VM, so the install list is empty; the installed programs are listed instead. ({GuestErrors.Clean(installed.WingetError ?? "", admin.Password)})");
        }
        else if (installed.WingetPackages.Count == 0)
        {
            warnings.Add("winget export found nothing it can install from its sources; the installed programs are listed instead. Search for them in the package picker.");
        }

        if (packages.Baseline is null)
        {
            warnings.Add($"There is no clean Appx baseline for Windows {packages.Build} {packages.Edition}, so removed packages cannot be found. Record one from a clean VM of that build.");
        }
        else if (packages.Baseline.Approximate)
        {
            warnings.Add($"The Appx baseline is for {packages.Baseline.Edition}, not {packages.Edition}; check the removed packages.");
        }

        if (!browsers.ProfileFound)
        {
            warnings.Add($"{account} has not signed in to the VM yet, so extensions were read from every profile and its own settings could not be read.");
        }
        else if (browsers.SettingsError is { } settingsError)
        {
            warnings.Add($"The settings of {account} could not be read, so only machine-wide tweaks were checked. ({GuestErrors.Clean(settingsError, admin.Password)})");
        }

        var draft = new ProfileDraft(
            vmId,
            vm.Name,
            packages.Build,
            packages.Edition,
            packages.Baseline,
            Install(installed),
            RemoveAppx(packages),
            Tweaks(browsers),
            Browsers(browsers),
            listPrograms ? installed.Programs.Order(StringComparer.CurrentCultureIgnoreCase).ToList() : [],
            warnings);
        logger.LogInformation(
            "Captured a setup profile draft from {Name}: {Install} packages, {Remove} removed Appx packages, {Tweaks} tweaks, {Extensions} extensions.",
            vm.Name, draft.Install.Count, draft.RemoveAppx.Count, draft.Tweaks.Count, draft.Browsers.Sum(browser => browser.Extensions.Count));
        return draft;
    }

    private List<DraftItem> Install(GuestInstalledSoftware installed) =>
        (installed.WingetPackages ?? [])
            .Where(id => PackageAliasResolver.Classify(id) is InstallEntryKind.Winget or InstallEntryKind.MicrosoftStore)
            .Select(id =>
            {
                var windows = PartOfWindows.Any(prefix => id.Equals(prefix, StringComparison.OrdinalIgnoreCase) || id.StartsWith(prefix + ".", StringComparison.OrdinalIgnoreCase));
                return new DraftItem(id, catalogs.PackageIdIndex.GetValueOrDefault(id)?.Name ?? id, !windows, windows ? "Usually part of Windows or of another package." : null);
            })
            .OrderBy(item => !item.Selected).ThenBy(item => item.Name, StringComparer.CurrentCultureIgnoreCase)
            .ToList();

    private static List<DraftItem> RemoveAppx(VmAppxInventory packages) =>
        packages.RemovedFromBaseline
            .Select(package => new DraftItem(
                package.Name,
                package.FriendlyName,
                package.Rating != AppxRating.Keep,
                package.Rating == AppxRating.Keep ? "HyperHarbor's catalog suggests keeping this one." : package.Note))
            .ToList();

    private List<DraftItem> Tweaks(GuestBrowsersAndSettings found) =>
        catalogs.Tweaks
            .Where(tweak => tweak.Values.All(value =>
                found.Values.TryGetValue($"{value.Key}|{value.Name}", out var current) && string.Equals(current, value.Value, StringComparison.OrdinalIgnoreCase)))
            .Select(tweak => new DraftItem(tweak.Id, tweak.Name, true, null))
            .ToList();

    private List<DraftBrowser> Browsers(GuestBrowsersAndSettings found)
    {
        var browsers = found.Extensions
            .GroupBy(extension => extension.Browser, StringComparer.OrdinalIgnoreCase)
            .Select(group => (Browser: catalogs.Browsers.FirstOrDefault(browser => browser.Id == group.Key), Extensions: group.ToList()))
            .Where(item => item.Browser is not null)
            .OrderByDescending(item => item.Extensions.Count(extension => !catalogs.BuiltInExtensions.Contains(extension.ProfileId)))
            .ToList();
        return browsers
            .Select((item, index) => new DraftBrowser(
                item.Browser!.WingetId,
                item.Browser.Name,
                index == 0,
                item.Extensions
                    .Select(extension => catalogs.BuiltInExtensions.Contains(extension.ProfileId)
                        ? new DraftItem(extension.ProfileId, extension.Name, false, "The browser installs this one by itself.")
                        : new DraftItem(extension.ProfileId, catalogs.ExtensionIndex.GetValueOrDefault(extension.ProfileId)?.Name ?? extension.Name, true, null))
                    .OrderBy(extension => !extension.Selected).ThenBy(extension => extension.Name, StringComparer.CurrentCultureIgnoreCase)
                    .ToList()))
            .ToList();
    }
}
