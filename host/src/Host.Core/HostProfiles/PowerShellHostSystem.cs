using System.Security.Principal;
using System.Text.Json.Nodes;
using HyperHarbor.Host.Core.Profiles;

namespace HyperHarbor.Host.Core.HostProfiles;

/// <summary>
/// <see cref="IHostSystem"/> over Windows PowerShell (see <see cref="HostScripts"/>). Needs administrator rights, which
/// the installed service has as LocalSystem. Each handler is its own script and its own timeout; a script that fails as
/// a whole turns every item it was given into a failure, and the other handlers still run.
/// </summary>
public sealed class PowerShellHostSystem : IHostSystem
{
    private static readonly TimeSpan InspectTimeout = TimeSpan.FromMinutes(3);
    private static readonly TimeSpan ApplyTimeout = TimeSpan.FromMinutes(10);
    private static readonly TimeSpan ProgramsTimeout = TimeSpan.FromMinutes(45);
    private static readonly TimeSpan AppxTimeout = TimeSpan.FromMinutes(20);

    private static readonly string EncodedInspect = HostPowerShell.Encode(HostScripts.Inspect);
    private static readonly string EncodedSystem = HostPowerShell.Encode(HostScripts.System);
    private static readonly string EncodedRegistry = HostPowerShell.Encode(HostScripts.Registry);
    private static readonly string EncodedAppx = HostPowerShell.Encode(HostScripts.Appx);
    private static readonly string EncodedPrograms = HostPowerShell.Encode(HostScripts.Programs);
    private static readonly string EncodedRestorePoint = HostPowerShell.Encode(HostScripts.RestorePoint);
    private static readonly string EncodedExport = HostPowerShell.Encode(HostScripts.ExportRegistry);

    public bool CanModify
    {
        get
        {
            using var identity = WindowsIdentity.GetCurrent();
            return identity.IsSystem || new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
        }
    }

    public async Task<HostSnapshot> InspectAsync(IReadOnlyList<RegistryProbe> probes, CancellationToken cancellationToken)
    {
        var result = await HostPowerShell.RunAsync(EncodedInspect, new
        {
            probes = probes.Select(probe => new
            {
                id = probe.Id,
                target = probe.Target == RegistryTarget.Machine ? "machine" : "users",
                key = probe.Key,
                name = probe.Name,
            }),
        }, InspectTimeout, cancellationToken).ConfigureAwait(false);
        return ParseSnapshot(result);
    }

    public async Task<IReadOnlyList<ApplyItemResult>> ApplyAsync(HostDiff diff, CancellationToken cancellationToken)
    {
        var results = new List<ApplyItemResult>();

        if (diff.Services.Count + diff.Startup.Count > 0 || diff.Power is not null)
        {
            var power = diff.Power;
            var items = new List<string>();
            items.AddRange(diff.Services.Select(change => $"Service {change.Name}"));
            items.AddRange(diff.Startup.Select(change => StartupItem(change)));
            if (power is not null)
            {
                items.Add("Power plan");
                items.AddRange(power.Disarm.Concat(power.Arm).Select(device => $"Wake: {device}"));
            }

            results.AddRange(await RunGroupAsync(EncodedSystem, new
            {
                services = diff.Services.Select(change => new { item = $"Service {change.Name}", name = change.Name, startup = change.To, stop = change.StopNow }),
                startup = diff.Startup.Select(change => new { item = StartupItem(change), scope = change.Entry.Scope, source = change.Entry.Source, name = change.Entry.Name, enable = change.Enable }),
                power = power is null ? null : new
                {
                    plan = power.PlanTo,
                    duplicate = power.Duplicate,
                    disarm = power.Disarm,
                    arm = power.Arm,
                },
            }, ApplyTimeout, items, cancellationToken).ConfigureAwait(false));
        }

        if (diff.Registry.Count > 0)
        {
            var writes = diff.Registry.Select(change => change.Write).ToList();
            var grouped = await RunGroupAsync(EncodedRegistry, new
            {
                writes = writes.Select(write => new
                {
                    item = write.Item,
                    target = write.Target == RegistryTarget.Machine ? "machine" : "users",
                    key = write.Key,
                    name = write.Name,
                    type = write.Type,
                    value = write.Value,
                }),
            }, ApplyTimeout, writes.Select(write => write.Item).Distinct().ToList(), cancellationToken).ConfigureAwait(false);

            // A tweak can write several values; it counts as one item, done only when all of them were written.
            results.AddRange(grouped.GroupBy(item => item.Item, StringComparer.Ordinal).Select(group => group.FirstOrDefault(item => !item.Ok) ?? group.First()));
        }

        if (diff.Appx.Count > 0)
        {
            results.AddRange(await RunGroupAsync(
                EncodedAppx,
                new { appx = diff.Appx.Select(change => new { id = change.Name, item = AppxItem(change) }) },
                AppxTimeout,
                diff.Appx.Select(AppxItem).ToList(),
                cancellationToken).ConfigureAwait(false));
        }

        if (diff.Programs.Count > 0)
        {
            results.AddRange(await RunGroupAsync(EncodedPrograms, new
            {
                programs = diff.Programs.Select(change => new
                {
                    item = ProgramItem(change),
                    kind = change.Catalog.Uninstall,
                    processes = change.Catalog.Processes,
                    scope = change.Found.Scope,
                    key = change.Found.Key,
                    uninstall = change.Found.UninstallString,
                    quiet = change.Found.QuietUninstallString,
                    msi = change.Found.WindowsInstaller,
                }),
            }, ProgramsTimeout, diff.Programs.Select(ProgramItem).ToList(), cancellationToken).ConfigureAwait(false));
        }

        return results;
    }

