using System.Globalization;
using System.Text.RegularExpressions;
using HyperHarbor.Host.Core.HostProfiles;
using HyperHarbor.Host.Core.Lifecycle;
using HyperHarbor.Shared.Contracts;
using HyperHarbor.Shared.Contracts.Profiles;

namespace HyperHarbor.Host.Core.Profiles;

/// <summary>
/// Checks a setup profile against the rules in schemas/profile.v1.schema.json and the catalogs, and returns it
/// tidied: values trimmed, duplicates dropped, and missing friendly names filled from the catalogs.
/// </summary>
public sealed partial class ProfileValidator(Catalogs catalogs, HostCatalogs? hostCatalogs = null)
{
    public const int MaxServices = 200;
    public const int MaxStartupPatterns = 200;
    public static readonly string[] ServiceStartupTypes = ["disabled", "manual", "automatic", "automaticDelayed"];
    public static readonly string[] PowerPlans = ["highPerformance", "ultimate", "balanced", "powerSaver"];
    public static readonly string[] WakeRules = ["nicOnly", "nicAndInput", "unchanged"];

    private readonly HostCatalogs _hostCatalogs = hostCatalogs ?? HostCatalogs.Default;

    [GeneratedRegex(@"^[A-Za-z0-9][A-Za-z0-9_.-]{0,255}$")]
    private static partial Regex ServiceName();

    [GeneratedRegex(@"^[^\\/\r\n]{1,200}$")]
    private static partial Regex StartupPattern();

    [GeneratedRegex(@"^[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}$")]
    private static partial Regex PlanGuid();

    public const int MaxName = 60;
    public const int MaxDescription = 500;
    public const int MaxInstall = 200;
    public const int MaxRemove = 200;
    public const int MaxCapabilities = 100;
    public const int MaxTweaks = 100;
    public const int MaxExtensions = 100;

    [GeneratedRegex(@"^[A-Za-z0-9][A-Za-z0-9._-]{0,127}$")]
    private static partial Regex AppxOrFeature();

    [GeneratedRegex(@"^[A-Za-z0-9][A-Za-z0-9._~-]{0,199}$")]
    private static partial Regex Capability();

    [GeneratedRegex(@"^(HKLM|HKCU)\\[^\r\n]{1,255}$")]
    private static partial Regex RegistryKey();

    private readonly PackageAliasResolver _packages = new(catalogs);

