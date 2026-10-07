using System.Diagnostics;
using System.Text;
using System.Text.Json.Nodes;
using HyperHarbor.Host.Core.HostProfiles;
using HyperHarbor.Host.Core.Lifecycle;
using HyperHarbor.Host.Core.Profiles;
using HyperHarbor.Host.Tests.Profiles;
using HyperHarbor.Shared.Contracts.Profiles;
using Json.Schema;

namespace HyperHarbor.Host.Tests.HostProfiles;

/// <summary>The shipped host profile, the host catalogs, and the YAML and script plumbing around them.</summary>
public sealed class HostProfileFormatTests
{
    private static readonly ProfileValidator Validator = new(Catalogs.Default, HostCatalogs.Default);

    [Fact]
    public void TheShippedProfile_ReadsAndValidates_AsAHostProfile()
    {
        var profile = Validator.Normalize(ProfileYamlReader.Read(HostLeanProfiles.LeanYaml()));

        Assert.Equal(ProfileTarget.Host, profile.Target);
        Assert.Equal("Host gaming", profile.Name);
        Assert.NotEmpty(profile.Services!);
        Assert.NotEmpty(profile.Startup!.Disable!);
        Assert.Equal("highPerformance", profile.Power!.Plan);
        Assert.Equal("nicAndInput", profile.Power.Wake);
        Assert.Contains(profile.Remove!.Programs!, program => program.Id == "onedrive");
        Assert.Contains(profile.Remove.Appx!, app => app.Id == "Microsoft.GamingServices");
        Assert.Contains(profile.Tweaks!, tweak => tweak.Id == "system.disableGameDvr");
        Assert.Equal("Microsoft.Edge", profile.Browser!.App.Id);
    }

    [Fact]
    public void TheShippedProfile_IsValidAgainstTheSchema()
    {
        var result = ProfileYamlTests.ProfileSchema.Value.Evaluate(ProfileYamlTests.ToJson(HostLeanProfiles.LeanYaml()), new EvaluationOptions { OutputFormat = OutputFormat.List });

        Assert.True(result.IsValid, string.Join("; ", result.Details?.Where(detail => detail.Errors is not null).SelectMany(detail => detail.Errors!.Select(error => $"{detail.InstanceLocation}: {error.Value}")) ?? []));
    }

    [Fact]
    public void TheShippedProfile_RemovesEveryConsumerAppAndRgbSuiteTheTaskNames()
    {
        var profile = ProfileYamlReader.Read(HostLeanProfiles.LeanYaml());

        var programs = profile.Remove!.Programs!.Select(item => item.Id).ToList();
        Assert.Contains("onedrive", programs);
        Assert.All(["icue", "razer-synapse", "armoury-crate", "msi-center", "gigabyte-control-center", "signalrgb", "nzxt-cam"], id => Assert.Contains(id, programs));

        var tweaks = profile.Tweaks!.Select(tweak => tweak.Id).ToList();
        Assert.Contains("taskbar.disableWidgets", tweaks);
        Assert.Contains("system.disableCopilot", tweaks);
        Assert.Contains("start.disableSearchHighlights", tweaks);
        Assert.Contains("system.quietGameBar", tweaks);
        Assert.Contains("system.disableGameDvr", tweaks);

        Assert.Equal("highPerformance", profile.Power!.Plan);
        Assert.Equal("nicAndInput", profile.Power.Wake);
        Assert.Contains(profile.Services!, service => service.Id == "DiagTrack" && service.Startup == "disabled");
    }

    [Fact]
    public void TheShippedProfile_TouchesNoProtectedService_AndNoFrameworkApp()
    {
        var profile = ProfileYamlReader.Read(HostLeanProfiles.LeanYaml());
        var guards = HostCatalogs.Default.Guards;

        Assert.DoesNotContain(profile.Services!, service => Wildcard.IsMatchAny(guards.ProtectedServices.Select(entry => entry.Pattern), service.Id));
        Assert.DoesNotContain(profile.Remove!.Appx!, app => Catalogs.Default.AppxIndex.TryGetValue(app.Id, out var entry) && entry.Rating == AppxRating.Keep);
    }

