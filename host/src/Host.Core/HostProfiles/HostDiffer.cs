using System.Globalization;
using HyperHarbor.Host.Core.Profiles;
using HyperHarbor.Shared.Contracts.Profiles;

namespace HyperHarbor.Host.Core.HostProfiles;

/// <summary>
/// Compares what a host profile asks for with what the host looks like (a <see cref="HostSnapshot"/>) and returns
/// only the differences, after the guards and the allowlists have kept what must stay. Pure: it reads nothing and
/// changes nothing, so a dry run and an apply see the same thing, and a host that already matches gives an empty diff.
/// Guards: Gaming Services and the Xbox Identity Provider stay while a game from the curated list is in a Steam
/// library; the print spooler stays while a physical printer exists; Photos stays while no other image viewer is
/// installed. Services that hold Hyper-V, Remote Desktop, and Windows together are never changed, and nothing the
/// startup allowlist names is ever disabled.
/// </summary>
public sealed class HostDiffer(Catalogs catalogs, HostCatalogs hostCatalogs)
{
    /// <summary>Wake devices that count as keyboards and mice for the nicAndInput rule.</summary>
    public static readonly string[] InputDevicePatterns = ["*keyboard*", "*mouse*", "*trackpad*", "*touchpad*"];

    public static readonly IReadOnlyDictionary<string, (string Guid, string Name)> KnownPlans = new Dictionary<string, (string, string)>(StringComparer.Ordinal)
    {
        ["highPerformance"] = ("8c5e7fda-e8bf-4a96-9a85-a6e23a8c635c", "High performance"),
        ["ultimate"] = ("e9a42b02-d5df-448d-aa00-03f14749eb61", "Ultimate Performance"),
        ["balanced"] = ("381b4222-f694-41f0-9685-ff5bb260df2e", "Balanced"),
        ["powerSaver"] = ("a1841308-3541-4fab-bc81-f71556f20b4a", "Power saver"),
    };

    /// <summary>The registry values the plan needs to compare, to give to <see cref="IHostSystem.InspectAsync"/>.</summary>
    public static IReadOnlyList<RegistryProbe> Probes(ApplyPlan plan) =>
        plan.Registry.Select(write => new RegistryProbe(write.Target, write.Key, write.Name)).DistinctBy(probe => probe.Id).ToList();

    public HostDiff Diff(ApplyPlan plan, HostSnapshot snapshot, IReadOnlyList<SteamGame> games)
    {
        var guards = hostCatalogs.Guards;
        var kept = new List<string>();
        var problems = new List<string>(plan.Problems);
        problems.AddRange(snapshot.Warnings);
        var alreadyInPlace = 0;

        // The guards.
        var keepAppx = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var keepServices = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var curated = guards.Games.ToDictionary(game => game.AppId);
        var found = games.Where(game => curated.ContainsKey(game.AppId)).Select(game => curated[game.AppId].Name).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToList();
        if (found.Count > 0)
        {
            var reason = $"{string.Join(", ", found)} found in a Steam library";
            foreach (var name in guards.GamingServices.KeepAppx) { keepAppx[name] = reason; }
            foreach (var name in guards.GamingServices.KeepServices) { keepServices[name] = reason; }
        }

        if (snapshot.Printers.Any(printer => IsPhysical(printer, guards)))
        {
            foreach (var name in guards.Spooler.KeepServices) { keepServices[name] = "a physical printer exists"; }
        }

        if (!snapshot.Programs.Any(program => Wildcard.IsMatchAny(guards.ImageViewers, program.Name)))
        {
            foreach (var name in guards.Photos.KeepAppx) { keepAppx[name] = "no other image viewer is installed"; }
        }

        var services = Services(plan, snapshot, guards, keepServices, kept, ref alreadyInPlace);
        var startup = Startup(plan, snapshot, kept, ref alreadyInPlace);
        var registry = Registry(plan, snapshot, ref alreadyInPlace);
        var power = Power(plan, snapshot, problems, ref alreadyInPlace);
        var appx = Appx(plan, snapshot, keepAppx, kept, ref alreadyInPlace);
        var programs = Programs(plan, snapshot, problems, ref alreadyInPlace);
        return new HostDiff(services, startup, registry, power, appx, programs, kept, problems, alreadyInPlace);
    }

    private static bool IsPhysical(PrinterInfo printer, HostGuardsCatalog guards) =>
        !Wildcard.IsMatchAny(guards.VirtualPrinterNames, printer.Name) && !Wildcard.IsMatchAny(guards.VirtualPrinterPorts, printer.Port);