    /// <exception cref="LifecycleValidationException">One or more problems, each with the field it is about.</exception>
    public SetupProfile Normalize(SetupProfile profile)
    {
        var issues = new List<ValidationIssue>();
        var name = profile.Name?.Trim() ?? "";
        if (name.Length is 0 or > MaxName)
        {
            issues.Add(new("name", $"Give the profile a name of 1 to {MaxName} characters."));
        }

        var description = string.IsNullOrWhiteSpace(profile.Description) ? null : profile.Description.Trim();
        if (description?.Length > MaxDescription)
        {
            issues.Add(new("description", $"Keep the description under {MaxDescription} characters."));
        }

        var isHost = profile.Target == ProfileTarget.Host;
        if (!isHost)
        {
            if (profile.Services is not null)
            {
                issues.Add(new("services", "Services belong to host profiles. Set target: host."));
            }

            if (profile.Startup is not null)
            {
                issues.Add(new("startup", "Startup entries belong to host profiles. Set target: host."));
            }

            if (profile.Power is not null)
            {
                issues.Add(new("power", "Power settings belong to host profiles. Set target: host."));
            }

            if (profile.Remove?.Programs is not null)
            {
                issues.Add(new("remove.programs", "Uninstalling programs belongs to host profiles. Set target: host."));
            }
        }
        else if (profile.Install is { Count: > 0 })
        {
            issues.Add(new("install", "Host profiles do not install packages yet."));
        }

        var install = Items(profile.Install, "install", MaxInstall, issues, entry =>
            PackageAliasResolver.Classify(entry) switch
            {
                InstallEntryKind.Invalid => "is not a winget id (Publisher.Name), a Microsoft Store id, or a package alias.",
                InstallEntryKind.Alias when !catalogs.AliasIndex.ContainsKey(entry) => "is not a known package alias. Use the winget id instead.",
                _ => null,
            }, _packages.NameOf);

        ProfileRemove? remove = null;
        if (profile.Remove is { } removing)
        {
            remove = new ProfileRemove(
                Items(removing.Appx, "remove.appx", MaxRemove, issues, entry => AppxOrFeature().IsMatch(entry) ? null : "is not a package name.", entry => catalogs.AppxIndex.GetValueOrDefault(entry)?.FriendlyName),
                Items(removing.Capabilities, "remove.capabilities", MaxCapabilities, issues, entry => Capability().IsMatch(entry) ? null : "is not a capability name.", _ => null),
                Items(removing.Features, "remove.features", MaxCapabilities, issues, entry => AppxOrFeature().IsMatch(entry) ? null : "is not an optional feature name.", _ => null),
                isHost
                    ? Items(removing.Programs, "remove.programs", MaxRemove, issues, entry => _hostCatalogs.ProgramIndex.ContainsKey(entry) ? null : "is not a program HyperHarbor knows how to uninstall.", entry => _hostCatalogs.ProgramIndex.GetValueOrDefault(entry)?.Name)
                    : null);
        }

        var tweaks = Tweaks(profile.Tweaks, issues);
        var browser = Browser(profile.Browser, issues);
        var services = isHost ? Services(profile.Services, issues) : null;
        var startup = isHost ? Startup(profile.Startup, issues) : null;
        var power = isHost ? Power(profile.Power, issues) : null;

        if (issues.Count > 0)
        {
            throw new LifecycleValidationException("The profile has problems. Fix the fields listed.", issues);
        }

        return new SetupProfile(name, description, install, remove, tweaks, browser, isHost ? ProfileTarget.Host : null, services, startup, power);
    }

    private List<ProfileService>? Services(IReadOnlyList<ProfileService>? services, List<ValidationIssue> issues)
    {
        if (services is null)
        {
            return null;
        }

        if (services.Count > MaxServices)
        {
            issues.Add(new("services", $"Keep the list to {MaxServices} services."));
        }

        var result = new List<ProfileService>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        for (var i = 0; i < services.Count; i++)
        {
            var id = services[i].Id?.Trim() ?? "";
            var startup = services[i].Startup?.Trim() ?? "";
            if (!ServiceName().IsMatch(id))
            {
                issues.Add(new($"services[{i}]", $"\"{id}\" is not a service name."));
                continue;
            }

            if (!ServiceStartupTypes.Contains(startup, StringComparer.Ordinal))
            {
                issues.Add(new($"services[{i}]", $"The startup type of {id} must be one of {string.Join(", ", ServiceStartupTypes)}."));
                continue;
            }

            if (seen.Add(id))
            {
                result.Add(new ProfileService(id, startup, Clean(services[i].Name)));
            }
        }

        return result;
    }

    private ProfileStartup? Startup(ProfileStartup? startup, List<ValidationIssue> issues)
    {
        if (startup is null)
        {
            return null;
        }

        List<ProfileItem>? Patterns(IReadOnlyList<ProfileItem>? items, string field) =>
            Items(items, field, MaxStartupPatterns, issues, entry => StartupPattern().IsMatch(entry) ? null : "is not an entry name (no slashes; * and ? are wildcards).", _ => null);

        var disable = Patterns(startup.Disable, "startup.disable");
        var enable = Patterns(startup.Enable, "startup.enable");
        var keep = Patterns(startup.Keep, "startup.keep");
        foreach (var overlap in (disable ?? []).Select(item => item.Id).Intersect((enable ?? []).Select(item => item.Id), StringComparer.OrdinalIgnoreCase))
        {
            issues.Add(new("startup", $"\"{overlap}\" is in both disable and enable."));
        }

        return new ProfileStartup(disable, enable, keep);
    }