    [Fact]
    public void AHostProfile_WritesAndReadsBack_WithEveryHostSection()
    {
        var profile = new SetupProfile(
            "Round trip",
            null,
            null,
            new ProfileRemove(null, null, null, [new("onedrive", "Microsoft OneDrive")]),
            [new(Name: "Put back", Registry: new RegistryTweak(@"HKLM\SOFTWARE\Policies\Thing", "Flag", RegistryValueType.Absent, ""))],
            null,
            ProfileTarget.Host,
            [new("DiagTrack", "disabled", "Connected User Experiences and Telemetry"), new("MapsBroker", "automaticDelayed")],
            new ProfileStartup([new("OneDrive*")], [new("Spotify")], [new("Steam*")]),
            new ProfilePower("highPerformance", "nicOnly", [new("HID Keyboard Device")]));

        var yaml = ProfileYamlWriter.Write(profile);
        var read = ProfileYamlReader.Read(yaml);

        Assert.Equal(yaml, ProfileYamlWriter.Write(read));
        Assert.Contains("target: host", yaml, StringComparison.Ordinal);
        Assert.Contains("  DiagTrack: disabled", yaml, StringComparison.Ordinal);
        Assert.Contains("type: absent", yaml, StringComparison.Ordinal);
        Assert.DoesNotContain("value:", yaml, StringComparison.Ordinal); // an absent value has none
        Assert.Equal(ProfileTarget.Host, read.Target);
        Assert.Equal("Connected User Experiences and Telemetry", read.Services![0].Name);
        Assert.Equal(RegistryValueType.Absent, read.Tweaks![0].Registry!.Type);
        Assert.Equal("", read.Tweaks[0].Registry!.Value);
        Assert.Equal(["OneDrive*"], read.Startup!.Disable!.Select(item => item.Id));
        Assert.Equal(["Spotify"], read.Startup.Enable!.Select(item => item.Id));
        Assert.Equal(["Steam*"], read.Startup.Keep!.Select(item => item.Id));
        Assert.Equal("nicOnly", read.Power!.Wake);
        Assert.Equal(["HID Keyboard Device"], read.Power.ArmWake!.Select(item => item.Id));
        Assert.Equal("Microsoft OneDrive", read.Remove!.Programs![0].Name);
    }

    [Fact]
    public void HostSections_AreRefused_InAVmProfile()
    {
        var profile = new SetupProfile("VM", null, null, new ProfileRemove(null, null, null, [new("onedrive")]), null, null, null,
            [new("DiagTrack", "disabled")], new ProfileStartup([new("x")], null, null), new ProfilePower("balanced", null, null));

        var error = Assert.Throws<LifecycleValidationException>(() => Validator.Normalize(profile));

        Assert.Equal(["services", "startup", "power", "remove.programs"], error.Errors.Select(issue => issue.Field));
    }

    [Fact]
    public void ABadHostProfile_ReportsTheFieldsItIsAbout()
    {
        var profile = new SetupProfile("Bad", null, [new("Git.Git")], new ProfileRemove(null, null, null, [new("not-a-program")]), null, null, ProfileTarget.Host,
            [new("Bad Name", "disabled"), new("DiagTrack", "sometimes")],
            new ProfileStartup([new(@"C:\path")], [new("same")], null),
            new ProfilePower("fast", "everyone", null));

        var error = Assert.Throws<LifecycleValidationException>(() => Validator.Normalize(profile));

        Assert.Equal(
            ["install", "remove.programs[0]", "services[0]", "services[1]", "startup.disable[0]", "power.plan", "power.wake"],
            error.Errors.Select(issue => issue.Field));
    }

    [Fact]
    public void AnAbsentRegistryValue_NeedsAnEmptyValue()
    {
        var profile = new SetupProfile("Absent", null, null, null,
            [new(Registry: new RegistryTweak(@"HKLM\SOFTWARE\X", "Y", RegistryValueType.Absent, "1"))], null, ProfileTarget.Host);

        var error = Assert.Throws<LifecycleValidationException>(() => Validator.Normalize(profile));

        Assert.Equal(["tweaks[0].registry.value"], error.Errors.Select(issue => issue.Field));
    }

