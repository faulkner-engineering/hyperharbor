using HyperHarbor.Host.Core.HostProfiles;
using HyperHarbor.Host.Core.Profiles;
using HyperHarbor.Shared.Contracts.Ipc;

namespace HyperHarbor.Host.Tests.HostProfiles;

/// <summary>
/// An in-memory PC for the Lean host action: it reports its state like the PowerShell scripts do and applies a diff
/// by changing that state, so a second look after an apply shows what really stayed. Registry values live per hive:
/// HKLM, one signed-in user, and the Default user.
/// </summary>
internal sealed class FakeHostSystem : IHostSystem
{
    public const string UserSid = "S-1-5-21-1-2-3-1001";

    public bool CanModify { get; set; } = true;

    public List<ServiceState> Services { get; } = [];

    public List<StartupEntry> Startup { get; } = [];

    public string ActivePlan { get; set; } = HostDiffer.KnownPlans["balanced"].Guid;

    public List<PowerPlanInfo> Plans { get; } =
    [
        new(HostDiffer.KnownPlans["balanced"].Guid, "Balanced"),
        new(HostDiffer.KnownPlans["powerSaver"].Guid, "Power saver"),
    ];

    public List<string> Armed { get; } = [];

    public List<string> Network { get; } = [];

    public HashSet<string> Appx { get; } = new(StringComparer.OrdinalIgnoreCase);

    public List<InstalledProgram> Programs { get; } = [];

    public List<PrinterInfo> Printers { get; } = [];