    private ProfilePower? Power(ProfilePower? power, List<ValidationIssue> issues)
    {
        if (power is null)
        {
            return null;
        }

        var plan = string.IsNullOrWhiteSpace(power.Plan) ? null : power.Plan.Trim();
        if (plan is not null && !PowerPlans.Contains(plan, StringComparer.Ordinal) && !PlanGuid().IsMatch(plan))
        {
            issues.Add(new("power.plan", $"Use {string.Join(", ", PowerPlans)}, or a plan GUID."));
        }

        var wake = string.IsNullOrWhiteSpace(power.Wake) ? null : power.Wake.Trim();
        if (wake is not null && !WakeRules.Contains(wake, StringComparer.Ordinal))
        {
            issues.Add(new("power.wake", $"Use {string.Join(" or ", WakeRules)}."));
        }

        var armWake = Items(power.ArmWake, "power.armWake", 50, issues, entry => entry.Length is > 0 and <= 200 ? null : "is not a device name.", _ => null);
        return new ProfilePower(plan, wake, armWake);
    }

    private List<ProfileItem>? Items(
        IReadOnlyList<ProfileItem>? items,
        string field,
        int max,
        List<ValidationIssue> issues,
        Func<string, string?> problem,
        Func<string, string?> catalogName)
    {
        if (items is null)
        {
            return null;
        }

        if (items.Count > max)
        {
            issues.Add(new(field, $"Keep the list to {max} entries."));
        }

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var result = new List<ProfileItem>();
        for (var i = 0; i < items.Count; i++)
        {
            var id = items[i].Id?.Trim() ?? "";
            if (problem(id) is { } message)
            {
                issues.Add(new($"{field}[{i}]", $"\"{id}\" {message}"));
                continue;
            }

            if (seen.Add(id))
            {
                result.Add(new ProfileItem(id, Clean(items[i].Name) ?? catalogName(id)));
            }
        }

        return result;
    }

    private List<ProfileTweak>? Tweaks(IReadOnlyList<ProfileTweak>? tweaks, List<ValidationIssue> issues)
    {
        if (tweaks is null)
        {
            return null;
        }

        if (tweaks.Count > MaxTweaks)
        {
            issues.Add(new("tweaks", $"Keep the list to {MaxTweaks} tweaks."));
        }

        var result = new List<ProfileTweak>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        for (var i = 0; i < tweaks.Count; i++)
        {
            var field = $"tweaks[{i}]";
            var tweak = tweaks[i];
            if ((tweak.Id is null) == (tweak.Registry is null))
            {
                issues.Add(new(field, "A tweak is either a curated tweak id or a registry value."));
                continue;
            }

            if (tweak.Id is { } id)
            {
                if (!catalogs.TweakIndex.TryGetValue(id.Trim(), out var known))
                {
                    issues.Add(new(field, $"\"{id}\" is not a known tweak. Use a registry value for anything else."));
                }
                else if (seen.Add(known.Id))
                {
                    result.Add(new ProfileTweak(known.Id, Clean(tweak.Name) ?? known.Name));
                }

                continue;
            }

            var registry = tweak.Registry!;
            var key = registry.Key.Trim();
            if (!RegistryKey().IsMatch(key))
            {
                issues.Add(new($"{field}.registry.key", "Start the key with HKLM\\ or HKCU\\."));
            }

            if (registry.Name.Length > 255)
            {
                issues.Add(new($"{field}.registry.name", "Keep the value name under 256 characters."));
            }

            var valid = registry.Type switch
            {
                RegistryValueType.Absent => registry.Value.Length == 0,
                RegistryValueType.Dword => uint.TryParse(registry.Value, NumberStyles.None, CultureInfo.InvariantCulture, out _),
                RegistryValueType.Qword => ulong.TryParse(registry.Value, NumberStyles.None, CultureInfo.InvariantCulture, out _),
                _ => registry.Value.Length <= 4096 && !registry.Value.Contains('\0', StringComparison.Ordinal),
            };
            if (!valid)
            {
                issues.Add(new($"{field}.registry.value", registry.Type == RegistryValueType.Absent
                    ? "Leave the value empty: the value is deleted."
                    : registry.Type == RegistryValueType.String
                    ? "Keep the text under 4096 characters."
                    : $"Use a whole number from 0 to {(registry.Type == RegistryValueType.Dword ? "4294967295" : "18446744073709551615")}."));
            }

            result.Add(new ProfileTweak(Name: Clean(tweak.Name), Registry: registry with { Key = key }));
        }

        return result;
    }

