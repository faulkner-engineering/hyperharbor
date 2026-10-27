using System.Text.Json;
using System.Xml.Linq;
using HyperHarbor.Shared.Contracts.Profiles;

namespace HyperHarbor.Host.Core.Profiles;

/// <summary>Where a registry write goes in the guest (or on the host, for host profiles).</summary>
public enum RegistryTarget
{
    /// <summary>HKEY_LOCAL_MACHINE.</summary>
    Machine,

    /// <summary>
    /// The Default user profile's hive (C:\Users\Default\NTUSER.DAT). Profiles created afterwards start from it, so
    /// the User's account, which has not signed in yet on a new VM, gets the value at its first sign-in. On the host
    /// it also means every signed-in user's hive.
    /// </summary>
    DefaultUser,
}

/// <summary>A registry value to write, and the item it belongs to (for the results).</summary>
/// <param name="Key">The path below the hive, for example SOFTWARE\Policies\Microsoft\Edge.</param>
/// <param name="Type">dword, qword, string, or absent (delete the value).</param>
public sealed record RegistryWrite(string Item, RegistryTarget Target, string Key, string Name, string Type, string Value);

/// <summary>A text file to write in the guest (UTF-8), and the item it belongs to. Written before the registry values.</summary>
/// <param name="Path">An absolute path in the guest.</param>
public sealed record FileWrite(string Item, string Path, string Contents);

/// <summary>A package to install, with the name the results use.</summary>
public sealed record PackageInstall(string Item, string Id, PackageSource Source);

/// <summary>What applying a setup profile does, in order, and what could not be planned.</summary>
public sealed record ApplyPlan(
    IReadOnlyList<PackageInstall> Packages,
    IReadOnlyList<ProfileItem> Appx,
    IReadOnlyList<ProfileItem> Capabilities,
    IReadOnlyList<ProfileItem> Features,
    IReadOnlyList<RegistryWrite> Registry,
    IReadOnlyList<string> Problems,
    IReadOnlyList<ProfileService>? Services = null,
    ProfileStartup? Startup = null,
    ProfilePower? Power = null,
    IReadOnlyList<ProfileItem>? Programs = null,
    IReadOnlyList<FileWrite>? Files = null)
{
    public int ItemCount =>
        Packages.Count + Appx.Count + Capabilities.Count + Features.Count + Registry.Select(write => write.Item).Distinct().Count()
        + (Services?.Count ?? 0) + (Programs?.Count ?? 0);
}

/// <summary>
/// Turns a setup profile into an <see cref="ApplyPlan"/>: aliases resolved to winget ids, the browser added to the
/// installs, curated tweaks expanded to their registry values (HKCU values go to the Default user hive), and browser
/// policies and extensions written as the browser's policy values. Extensions use ExtensionSettings with
/// installation_mode normal_installed, so they are installed and can be turned off but not removed.
/// </summary>
public sealed class SetupProfilePlanner(Catalogs catalogs)
{
    private readonly PackageAliasResolver _packages = new(catalogs);

    public ApplyPlan Plan(SetupProfile profile)
    {
        var problems = new List<string>();
        var packages = new List<PackageInstall>();
        foreach (var item in profile.Install ?? [])
        {
            AddPackage(packages, problems, item);
        }

        var isHost = profile.Target == ProfileTarget.Host;
        var browser = profile.Browser is { } chosen ? catalogs.FindBrowser(chosen.App.Id) : null;
        if (profile.Browser is { } wanted)
        {
            if (browser is null)
            {
                problems.Add($"{wanted.App.Name ?? wanted.App.Id} is not a browser HyperHarbor can configure.");
            }
            else if (!isHost && !packages.Any(package => string.Equals(package.Id, browser.WingetId, StringComparison.OrdinalIgnoreCase)))
            {
                packages.Add(new PackageInstall(browser.Name, browser.WingetId, PackageSource.Winget));
            }
        }

        var registry = new List<RegistryWrite>();
        var files = new List<FileWrite>();
        foreach (var tweak in profile.Tweaks ?? [])
        {
            if (tweak.Id is { } id)
            {
                if (catalogs.TweakIndex.TryGetValue(id, out var known))
                {
                    registry.AddRange(known.Values.Select(value => Write(known.Name, value.Key, value.Name, value.Type, value.Value)));
                }
                else
                {
                    problems.Add($"The tweak {id} is not in this host's catalog.");
                }
            }
            else if (tweak.Registry is { } custom)
            {
                registry.Add(Write(tweak.Name ?? $@"{custom.Key}\{custom.Name}", custom.Key, custom.Name, custom.Type.ToString().ToLowerInvariant(), custom.Type == RegistryValueType.Absent ? "" : custom.Value));
            }
        }

        if (browser is not null && profile.Browser is { } settings)
        {
            registry.AddRange(Policies(browser, settings, problems));
            if (Extensions(browser, settings, problems) is { } extensions)
            {
                registry.Add(extensions);
            }

            if (settings.MakeDefault && !isHost)
            {
                var item = $"{browser.Name} as the default browser";
                files.Add(new FileWrite(item, DefaultAssociationsPath, DefaultAssociations(browser)));
                registry.Add(new RegistryWrite(item, RegistryTarget.Machine, DefaultAssociationsPolicyKey, "DefaultAssociationsConfiguration", "string", DefaultAssociationsPath));
            }
        }

        return new ApplyPlan(
            packages,
            profile.Remove?.Appx ?? [],
            profile.Remove?.Capabilities ?? [],
            profile.Remove?.Features ?? [],
            registry,
            problems,
            profile.Services,
            profile.Startup,
            profile.Power,
            profile.Remove?.Programs,
            files);
    }

