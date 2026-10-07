using HyperHarbor.Host.Core.HostProfiles;
using HyperHarbor.Host.Core.Profiles;
using HyperHarbor.Shared.Contracts.Profiles;

namespace HyperHarbor.Host.Tests.HostProfiles;

/// <summary>The pure part of the Lean host action: what a profile changes on a given PC, and what the guards keep.</summary>
public sealed class HostDifferTests
{
    private static readonly SteamGame Halo = new(976730, "Halo: The Master Chief Collection");
    private static readonly SteamGame Uncurated = new(220, "Half-Life 2");

    private readonly HostDiffer _differ = new(Catalogs.Default, HostCatalogs.Default);
    private readonly SetupProfilePlanner _planner = new(Catalogs.Default);

    private static SetupProfile Host(
        ProfileStartup? startup = null,
        IReadOnlyList<ProfileService>? services = null,
        IReadOnlyList<ProfileItem>? appx = null,
        ProfilePower? power = null) =>
        new("Test", null, null, appx is null ? null : new ProfileRemove(appx, null, null), null, null, ProfileTarget.Host, services, startup, power);

    private HostDiff Diff(SetupProfile profile, FakeHostSystem host, params SteamGame[] games)
    {
        var plan = _planner.Plan(profile);
        var snapshot = host.InspectAsync(HostDiffer.Probes(plan), CancellationToken.None).GetAwaiter().GetResult();
        return _differ.Diff(plan, snapshot, games);
    }

    // The startup allowlist.

    [Fact]
    public void AStartupEntry_OnTheAllowlist_IsNeverDisabled_EvenByAWildcard()
    {
        var host = FakeHostSystem.GamingPc();
        host.Startup.Add(new StartupEntry("machine", "Run", "RtkAudUService", "x.exe", true));
        host.Startup.Add(new StartupEntry("machine", "Run", "Windows Defender notifications", "x.exe", true));
        host.Startup.Add(new StartupEntry("machine", "Run", "NVIDIA Broadcast", "x.exe", true));

        var diff = Diff(Host(startup: new ProfileStartup([new("*")], null, null)), host);

        var disabled = diff.Startup.Select(change => change.Entry.Name).ToList();
        Assert.Contains("OneDrive", disabled);
        Assert.Contains("iCUE", disabled);
        Assert.DoesNotContain("SecurityHealth", disabled);
        Assert.DoesNotContain("Steam", disabled);
        Assert.DoesNotContain("RtkAudUService", disabled);
        Assert.DoesNotContain("Windows Defender notifications", disabled);
        Assert.DoesNotContain("NVIDIA Broadcast", disabled);
        Assert.Contains(diff.Kept, note => note.StartsWith("Startup entry SecurityHealth: kept", StringComparison.Ordinal));
        Assert.Contains(diff.Kept, note => note.Contains("Steam", StringComparison.Ordinal) && note.Contains("game launcher", StringComparison.Ordinal));
    }

    [Fact]
    public void AStartupEntry_OnTheProfilesKeepList_IsNotDisabled()
    {
        var host = FakeHostSystem.GamingPc();

        var diff = Diff(Host(startup: new ProfileStartup([new("*")], null, [new("iCUE")])), host);

        Assert.DoesNotContain(diff.Startup, change => change.Entry.Name == "iCUE");
        Assert.Contains(diff.Kept, note => note.Contains("iCUE", StringComparison.Ordinal) && note.Contains("keep list", StringComparison.Ordinal));
    }

    [Fact]
    public void StartupEntries_AreMatchedByWildcard_AndDisabledOnesCountAsInPlace()
    {
        var host = FakeHostSystem.GamingPc();

        var diff = Diff(Host(startup: new ProfileStartup([new("OneDrive*"), new("Spotify*")], null, null)), host);

        Assert.Equal(["OneDrive"], diff.Startup.Select(change => change.Entry.Name));
        Assert.Equal(1, diff.AlreadyInPlace); // Spotify is already disabled.
    }

    [Fact]
    public void EnableList_ReenablesDisabledEntries()
    {
        var host = FakeHostSystem.GamingPc();

        var diff = Diff(Host(startup: new ProfileStartup(null, [new("Spotify")], null)), host);

        var change = Assert.Single(diff.Startup);
        Assert.True(change.Enable);
        Assert.Equal("Spotify", change.Entry.Name);
    }

    // The gaming services guard (Gaming Services and the Xbox Identity Provider).

    private static readonly SetupProfile XboxRemoval = Host(
        services: [new("GamingServices", "disabled"), new("XblAuthManager", "disabled"), new("DiagTrack", "disabled")],
        appx: [new("Microsoft.GamingServices"), new("Microsoft.XboxIdentityProvider"), new("Microsoft.BingNews")]);