    [Fact]
    public void TheSchema_AndTheHostModel_HaveTheSameProperties()
    {
        var schema = JsonNode.Parse(Catalogs.ProfileSchema)!;
        static IEnumerable<string> Keys(JsonNode? node) => node!.AsObject().Select(pair => pair.Key);

        Assert.Equal(["target", "services", "startup", "power"], Keys(schema["properties"]).Skip(7));
        Assert.Equal(["disable", "enable", "keep"], Keys(schema["properties"]!["startup"]!["properties"]));
        Assert.Equal(["plan", "wake", "armWake"], Keys(schema["properties"]!["power"]!["properties"]));
        Assert.Equal(["appx", "capabilities", "features", "programs"], Keys(schema["properties"]!["remove"]!["properties"]));
        Assert.Equal(["vm", "host"], schema["properties"]!["target"]!["enum"]!.AsArray().Select(value => (string)value!));
        Assert.Equal(ProfileValidator.ServiceStartupTypes, schema["properties"]!["services"]!["additionalProperties"]!["enum"]!.AsArray().Select(value => (string)value!));
        Assert.Equal(ProfileValidator.WakeRules, schema["properties"]!["power"]!["properties"]!["wake"]!["enum"]!.AsArray().Select(value => (string)value!));
    }

    // The catalogs.

    [Fact]
    public void TheHostCatalogs_AreWellFormed()
    {
        var catalogs = HostCatalogs.Default;

        Assert.NotEmpty(catalogs.StartupAllowlist);
        Assert.All(catalogs.StartupAllowlist, entry =>
        {
            Assert.False(string.IsNullOrWhiteSpace(entry.Pattern));
            Assert.False(string.IsNullOrWhiteSpace(entry.Reason));
            Assert.True(Wildcard.IsMatch(entry.Pattern, entry.Pattern.Replace("*", "x", StringComparison.Ordinal).Replace("?", "y", StringComparison.Ordinal)));
        });
        Assert.All(catalogs.Programs, program =>
        {
            Assert.Matches("^[a-z0-9][a-z0-9.-]{0,39}$", program.Id);
            Assert.NotEmpty(program.Match);
            Assert.Contains(program.Uninstall, (string[])["standard", "oneDrive"]);
        });
        Assert.Equal(catalogs.Programs.Count, catalogs.Programs.Select(program => program.Id).Distinct().Count());
        Assert.NotEmpty(catalogs.Guards.ProtectedServices);
        Assert.Contains(catalogs.Guards.ProtectedServices, entry => Wildcard.IsMatch(entry.Pattern, "vmcompute"));
        Assert.Contains(catalogs.Guards.GamingServices.KeepAppx, name => name == "Microsoft.XboxIdentityProvider");
        Assert.Contains(catalogs.Guards.GamingServices.KeepServices, name => name == "GamingServices");
    }

    [Theory]
    [InlineData("OneDrive*", "OneDrive", true)]
    [InlineData("onedrive*", "OneDriveSetup", true)]
    [InlineData("OneDrive", "OneDriveSetup", false)]
    [InlineData("iC?E", "iCUE", true)]
    [InlineData("*Defender*", "Windows Defender notifications", true)]
    [InlineData("a.b", "aXb", false)]
    public void Wildcards_MatchLikeStartupEntryNames(string pattern, string text, bool expected)
    {
        Assert.Equal(expected, Wildcard.IsMatch(pattern, text));
    }

    // The scripts.

    [Fact]
    public void EveryScript_FitsTheCommandLine()
    {
        foreach (var script in AllScripts())
        {
            Assert.True(HostPowerShell.Encode(script).Length < 30000, "A script is too long for -EncodedCommand.");
        }
    }

    [Fact]
    public void EveryScript_ParsesInWindowsPowerShell()
    {
        var failures = new List<string>();
        foreach (var script in AllScripts())
        {
            var start = new ProcessStartInfo("powershell.exe") { UseShellExecute = false, CreateNoWindow = true, RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true };
            foreach (var argument in new[] { "-NoProfile", "-NonInteractive", "-Command", "$text = [Console]::In.ReadToEnd(); $errors = $null; [void][System.Management.Automation.Language.Parser]::ParseInput($text, [ref]$null, [ref]$errors); $errors | ForEach-Object { $_.Message + ' at line ' + $_.Extent.StartLineNumber }" })
            {
                start.ArgumentList.Add(argument);
            }

            using var process = Process.Start(start)!;
            process.StandardInput.Write(script);
            process.StandardInput.Close();
            var output = process.StandardOutput.ReadToEnd().Trim();
            process.WaitForExit();
            if (output.Length > 0)
            {
                failures.Add(output);
            }
        }

        Assert.Empty(failures);
    }