    private static List<ServiceChange> Services(
        ApplyPlan plan,
        HostSnapshot snapshot,
        HostGuardsCatalog guards,
        Dictionary<string, string> keepServices,
        List<string> kept,
        ref int alreadyInPlace)
    {
        var changes = new List<ServiceChange>();
        var byName = snapshot.Services.ToDictionary(service => service.Name, StringComparer.OrdinalIgnoreCase);
        foreach (var wanted in plan.Services ?? [])
        {
            if (!byName.TryGetValue(wanted.Id, out var actual))
            {
                continue; // Not on this edition of Windows.
            }

            // Only the startup type decides whether it is in place: a disabled service that cannot be stopped now stops at the next restart.
            if (string.Equals(actual.Startup, wanted.Startup, StringComparison.Ordinal))
            {
                alreadyInPlace++;
                continue;
            }

            var stopNow = wanted.Startup == "disabled" && actual.Running;

            if (guards.ProtectedServices.FirstOrDefault(entry => Wildcard.IsMatch(entry.Pattern, actual.Name)) is { } protectedService)
            {
                kept.Add($"Service {actual.Name}: kept ({protectedService.Reason}).");
            }
            else if (keepServices.TryGetValue(actual.Name, out var reason))
            {
                kept.Add($"Service {actual.Name}: kept, {reason}.");
            }
            else if (actual.Startup is "boot" or "system")
            {
                kept.Add($"Service {actual.Name}: kept (a driver service that starts with Windows).");
            }
            else
            {
                changes.Add(new ServiceChange(actual.Name, actual.DisplayName, actual.Startup, wanted.Startup, stopNow));
            }
        }

        return changes;
    }

    private List<StartupChange> Startup(ApplyPlan plan, HostSnapshot snapshot, List<string> kept, ref int alreadyInPlace)
    {
        var changes = new List<StartupChange>();
        if (plan.Startup is not { } startup)
        {
            return changes;
        }

        var disable = (startup.Disable ?? []).Select(item => item.Id).ToList();
        var enable = (startup.Enable ?? []).Select(item => item.Id).ToList();
        var keep = (startup.Keep ?? []).Select(item => item.Id).ToList();
        var noted = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in snapshot.Startup)
        {
            if (Wildcard.IsMatchAny(enable, entry.Name))
            {
                if (entry.Enabled)
                {
                    alreadyInPlace++;
                }
                else
                {
                    changes.Add(new StartupChange(entry, Enable: true));
                }

                continue;
            }

            if (!Wildcard.IsMatchAny(disable, entry.Name))
            {
                continue;
            }

            if (!entry.Enabled)
            {
                alreadyInPlace++;
                continue;
            }

            var allowed = hostCatalogs.StartupAllowlist.FirstOrDefault(allow => Wildcard.IsMatch(allow.Pattern, entry.Name));
            if (allowed is not null || Wildcard.IsMatchAny(keep, entry.Name))
            {
                if (noted.Add(entry.Name))
                {
                    kept.Add($"Startup entry {entry.Name}: kept ({(allowed is null ? "on this profile's keep list" : allowed.Reason.TrimEnd('.'))}).");
                }

                continue;
            }

            changes.Add(new StartupChange(entry, Enable: false));
        }

        return changes;
    }

    private static List<RegistryChange> Registry(ApplyPlan plan, HostSnapshot snapshot, ref int alreadyInPlace)
    {
        var changes = new List<RegistryChange>();
        foreach (var write in plan.Registry)
        {
            var probe = new RegistryProbe(write.Target, write.Key, write.Name);
            snapshot.Registry.TryGetValue(probe.Id, out var hives);
            hives ??= [];
            if (hives.Count > 0 && hives.All(hive => Matches(write, hive)))
            {
                alreadyInPlace++;
            }
            else
            {
                // The value shown is the first hive that differs, so "1 to 1" never appears when only the Default user differs.
                var before = hives.FirstOrDefault(hive => !Matches(write, hive)) ?? (hives.Count > 0 ? hives[0] : null);
                changes.Add(new RegistryChange(write, before?.Type, before?.Value));
            }
        }

        return changes;
    }

    internal static bool Matches(RegistryWrite write, RegistryValueState hive) => write.Type switch
    {
        "absent" => hive.Type is null,
        "string" => hive.Type == "string" && string.Equals(hive.Value, write.Value, StringComparison.Ordinal),
        "dword" or "qword" => hive.Type == write.Type && string.Equals(NormalizeNumber(hive.Value), NormalizeNumber(write.Value), StringComparison.Ordinal),
        _ => false,
    };