    [Fact]
    public void GamingServicesAndTheXboxIdentityProvider_AreKept_WhenACuratedGameIsInASteamLibrary()
    {
        var diff = Diff(XboxRemoval, FakeHostSystem.GamingPc(), Halo, Uncurated);

        Assert.Equal(["Microsoft.BingNews"], diff.Appx.Select(change => change.Name));
        Assert.Equal(["DiagTrack"], diff.Services.Select(change => change.Name));
        Assert.Contains(diff.Kept, note => note.Contains("Microsoft.GamingServices", StringComparison.Ordinal) && note.Contains("Halo: The Master Chief Collection", StringComparison.Ordinal));
        Assert.Contains(diff.Kept, note => note.Contains("Xbox Identity Provider", StringComparison.Ordinal));
        Assert.Contains(diff.Kept, note => note.StartsWith("Service GamingServices: kept", StringComparison.Ordinal));
        Assert.Contains(diff.Kept, note => note.StartsWith("Service XblAuthManager: kept", StringComparison.Ordinal));
    }

    [Fact]
    public void GamingServicesAndTheXboxIdentityProvider_AreRemoved_WhenNoCuratedGameIsFound()
    {
        var diff = Diff(XboxRemoval, FakeHostSystem.GamingPc(), Uncurated);

        Assert.Equal(["Microsoft.BingNews", "Microsoft.GamingServices", "Microsoft.XboxIdentityProvider"], diff.Appx.Select(change => change.Name).Order(StringComparer.Ordinal));
        Assert.Equal(["DiagTrack", "GamingServices", "XblAuthManager"], diff.Services.Select(change => change.Name).Order(StringComparer.Ordinal));
        Assert.DoesNotContain(diff.Kept, note => note.Contains("Steam library", StringComparison.Ordinal));
    }

    [Fact]
    public void EveryCuratedGame_HasAnAppId_AndAReason()
    {
        Assert.All(HostCatalogs.Default.Guards.Games, game =>
        {
            Assert.True(game.AppId > 0);
            Assert.False(string.IsNullOrWhiteSpace(game.Name));
            Assert.False(string.IsNullOrWhiteSpace(game.Reason));
        });
        Assert.Equal(HostCatalogs.Default.Guards.Games.Count, HostCatalogs.Default.Guards.Games.Select(game => game.AppId).Distinct().Count());
    }

    // The spooler and Photos guards.

    [Fact]
    public void TheSpooler_IsKept_WhenAPhysicalPrinterExists_ButNotForVirtualOnes()
    {
        var profile = Host(services: [new("Spooler", "disabled")]);
        var host = FakeHostSystem.GamingPc();

        Assert.Equal(["Spooler"], Diff(profile, host).Services.Select(change => change.Name)); // only Print to PDF

        host.Printers.Add(new PrinterInfo("Brother HL-L2350DW series", "IP_192.168.1.40"));
        var kept = Diff(profile, host);

        Assert.Empty(kept.Services);
        Assert.Contains(kept.Kept, note => note.StartsWith("Service Spooler: kept, a physical printer exists", StringComparison.Ordinal));
    }

    [Fact]
    public void Photos_IsKept_WhenNoOtherImageViewerIsInstalled()
    {
        var profile = Host(appx: [new("Microsoft.Windows.Photos")]);
        var host = FakeHostSystem.GamingPc();

        var kept = Diff(profile, host);

        Assert.Empty(kept.Appx);
        Assert.Contains(kept.Kept, note => note.Contains("no other image viewer", StringComparison.Ordinal));

        host.Programs.Add(new InstalledProgram("IrfanView 4.67 (64-bit)", "machine", "IrfanView", null, null, false));
        var removed = Diff(profile, host);

        Assert.Equal(["Microsoft.Windows.Photos"], removed.Appx.Select(change => change.Name));
    }

    // What no profile may change.

    [Fact]
    public void HyperVServices_AndFrameworkApps_AreNeverChanged()
    {
        var profile = Host(services: [new("vmms", "disabled"), new("Fax", "automatic")], appx: [new("Microsoft.WindowsStore")]);

        var diff = Diff(profile, FakeHostSystem.GamingPc());

        Assert.Equal(["Fax"], diff.Services.Select(change => change.Name));
        Assert.Empty(diff.Appx);
        Assert.Contains(diff.Kept, note => note.StartsWith("Service vmms: kept (Hyper-V)", StringComparison.Ordinal));
        Assert.Contains(diff.Kept, note => note.Contains("framework or security piece", StringComparison.Ordinal));
    }

    [Fact]
    public void ServicesNotOnThisWindows_AreSkipped_WithoutAProblem()
    {
        var diff = Diff(Host(services: [new("NotAService", "disabled")]), FakeHostSystem.GamingPc());

        Assert.True(diff.IsEmpty);
        Assert.Empty(diff.Problems);
    }