    /// <summary>
    /// Where the default associations file goes in the guest. The policy names a path, and Windows reads the file at
    /// every sign-in, so it must stay there. HyperHarbor's VMs install Windows on C:.
    /// </summary>
    internal const string DefaultAssociationsPath = @"C:\ProgramData\HyperHarbor\DefaultAssociations.xml";

    /// <summary>The policy "Set a default associations configuration file" (HKLM).</summary>
    internal const string DefaultAssociationsPolicyKey = @"SOFTWARE\Policies\Microsoft\Windows\System";

    /// <summary>The web link and HTML file types the default browser takes over.</summary>
    internal static readonly string[] BrowserAssociations = [".htm", ".html", "http", "https"];

    /// <summary>
    /// The default associations file. On Windows 11 the UserChoice protection driver stops anything but Windows from
    /// choosing the default browser, so this policy is the supported way; Windows applies it at every sign-in.
    /// </summary>
    internal static string DefaultAssociations(BrowserCatalogEntry browser)
    {
        var associations = new XElement("DefaultAssociations", BrowserAssociations.Select(identifier => new XElement(
            "Association",
            new XAttribute("Identifier", identifier),
            new XAttribute("ProgId", browser.ProgId),
            new XAttribute("ApplicationName", browser.ApplicationName))));
        return "<?xml version=\"1.0\" encoding=\"UTF-8\"?>\n" + associations + "\n";
    }

    private void AddPackage(List<PackageInstall> packages, List<string> problems, ProfileItem item)
    {
        try
        {
            var resolved = _packages.Resolve(item.Id);
            if (!packages.Any(package => string.Equals(package.Id, resolved.Id, StringComparison.OrdinalIgnoreCase)))
            {
                packages.Add(new PackageInstall(item.Name ?? resolved.Name ?? resolved.Id, resolved.Id, resolved.Source));
            }
        }
        catch (Exception ex) when (ex is UnknownAliasException or ArgumentException)
        {
            problems.Add(ex.Message);
        }
    }

    /// <summary>HKLM\… to the machine, HKCU\… to the Default user hive.</summary>
    private static RegistryWrite Write(string item, string key, string name, string type, string value) =>
        key.StartsWith(@"HKCU\", StringComparison.OrdinalIgnoreCase)
            ? new RegistryWrite(item, RegistryTarget.DefaultUser, key[5..], name, type, value)
            : new RegistryWrite(item, RegistryTarget.Machine, key[5..], name, type, value);

    private IEnumerable<RegistryWrite> Policies(BrowserCatalogEntry browser, ProfileBrowser settings, List<string> problems)
    {
        foreach (var (key, value) in settings.Policies ?? new Dictionary<string, string>())
        {
            if (!catalogs.PolicyIndex.TryGetValue(key, out var policy) || !policy.AppliesTo.Contains(browser.Id, StringComparer.Ordinal))
            {
                problems.Add($"The policy {key} does not apply to {browser.Name}.");
                continue;
            }

            var (type, data) = policy.Type switch
            {
                "boolean" => ("dword", value == "true" ? "1" : "0"),
                "integer" => ("dword", value),
                _ => ("string", value),
            };
            yield return Write($"{browser.Name} policy: {policy.Name}", browser.PolicyRoot, policy.Policy, type, data);
        }
    }

    /// <summary>One ExtensionSettings value with every extension; null without extensions.</summary>
    private RegistryWrite? Extensions(BrowserCatalogEntry browser, ProfileBrowser settings, List<string> problems)
    {
        var entries = new SortedDictionary<string, object>(StringComparer.Ordinal);
        foreach (var item in settings.Extensions ?? [])
        {
            if (ExtensionIdParser.Parse(item.Id) is not { } extension)
            {
                problems.Add($"{item.Name ?? item.Id} is not an extension id.");
                continue;
            }

            var updateUrl = extension.Store == ExtensionStore.Edge ? browser.EdgeUpdateUrl : browser.UpdateUrl;
            if (updateUrl is null)
            {
                problems.Add($"{item.Name ?? item.Id} is from the Edge Add-ons store, so {browser.Name} cannot install it.");
                continue;
            }

            entries[extension.Id] = new Dictionary<string, string> { ["installation_mode"] = "normal_installed", ["update_url"] = updateUrl };
        }

        return entries.Count == 0
            ? null
            : Write($"{browser.Name} extensions ({entries.Count})", browser.PolicyRoot, "ExtensionSettings", "string", JsonSerializer.Serialize(entries));
    }
}