    /// <summary>"hive|key|name" to (type, value). Hives: HKLM, the user's SID, Default.</summary>
    public Dictionary<string, (string Type, string Value)> Registry { get; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>What happened, in order: inspect, restorePoint, export, apply.</summary>
    public List<string> Log { get; } = [];

    public int ApplyCalls => Log.Count(entry => entry == "apply");

    public Exception? RestorePointFailure { get; set; }

    public List<string> ExportedKeys { get; } = [];

    /// <summary>Items that fail to apply, by item text.</summary>
    public HashSet<string> FailingItems { get; } = new(StringComparer.Ordinal);

    public Task<HostSnapshot> InspectAsync(IReadOnlyList<RegistryProbe> probes, CancellationToken cancellationToken)
    {
        Log.Add("inspect");
        var registry = new Dictionary<string, IReadOnlyList<RegistryValueState>>(StringComparer.Ordinal);
        foreach (var probe in probes)
        {
            var hives = probe.Target == RegistryTarget.Machine ? new[] { "HKLM" } : [UserSid, "Default"];
            registry[probe.Id] = hives.Select(hive =>
            {
                var found = Registry.TryGetValue($"{hive}|{probe.Key}|{probe.Name}", out var value);
                return new RegistryValueState(hive, found ? value.Type : null, found ? value.Value : null);
            }).ToList();
        }

        return Task.FromResult(new HostSnapshot(
            Services.ToList(),
            Startup.ToList(),
            new PowerState(ActivePlan, Plans.ToList(), Armed.ToList(), Network.ToList()),
            Appx.ToHashSet(StringComparer.OrdinalIgnoreCase),
            Programs.ToList(),
            Printers.ToList(),
            registry,
            []));
    }

    public Task<IReadOnlyList<ApplyItemResult>> ApplyAsync(HostDiff diff, CancellationToken cancellationToken)
    {
        Log.Add("apply");
        var results = new List<ApplyItemResult>();
        void Done(string item, Action change)
        {
            if (FailingItems.Contains(item))
            {
                results.Add(new ApplyItemResult(item, false, "refused"));
                return;
            }

            change();
            results.Add(new ApplyItemResult(item, true));
        }

        foreach (var change in diff.Services)
        {
            Done($"Service {change.Name}", () =>
            {
                var index = Services.FindIndex(service => service.Name == change.Name);
                Services[index] = Services[index] with { Startup = change.To, Running = Services[index].Running && !change.StopNow };
            });
        }

        foreach (var change in diff.Startup)
        {
            Done(PowerShellHostSystem.StartupItem(change), () =>
            {
                var index = Startup.FindIndex(entry => entry.Key == change.Entry.Key);
                Startup[index] = Startup[index] with { Enabled = change.Enable };
            });
        }

        foreach (var change in diff.Registry)
        {
            Done(change.Write.Item, () =>
            {
                var hives = change.Write.Target == RegistryTarget.Machine ? new[] { "HKLM" } : [UserSid, "Default"];
                foreach (var hive in hives)
                {
                    var key = $"{hive}|{change.Write.Key}|{change.Write.Name}";
                    if (change.Write.Type == "absent")
                    {
                        Registry.Remove(key);
                    }
                    else
                    {
                        Registry[key] = (change.Write.Type, change.Write.Value);
                    }
                }
            });
        }

        if (diff.Power is { } power)
        {
            if (power.PlanTo is not null)
            {
                Done("Power plan", () =>
                {
                    if (power.Duplicate)
                    {
                        Plans.Add(new PowerPlanInfo(power.PlanTo, power.PlanToName ?? "Created"));
                    }

                    ActivePlan = power.PlanTo;
                });
            }

            foreach (var device in power.Disarm)
            {
                Done($"Wake: {device}", () => Armed.Remove(device));
            }

            foreach (var device in power.Arm)
            {
                Done($"Wake: {device}", () => Armed.Add(device));
            }
        }

        foreach (var change in diff.Appx)
        {
            Done(PowerShellHostSystem.AppxItem(change), () => Appx.Remove(change.Name));
        }

        foreach (var change in diff.Programs)
        {
            Done(PowerShellHostSystem.ProgramItem(change), () => Programs.RemoveAll(program => program.Key == change.Found.Key && program.Scope == change.Found.Scope));
        }

        return Task.FromResult<IReadOnlyList<ApplyItemResult>>(results);
    }

    public Task<string> CreateRestorePointAsync(string description, CancellationToken cancellationToken)
    {
        if (RestorePointFailure is not null)
        {
            throw RestorePointFailure;
        }

        Log.Add("restorePoint");
        return Task.FromResult($"{description} (restore point 7)");
    }

    public Task<IReadOnlyList<string>> ExportRegistryAsync(IReadOnlyList<string> keys, string folder, CancellationToken cancellationToken)
    {
        Log.Add("export");
        ExportedKeys.AddRange(keys);
        return Task.FromResult<IReadOnlyList<string>>(keys.Select((_, index) => Path.Combine(folder, $"export-{index + 1:D3}.reg")).ToList());
    }

    /// <summary>A gaming PC with something for every handler of the shipped profile to do.</summary>
    public static FakeHostSystem GamingPc()
    {
        var host = new FakeHostSystem();
        host.Services.AddRange(
        [
            new ServiceState("DiagTrack", "Connected User Experiences and Telemetry", "automatic", true),
            new ServiceState("MapsBroker", "Downloaded Maps Manager", "automaticDelayed", false),
            new ServiceState("Spooler", "Print Spooler", "automatic", true),
            new ServiceState("XblAuthManager", "Xbox Live Auth Manager", "manual", false),
            new ServiceState("GamingServices", "Gaming Services", "manual", true),
            new ServiceState("vmms", "Hyper-V Virtual Machine Management", "automatic", true),
            new ServiceState("Fax", "Fax", "disabled", false),
        ]);
        host.Startup.AddRange(
        [
            new StartupEntry("machine", "Run", "SecurityHealth", @"C:\Windows\system32\SecurityHealthSystray.exe", true),
            new StartupEntry($"user:{UserSid}", "Run", "OneDrive", @"C:\Users\p\AppData\Local\Microsoft\OneDrive\OneDrive.exe /background", true),
            new StartupEntry($"user:{UserSid}", "Run", "Steam", @"C:\Program Files (x86)\Steam\steam.exe -silent", true),
            new StartupEntry("machine", "Run", "iCUE", @"C:\Program Files\Corsair\iCUE.exe", true),
            new StartupEntry($"user:{UserSid}", "Run", "Spotify", @"C:\Users\p\AppData\Roaming\Spotify\Spotify.exe", false),
        ]);
        host.Armed.AddRange(["Intel(R) Ethernet Controller I225-V", "HID Keyboard Device"]);
        host.Network.Add("Intel(R) Ethernet Controller I225-V");
        host.Appx.UnionWith(["Microsoft.BingNews", "Microsoft.Windows.Photos", "Microsoft.GamingServices", "Microsoft.XboxIdentityProvider", "Microsoft.WindowsStore", "Microsoft.Copilot"]);
        host.Programs.AddRange(
        [
            new InstalledProgram("Microsoft OneDrive", $"user:{UserSid}", "OneDriveSetup.exe", @"""C:\Users\p\AppData\Local\Microsoft\OneDrive\24.1\OneDriveSetup.exe"" /uninstall", null, false),
            new InstalledProgram("iCUE", "machine", "{11111111-2222-3333-4444-555555555555}", null, null, true),
            new InstalledProgram("Git", "machine", "Git_is1", @"C:\Program Files\Git\unins000.exe", null, false),
        ]);
        host.Printers.Add(new PrinterInfo("Microsoft Print to PDF", "PORTPROMPT:"));
        host.Registry[$"HKLM|SOFTWARE\\Policies\\Microsoft\\Dsh|AllowNewsAndInterests"] = ("dword", "1");
        host.Registry[$"{UserSid}|Software\\Microsoft\\Windows\\CurrentVersion\\GameDVR|AppCaptureEnabled"] = ("dword", "1");
        host.Registry[$"Default|Software\\Microsoft\\Windows\\CurrentVersion\\GameDVR|AppCaptureEnabled"] = ("dword", "1");
        return host;
    }
}

internal sealed class FakeSteamLibrary(params SteamGame[] games) : ISteamLibrary
{
    public List<SteamGame> Games { get; } = [.. games];

    public IReadOnlyList<SteamGame> InstalledGames() => Games;
}

/// <summary>Reports a quieter PC after each sample, as the Lean host action should leave it.</summary>
internal sealed class FakeHostMetrics(TimeProvider time) : IHostMetrics
{
    private int _used = 9000;
    private int _processes = 260;

    public int Samples { get; private set; }

    public Task<HostLeanMetrics> SampleAsync(CancellationToken cancellationToken)
    {
        Samples++;
        var sample = new HostLeanMetrics(time.GetUtcNow(), _used, _processes, true);
        _used -= 600;
        _processes -= 25;
        return Task.FromResult(sample);
    }
}