    public async Task<string> CreateRestorePointAsync(string description, CancellationToken cancellationToken)
    {
        var result = await HostPowerShell.RunAsync(EncodedRestorePoint, new { description }, TimeSpan.FromMinutes(5), cancellationToken).ConfigureAwait(false);
        return $"{(string?)result?["description"] ?? description} (restore point {(int?)result?["sequence"]})";
    }

    public async Task<IReadOnlyList<string>> ExportRegistryAsync(IReadOnlyList<string> keys, string folder, CancellationToken cancellationToken)
    {
        var result = await HostPowerShell.RunAsync(EncodedExport, new { keys, folder }, TimeSpan.FromMinutes(5), cancellationToken).ConfigureAwait(false);
        return (result?["files"] as JsonArray)?.Select(file => (string)file!).ToList() ?? [];
    }

    internal static string StartupItem(StartupChange change) => $"Startup {change.Entry.Name} ({(change.Entry.Scope == "machine" ? "all users" : "one user")})";

    internal static string AppxItem(AppxChange change) => $"App {(change.FriendlyName.Length > 0 ? change.FriendlyName : change.Name)}";

    internal static string ProgramItem(ProgramChange change) => $"Program {change.Found.Name}";

    /// <summary>Runs one apply script. If the script fails as a whole, every item it was given is reported failed.</summary>
    private static async Task<IReadOnlyList<ApplyItemResult>> RunGroupAsync(string script, object request, TimeSpan timeout, IReadOnlyList<string> items, CancellationToken cancellationToken)
    {
        try
        {
            return ParseItems(await HostPowerShell.RunAsync(script, request, timeout, cancellationToken).ConfigureAwait(false));
        }
        catch (HostLeanException ex)
        {
            return items.Select(item => new ApplyItemResult(item, false, ex.Message)).ToList();
        }
    }

    internal static IReadOnlyList<ApplyItemResult> ParseItems(JsonNode? result)
    {
        if (result?["items"] is not JsonArray items)
        {
            throw new HostLeanException("The results of a Lean host step did not have the expected shape.");
        }

        return items.OfType<JsonObject>()
            .Where(item => (string?)item["item"] is { Length: > 0 })
            .Select(item => new ApplyItemResult(
                (string)item["item"]!,
                (bool?)item["ok"] ?? false,
                (string?)item["error"] is { Length: > 0 } error ? error.Trim() : null,
                (bool?)item["restart"] ?? false))
            .ToList();
    }

    internal static HostSnapshot ParseSnapshot(JsonNode? result)
    {
        if (result is not JsonObject root)
        {
            throw new HostLeanException("The host's state did not have the expected shape.");
        }

        static IEnumerable<JsonObject> Objects(JsonNode? node) => node is JsonArray array ? array.OfType<JsonObject>() : node is JsonObject single ? [single] : [];
        static string Text(JsonNode? node) => (string?)node ?? "";
        static IReadOnlyList<string> Strings(JsonNode? node) => node switch
        {
            JsonArray array => array.Select(item => (string?)item ?? "").Where(item => item.Length > 0).ToList(),
            JsonValue value when (string?)value is { Length: > 0 } text => [text],
            _ => [],
        };

        var power = root["power"] as JsonObject ?? new JsonObject();
        var registry = new Dictionary<string, IReadOnlyList<RegistryValueState>>(StringComparer.Ordinal);
        if (root["registry"] is JsonObject values)
        {
            foreach (var (id, hives) in values)
            {
                registry[id] = Objects(hives).Select(hive => new RegistryValueState(Text(hive["hive"]), (string?)hive["type"], (string?)hive["value"])).ToList();
            }
        }

        return new HostSnapshot(
            Objects(root["services"]).Select(service => new ServiceState(Text(service["name"]), Text(service["display"]), Text(service["startup"]), (bool?)service["running"] ?? false)).ToList(),
            Objects(root["startup"]).Select(entry => new StartupEntry(Text(entry["scope"]), Text(entry["source"]), Text(entry["name"]), Text(entry["command"]), (bool?)entry["enabled"] ?? true)).ToList(),
            new PowerState(
                Text(power["active"]).ToLowerInvariant(),
                Objects(power["plans"]).Select(plan => new PowerPlanInfo(Text(plan["guid"]).ToLowerInvariant(), Text(plan["name"]))).ToList(),
                Strings(power["armed"]),
                Strings(power["network"])),
            Strings(root["appx"]).ToHashSet(StringComparer.OrdinalIgnoreCase),
            Objects(root["programs"]).Select(program => new InstalledProgram(
                Text(program["name"]),
                Text(program["scope"]),
                Text(program["key"]),
                (string?)program["uninstall"] is { Length: > 0 } uninstall ? uninstall : null,
                (string?)program["quiet"] is { Length: > 0 } quiet ? quiet : null,
                (bool?)program["msi"] ?? false)).ToList(),
            Objects(root["printers"]).Select(printer => new PrinterInfo(Text(printer["name"]), Text(printer["port"]))).ToList(),
            registry,
            Strings(root["warnings"]));
    }
}