    private static IEnumerable<string> AllScripts() =>
    [
        HostScripts.Inspect, HostScripts.System, HostScripts.Registry, HostScripts.Appx, HostScripts.Programs, HostScripts.RestorePoint, HostScripts.ExportRegistry,
    ];

    [Fact]
    public void TheHostsAnswer_ParsesIntoASnapshot()
    {
        const string json = """
            { "services": [ { "name": "DiagTrack", "display": "Telemetry", "startup": "automatic", "running": true } ],
              "startup": [ { "scope": "machine", "source": "Run", "name": "OneDrive", "command": "x.exe", "enabled": false } ],
              "power": { "active": "381B4222-F694-41F0-9685-FF5BB260DF2E", "plans": [ { "guid": "381b4222-f694-41f0-9685-ff5bb260df2e", "name": "Balanced" } ], "armed": "HID Keyboard Device", "network": [] },
              "appx": [ "Microsoft.BingNews" ],
              "programs": [ { "name": "iCUE", "scope": "machine", "key": "{1}", "uninstall": "", "quiet": "q.exe /S", "msi": false } ],
              "printers": [ { "name": "Brother", "port": "IP_1" } ],
              "registry": { "users|a|b": [ { "hive": "S-1-5-21-1", "type": "dword", "value": "0" }, { "hive": "Default", "type": null, "value": null } ] },
              "warnings": [ "careful" ] }
            """;

        var snapshot = PowerShellHostSystem.ParseSnapshot(JsonNode.Parse(json));

        Assert.Equal("automatic", Assert.Single(snapshot.Services).Startup);
        Assert.False(Assert.Single(snapshot.Startup).Enabled);
        Assert.Equal("381b4222-f694-41f0-9685-ff5bb260df2e", snapshot.Power.ActivePlan);
        Assert.Equal(["HID Keyboard Device"], snapshot.Power.ArmedDevices); // PowerShell sends one item as a string
        Assert.Empty(snapshot.Power.NetworkDevices);
        Assert.Contains("Microsoft.BingNews", snapshot.Appx);
        Assert.Null(Assert.Single(snapshot.Programs).UninstallString);
        Assert.Equal("q.exe /S", snapshot.Programs[0].QuietUninstallString);
        Assert.Equal("Brother", Assert.Single(snapshot.Printers).Name);
        Assert.Equal([("S-1-5-21-1", "dword"), ("Default", null)], snapshot.Registry["users|a|b"].Select(hive => (hive.Hive, hive.Type)));
        Assert.Equal(["careful"], snapshot.Warnings);
    }

    [Fact]
    public void ApplyResults_ParseWithTheirRestartFlag()
    {
        var items = PowerShellHostSystem.ParseItems(JsonNode.Parse("""{ "items": [ { "item": "Program x", "ok": true, "restart": true }, { "item": "Service y", "ok": false, "error": "denied " } ] }"""));

        Assert.Equal([new ApplyItemResult("Program x", true, null, true), new ApplyItemResult("Service y", false, "denied")], items);
        Assert.Throws<HostLeanException>(() => PowerShellHostSystem.ParseItems(JsonNode.Parse("""{ "nothing": 1 }""")));
    }

    // Undo profiles.

    [Fact]
    public void TheUndoProfile_RestoresServices_StartupEntries_AndValues_AndSkipsWhatItCannotRestore()
    {
        var diff = HostDiff.Empty with
        {
            Services = [new ServiceChange("DiagTrack", "Telemetry", "automatic", "disabled", true), new ServiceChange("Odd", "Odd", "boot", "disabled", false)],
            Startup = [new StartupChange(new StartupEntry("machine", "Run", "OneDrive", "x", true), Enable: false), new StartupChange(new StartupEntry("machine", "Folder", @"a\b.lnk", "x", true), Enable: false)],
            Registry =
            [
                new RegistryChange(new RegistryWrite("t", RegistryTarget.Machine, @"SOFTWARE\P", "A", "dword", "0"), "dword", "1"),
                new RegistryChange(new RegistryWrite("t", RegistryTarget.DefaultUser, @"Software\Q", "B", "dword", "0"), null, null),
                new RegistryChange(new RegistryWrite("t", RegistryTarget.Machine, @"SOFTWARE\R", "C", "dword", "0"), "other", null),
            ],
            Appx = [new AppxChange("Microsoft.BingNews", "News")],
        };

        var undo = UndoProfileBuilder.FromDiff(diff);

        Assert.Equal(ProfileTarget.Host, undo.Target);
        Assert.Equal([("DiagTrack", "automatic")], undo.Services!.Select(service => (service.Id, service.Startup)));
        Assert.Equal(["OneDrive"], undo.Startup!.Enable!.Select(item => item.Id));
        Assert.Equal(2, undo.Tweaks!.Count);
        Assert.Equal((@"HKLM\SOFTWARE\P", RegistryValueType.Dword, "1"), (undo.Tweaks[0].Registry!.Key, undo.Tweaks[0].Registry!.Type, undo.Tweaks[0].Registry!.Value));
        Assert.Equal((@"HKCU\Software\Q", RegistryValueType.Absent), (undo.Tweaks[1].Registry!.Key, undo.Tweaks[1].Registry!.Type));
        Assert.Null(undo.Remove);
        Assert.False(UndoProfileBuilder.IsEmpty(undo));
        Assert.True(UndoProfileBuilder.IsEmpty(UndoProfileBuilder.FromDiff(HostDiff.Empty with { Appx = diff.Appx })));

        var normalized = Validator.Normalize(ProfileYamlReader.Read(ProfileYamlWriter.Write(undo)));
        Assert.Equal(ProfileTarget.Host, normalized.Target);
    }