    private ProfileBrowser? Browser(ProfileBrowser? browser, List<ValidationIssue> issues)
    {
        if (browser is null)
        {
            return null;
        }

        var appId = browser.App?.Id?.Trim() ?? "";
        var known = catalogs.FindBrowser(appId);
        if (known is null)
        {
            issues.Add(new("browser.app", $"\"{appId}\" is not a browser HyperHarbor can configure. Use {string.Join(", ", catalogs.Browsers.Select(item => item.WingetId))}."));
        }

        var extensions = Items(browser.Extensions, "browser.extensions", MaxExtensions, issues,
            entry => ExtensionIdParser.Parse(entry) is { } parsed && parsed.ProfileId == entry ? null : "is not an extension id (32 letters a to p, or edge: and the id).",
            entry => catalogs.ExtensionIndex.GetValueOrDefault(entry)?.Name);

        Dictionary<string, string>? policies = null;
        if (browser.Policies is not null)
        {
            policies = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var (key, value) in browser.Policies)
            {
                var field = $"browser.policies.{key}";
                if (!catalogs.PolicyIndex.TryGetValue(key, out var policy))
                {
                    issues.Add(new(field, $"\"{key}\" is not a known browser policy."));
                    continue;
                }

                if (known is not null && !policy.AppliesTo.Contains(known.Id, StringComparer.Ordinal))
                {
                    issues.Add(new(field, $"{policy.Name} does not apply to {known.Name}."));
                    continue;
                }

                var text = value?.Trim() ?? "";
                var normalized = policy.Type switch
                {
                    "boolean" => bool.TryParse(text, out var flag) ? (flag ? "true" : "false") : null,
                    "integer" => long.TryParse(text, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var number)
                        && (policy.Values is null || policy.Values.ContainsKey(number.ToString(CultureInfo.InvariantCulture)))
                        ? number.ToString(CultureInfo.InvariantCulture)
                        : null,
                    _ => text.Length is > 0 and <= 2048 ? text : null,
                };
                if (normalized is null)
                {
                    issues.Add(new(field, policy.Type switch
                    {
                        "boolean" => "Use true or false.",
                        "integer" when policy.Values is { } values => $"Use one of {string.Join(", ", values.Keys)}.",
                        "integer" => "Use a whole number.",
                        _ => "Use text of 1 to 2048 characters.",
                    }));
                    continue;
                }

                policies[key] = normalized;
            }
        }

        return new ProfileBrowser(new ProfileItem(appId, Clean(browser.App?.Name) ?? known?.Name), extensions, policies);
    }

    /// <summary>The policy types for <see cref="ProfileYamlWriter"/>.</summary>
    public IReadOnlyDictionary<string, string> PolicyTypes => catalogs.Policies.ToDictionary(policy => policy.Key, policy => policy.Type, StringComparer.Ordinal);

    private static string? Clean(string? name)
    {
        var text = name is null ? null : string.Join(' ', name.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries)).Trim();
        return string.IsNullOrEmpty(text) ? null : text.Length > 120 ? text[..120] : text;
    }
}