    // Power.

    [Fact]
    public void PowerPlan_IsCreated_WhenHighPerformanceIsMissing_AndWakeIsLimitedToTheNetworkAdapter()
    {
        var diff = Diff(Host(power: new ProfilePower("highPerformance", "nicOnly", null)), FakeHostSystem.GamingPc());

        var power = Assert.IsType<PowerChange>(diff.Power);
        Assert.Equal("8c5e7fda-e8bf-4a96-9a85-a6e23a8c635c", power.PlanTo);
        Assert.True(power.Duplicate);
        Assert.Equal(["HID Keyboard Device"], power.Disarm);
        Assert.Empty(power.Arm);
    }

    [Fact]
    public void KeyboardsAndMice_KeepWaking_WithTheNicAndInputRule_ButOtherDevicesAreDisarmed()
    {
        var host = FakeHostSystem.GamingPc();
        host.Armed.AddRange(["HID-compliant mouse (004)", "Intel(R) Wi-Fi 6 AX201"]);

        var power = Assert.IsType<PowerChange>(Diff(Host(power: new ProfilePower(null, "nicAndInput", null)), host).Power);

        Assert.Equal(["Intel(R) Wi-Fi 6 AX201"], power.Disarm);
        Assert.Empty(power.Arm);
    }

    [Fact]
    public void WakeDevices_AreLeftAlone_WhenNoNetworkAdapterCanWake()
    {
        var host = FakeHostSystem.GamingPc();
        host.Network.Clear();

        var diff = Diff(Host(power: new ProfilePower(null, "nicOnly", null)), host);

        Assert.Null(diff.Power);
        Assert.Contains(diff.Problems, problem => problem.Contains("Wake-on-LAN", StringComparison.Ordinal));
    }

    [Fact]
    public void ANetworkAdapter_ThatMayNotWake_IsArmed()
    {
        var host = FakeHostSystem.GamingPc();
        host.Armed.Clear();

        var power = Assert.IsType<PowerChange>(Diff(Host(power: new ProfilePower(null, "nicOnly", null)), host).Power);

        Assert.Equal(["Intel(R) Ethernet Controller I225-V"], power.Arm);
        Assert.Empty(power.Disarm);
    }

    // Registry.

    [Fact]
    public void RegistryValues_AreSatisfied_OnlyWhenEveryHiveMatches()
    {
        var host = FakeHostSystem.GamingPc();
        var profile = Host() with { Tweaks = [new("system.disableGameDvr")] };

        var diff = Diff(profile, host);

        // GameDVR_Enabled is absent, AppCaptureEnabled is 1 in both hives, the policy is absent.
        Assert.Equal(3, diff.Registry.Count);

        host.Registry[$@"{FakeHostSystem.UserSid}|Software\Microsoft\Windows\CurrentVersion\GameDVR|AppCaptureEnabled"] = ("dword", "0");
        var partly = Diff(profile, host);

        Assert.Equal(3, partly.Registry.Count); // the Default hive still differs

        host.Registry[$@"Default|Software\Microsoft\Windows\CurrentVersion\GameDVR|AppCaptureEnabled"] = ("dword", "0");
        Assert.Equal(2, Diff(profile, host).Registry.Count);
    }

    [Fact]
    public void ARegistryChange_RemembersTheValueBeforeIt()
    {
        var profile = Host() with { Tweaks = [new("taskbar.disableWidgets")] };

        var change = Assert.Single(Diff(profile, FakeHostSystem.GamingPc()).Registry);

        Assert.Equal("dword", change.BeforeType);
        Assert.Equal("1", change.BeforeValue);
    }

    // Programs.

    [Fact]
    public void Programs_AreFoundByDisplayName_AndAbsentOnesCountAsInPlace()
    {
        var profile = Host() with { Remove = new ProfileRemove(null, null, null, [new("onedrive"), new("icue"), new("razer-synapse")]) };

        var diff = Diff(profile, FakeHostSystem.GamingPc());

        Assert.Equal(["Microsoft OneDrive", "iCUE"], diff.Programs.Select(change => change.Found.Name).Order(StringComparer.Ordinal));
        Assert.Equal(1, diff.AlreadyInPlace);
    }

    [Fact]
    public void TheFingerprint_FollowsTheChanges()
    {
        var host = FakeHostSystem.GamingPc();
        var profile = Host(services: [new("DiagTrack", "disabled")]);

        var first = Diff(profile, host).Fingerprint();
        Assert.Equal(first, Diff(profile, host).Fingerprint());

        host.Services[0] = host.Services[0] with { Startup = "disabled", Running = false };
        Assert.NotEqual(first, Diff(profile, host).Fingerprint());
    }
}
