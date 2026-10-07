using HyperHarbor.Host.Core.Profiles;
using HyperHarbor.Shared.Contracts.Profiles;

namespace HyperHarbor.Host.Core.HostProfiles;

/// <summary>
/// Turns what an apply changed into a profile that puts it back: service startup types, startup entries, registry
/// values (absent ones become type absent), the power plan, and the devices that were disarmed. Removed apps and
/// uninstalled programs cannot be put back this way and are only named in the description. Applying twice keeps the
/// earliest "before" of every value, so the undo profile always returns to how the PC was before the first apply.
/// </summary>
public static class UndoProfileBuilder
{
    public const string Name = "Undo Lean host";

    private static readonly string[] RestorableStartup = ["disabled", "manual", "automatic", "automaticDelayed"];

    public static SetupProfile FromDiff(HostDiff diff)
    {
        var services = diff.Services
            .Where(change => RestorableStartup.Contains(change.From, StringComparer.Ordinal))
            .Select(change => new ProfileService(change.Name, change.From, change.DisplayName.Length > 0 ? change.DisplayName : null))
            .ToList();

        // Entry names the profile format cannot hold (slashes, very long names) are left out; they stay disabled after an undo.
        var restorable = diff.Startup.Where(change => IsPlainName(change.Entry.Name)).ToList();
        var enable = restorable.Where(change => !change.Enable).Select(change => new ProfileItem(change.Entry.Name)).DistinctBy(item => item.Id, StringComparer.OrdinalIgnoreCase).ToList();
        var disable = restorable.Where(change => change.Enable).Select(change => new ProfileItem(change.Entry.Name)).DistinctBy(item => item.Id, StringComparer.OrdinalIgnoreCase).ToList();

        var tweaks = new List<ProfileTweak>();
        foreach (var change in diff.Registry)
        {
            var write = change.Write;
            var key = (write.Target == RegistryTarget.Machine ? @"HKLM\" : @"HKCU\") + write.Key;
            if (change.BeforeType is null)
            {
                tweaks.Add(new ProfileTweak(Name: write.Item, Registry: new RegistryTweak(key, write.Name, RegistryValueType.Absent, "")));
            }
            else if (change.BeforeType is "dword" or "qword" or "string" && change.BeforeValue is not null)
            {
                var type = change.BeforeType switch
                {
                    "dword" => RegistryValueType.Dword,
                    "qword" => RegistryValueType.Qword,
                    _ => RegistryValueType.String,
                };
                tweaks.Add(new ProfileTweak(Name: write.Item, Registry: new RegistryTweak(key, write.Name, type, change.BeforeValue)));
            }
        }

        ProfilePower? power = null;
        var disarmed = diff.Power?.Disarm ?? [];
        if (diff.Power is { } changedPower && (changedPower.PlanFrom is not null || disarmed.Count > 0))
        {
            power = new ProfilePower(
                changedPower.PlanTo is null ? null : changedPower.PlanFrom,
                disarmed.Count > 0 ? "unchanged" : null,
                disarmed.Count > 0 ? disarmed.Select(device => new ProfileItem(device)).ToList() : null);
        }

        return new SetupProfile(
            Name,
            "Puts back what the Lean host action changed: service startup types, startup entries, registry values, the power plan, and wake devices. Removed apps and uninstalled programs are not restored.",
            null,
            null,
            tweaks.Count > 0 ? tweaks : null,
            null,
            ProfileTarget.Host,
            services.Count > 0 ? services : null,
            enable.Count > 0 || disable.Count > 0 ? new ProfileStartup(disable.Count > 0 ? disable : null, enable.Count > 0 ? enable : null, null) : null,
            power);
    }

    private static bool IsPlainName(string name) =>
        name.Length is > 0 and <= 200 && name.IndexOfAny(['\\', '/', '\r', '\n']) < 0;

    /// <summary>Adds <paramref name="added"/> to <paramref name="existing"/>; for anything both hold, the existing (earlier) entry stays.</summary>
    public static SetupProfile Merge(SetupProfile? existing, SetupProfile added)
    {
        if (existing is null)
        {
            return added;
        }

        var services = (existing.Services ?? []).Concat(added.Services ?? []).DistinctBy(service => service.Id, StringComparer.OrdinalIgnoreCase).ToList();
        var tweaks = (existing.Tweaks ?? []).Concat(added.Tweaks ?? [])
            .DistinctBy(tweak => tweak.Registry is { } registry ? $"{registry.Key}|{registry.Name}".ToLowerInvariant() : tweak.Id)
            .ToList();
        List<ProfileItem>? Union(IReadOnlyList<ProfileItem>? first, IReadOnlyList<ProfileItem>? second) =>
            (first ?? []).Concat(second ?? []).DistinctBy(item => item.Id, StringComparer.OrdinalIgnoreCase).ToList() is { Count: > 0 } list ? list : null;

        var disable = Union(existing.Startup?.Disable, added.Startup?.Disable);
        var enable = Union(existing.Startup?.Enable, added.Startup?.Enable);

        // A name the first apply disabled and a later one enabled again (or the reverse) needs no undo any more.
        if (disable is not null && enable is not null)
        {
            var both = disable.Select(item => item.Id).Intersect(enable.Select(item => item.Id), StringComparer.OrdinalIgnoreCase).ToHashSet(StringComparer.OrdinalIgnoreCase);
            disable = disable.Where(item => !both.Contains(item.Id)).ToList() is { Count: > 0 } d ? d : null;
            enable = enable.Where(item => !both.Contains(item.Id)).ToList() is { Count: > 0 } e ? e : null;
        }

        var power = existing.Power is null && added.Power is null
            ? null
            : new ProfilePower(existing.Power?.Plan ?? added.Power?.Plan, existing.Power?.Wake ?? added.Power?.Wake, Union(existing.Power?.ArmWake, added.Power?.ArmWake));

        return new SetupProfile(
            Name,
            existing.Description,
            null,
            null,
            tweaks.Count > 0 ? tweaks : null,
            null,
            ProfileTarget.Host,
            services.Count > 0 ? services : null,
            disable is null && enable is null ? null : new ProfileStartup(disable, enable, null),
            power);
    }

    /// <summary>True when the profile would change nothing.</summary>
    public static bool IsEmpty(SetupProfile profile) =>
        profile.Services is not { Count: > 0 }
        && profile.Tweaks is not { Count: > 0 }
        && profile.Startup is not { Disable.Count: > 0 } and not { Enable.Count: > 0 }
        && profile.Power is null;
}