    private static string? NormalizeNumber(string? text)
    {
        if (text is null)
        {
            return null;
        }

        if (text.StartsWith("0x", StringComparison.OrdinalIgnoreCase) && ulong.TryParse(text.AsSpan(2), NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out var hex))
        {
            return hex.ToString(CultureInfo.InvariantCulture);
        }

        return ulong.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out var number) ? number.ToString(CultureInfo.InvariantCulture) : text;
    }

    private static PowerChange? Power(ApplyPlan plan, HostSnapshot snapshot, List<string> problems, ref int alreadyInPlace)
    {
        if (plan.Power is not { } power)
        {
            return null;
        }

        var state = snapshot.Power;
        string? planFrom = null, planFromName = null, planTo = null, planToName = null;
        var duplicate = false;
        if (power.Plan is { } wanted)
        {
            string target;
            string targetName;
            if (KnownPlans.TryGetValue(wanted, out var known))
            {
                (target, targetName) = known;
            }
            else
            {
                target = wanted.ToLowerInvariant();
                targetName = state.Plans.FirstOrDefault(p => p.Guid == target)?.Name ?? target;
            }

            if (string.Equals(state.ActivePlan, target, StringComparison.OrdinalIgnoreCase))
            {
                alreadyInPlace++;
            }
            else if (state.Plans.All(p => !string.Equals(p.Guid, target, StringComparison.OrdinalIgnoreCase)) && !KnownPlans.ContainsKey(wanted))
            {
                problems.Add($"The power plan {wanted} does not exist on this PC.");
            }
            else
            {
                planFrom = state.ActivePlan;
                planFromName = state.Plans.FirstOrDefault(p => string.Equals(p.Guid, state.ActivePlan, StringComparison.OrdinalIgnoreCase))?.Name;
                planTo = target;
                planToName = state.Plans.FirstOrDefault(p => string.Equals(p.Guid, target, StringComparison.OrdinalIgnoreCase))?.Name ?? targetName;
                duplicate = state.Plans.All(p => !string.Equals(p.Guid, target, StringComparison.OrdinalIgnoreCase));
            }
        }

        var armed = new HashSet<string>(state.ArmedDevices, StringComparer.OrdinalIgnoreCase);
        var wantArmed = new List<string>();
        // nicOnly: only wired network adapters may wake the PC. nicAndInput: also the keyboards and mice that may now.
        var limitWake = power.Wake is "nicOnly" or "nicAndInput";
        if (limitWake)
        {
            if (state.NetworkDevices.Count == 0)
            {
                problems.Add("Wake devices were left as they are: no wired network adapter that can wake the PC was found, and disarming everything would break Wake-on-LAN.");
                limitWake = false;
            }
            else
            {
                wantArmed.AddRange(state.NetworkDevices);
                if (power.Wake == "nicAndInput")
                {
                    wantArmed.AddRange(state.ArmedDevices.Where(device => Wildcard.IsMatchAny(InputDevicePatterns, device)));
                }
            }
        }

        wantArmed.AddRange((power.ArmWake ?? []).Select(item => item.Id));
        var wantSet = new HashSet<string>(wantArmed, StringComparer.OrdinalIgnoreCase);
        var arm = wantArmed.Where(device => !armed.Contains(device)).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        var disarm = limitWake ? state.ArmedDevices.Where(device => !wantSet.Contains(device)).Distinct(StringComparer.OrdinalIgnoreCase).ToList() : [];
        if (power.Wake is not null && arm.Count == 0 && disarm.Count == 0)
        {
            alreadyInPlace++;
        }

        return planTo is null && arm.Count == 0 && disarm.Count == 0
            ? null
            : new PowerChange(planFrom, planFromName, planTo, planToName, duplicate, disarm, arm);
    }

    private List<AppxChange> Appx(ApplyPlan plan, HostSnapshot snapshot, Dictionary<string, string> keepAppx, List<string> kept, ref int alreadyInPlace)
    {
        var changes = new List<AppxChange>();
        foreach (var item in plan.Appx)
        {
            if (!snapshot.Appx.Contains(item.Id))
            {
                alreadyInPlace++;
                continue;
            }

            var friendly = item.Name ?? catalogs.AppxIndex.GetValueOrDefault(item.Id)?.FriendlyName ?? "";
            if (catalogs.AppxIndex.TryGetValue(item.Id, out var rated) && rated.Rating == AppxRating.Keep)
            {
                kept.Add($"App {(friendly.Length > 0 ? friendly : item.Id)}: kept (a framework or security piece other apps need).");
            }
            else if (keepAppx.TryGetValue(item.Id, out var reason))
            {
                kept.Add($"App {(friendly.Length > 0 ? friendly : item.Id)}: kept, {reason}.");
            }
            else
            {
                changes.Add(new AppxChange(item.Id, friendly));
            }
        }

        return changes;
    }

    private List<ProgramChange> Programs(ApplyPlan plan, HostSnapshot snapshot, List<string> problems, ref int alreadyInPlace)
    {
        var changes = new List<ProgramChange>();
        foreach (var item in plan.Programs ?? [])
        {
            if (!hostCatalogs.ProgramIndex.TryGetValue(item.Id, out var entry))
            {
                problems.Add($"{item.Id} is not a program HyperHarbor knows how to uninstall.");
                continue;
            }

            var installed = snapshot.Programs
                .Where(program => Wildcard.IsMatchAny(entry.Match, program.Name))
                .DistinctBy(program => (program.Scope, program.Key))
                .ToList();
            if (installed.Count == 0)
            {
                alreadyInPlace++;
                continue;
            }

            changes.AddRange(installed.Select(program => new ProgramChange(entry, program)));
        }

        return changes;
    }
}