    [Fact]
    public void MergingUndoProfiles_KeepsTheEarlierEntry()
    {
        SetupProfile Undo(string startup, string value) => new(UndoProfileBuilder.Name, null, null, null,
            [new(Registry: new RegistryTweak(@"HKLM\SOFTWARE\P", "A", RegistryValueType.Dword, value))], null, ProfileTarget.Host,
            [new("DiagTrack", startup)], null, null);

        var merged = UndoProfileBuilder.Merge(Undo("automatic", "1"), Undo("disabled", "0"));

        Assert.Equal("automatic", Assert.Single(merged.Services!).Startup);
        Assert.Equal("1", Assert.Single(merged.Tweaks!).Registry!.Value);
    }
}

/// <summary>Reading Steam's library folders and manifests.</summary>
public sealed class SteamLibraryTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "hh-steam-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    private void Manifest(string library, long appId, string name, int flags = 4)
    {
        var apps = Path.Combine(library, "steamapps");
        Directory.CreateDirectory(apps);
        File.WriteAllText(Path.Combine(apps, $"appmanifest_{appId}.acf"), $"\"AppState\"\n{{\n\t\"appid\"\t\t\"{appId}\"\n\t\"name\"\t\t\"{name}\"\n\t\"StateFlags\"\t\t\"{flags}\"\n}}\n");
    }

    [Fact]
    public void Games_AreFoundInEveryLibraryFolder()
    {
        var steam = Path.Combine(_root, "Steam");
        var second = Path.Combine(_root, "Games", "SteamLibrary");
        Manifest(steam, 220, "Half-Life 2");
        Manifest(second, 976730, "Halo: The Master Chief Collection");
        Manifest(second, 1240440, "Halo Infinite", flags: 1026); // still downloading
        File.WriteAllText(
            Path.Combine(steam, "steamapps", "libraryfolders.vdf"),
            "\"libraryfolders\"\n{\n\t\"0\"\n\t{\n\t\t\"path\"\t\t\"" + steam.Replace(@"\", @"\\", StringComparison.Ordinal) + "\"\n\t}\n\t\"1\"\n\t{\n\t\t\"path\"\t\t\"" + second.Replace(@"\", @"\\", StringComparison.Ordinal) + "\"\n\t}\n}\n");

        var games = new SteamLibrary(() => steam).InstalledGames();

        Assert.Equal([(220L, "Half-Life 2"), (976730L, "Halo: The Master Chief Collection")], games.Select(game => (game.AppId, game.Name)));
    }

    [Fact]
    public void WithoutSteam_ThereAreNoGames()
    {
        Assert.Empty(new SteamLibrary(() => null).InstalledGames());
        Assert.Empty(new SteamLibrary(() => Path.Combine(_root, "missing")).InstalledGames());
    }

    [Fact]
    public void ALibraryFolderWithoutAListFile_StillReadsSteamsOwnFolder()
    {
        var steam = Path.Combine(_root, "Steam");
        Manifest(steam, 813780, "Age of Empires II: Definitive Edition");

        Assert.Equal([813780L], new SteamLibrary(() => steam).InstalledGames().Select(game => game.AppId));
    }
}
