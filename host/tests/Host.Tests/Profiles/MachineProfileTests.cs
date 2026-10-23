using HyperHarbor.Host.Core.Lifecycle;
using HyperHarbor.Host.Core.Profiles;
using HyperHarbor.Host.Core.Unattend;
using HyperHarbor.Shared.Contracts.Backup;
using HyperHarbor.Shared.Contracts.Profiles;
using HyperHarbor.Shared.Contracts.Unattend;

namespace HyperHarbor.Host.Tests.Profiles;

/// <summary>Machine profile files and the rules a profile must follow.</summary>
public sealed class MachineProfileTests : IDisposable
{
    private readonly string _directory = Directory.CreateTempSubdirectory("hh-machine-").FullName;
    private readonly MachineProfileValidator _validator;

    public MachineProfileTests()
    {
        _validator = new MachineProfileValidator(new ProfileValidator(Catalogs.Default), new UnattendProfileStore(_directory, () => "Central Standard Time"));
    }

    public void Dispose() => Directory.Delete(_directory, recursive: true);

    public static MachineProfile Sample() => new(
        "Gaming rig",
        InstallOs.Windows,
        "Steam, Discord, and a GPU share",
        new MachineAccount("hhadmin", null, "en-US", "GAMING-RIG", new WindowsInstallSettings("Windows 11 Pro"), null),
        new MachineDisplay(
            new MachineGuestDisplay(GuestTheme.Dark, "#E39B2E", new MachineWallpaper("#0B0D10")),
            new MachineConnection(ConnectionMonitors.All, ConnectionWindow.FullScreen, new MachineRedirect(Clipboard: true, Printers: false))),
        new MachineFirmware(Tpm: true, SecureBoot: true),
        new MachineCompute(8, new MachineMemory(16384, null, false), false, 256),
        new MachineGpu(true, null, new MachineGpuShare(60, 50, 50, 60)),
        new MachineNetwork("Default Switch", false),
        new MachineBackup(true, "NAS", new BackupTriggers(true, true, 15), null, null, BackupGpuMode.RequireOff),
        [new ProfileItem("Valve.Steam", "Steam"), new ProfileItem("discord")],
        new ProfileRemove([new ProfileItem("Microsoft.BingNews")], null, null, null),
        [new ProfileTweak("appearance.darkMode")],
        new ProfileBrowser(new ProfileItem("Brave.Brave"), [], null));

    [Fact]
    public void AProfile_WritesAsYaml_AndReadsBackTheSame()
    {
        var profile = _validator.Normalize(Sample());

        var yaml = MachineProfileYaml.Write(profile, _validator.PolicyTypes);
        var read = _validator.Normalize(MachineProfileYaml.Read(yaml));

        Assert.StartsWith("# yaml-language-server: $schema=" + MachineProfileYaml.SchemaUrl, yaml, StringComparison.Ordinal);
        Assert.Contains("os: windows\n", yaml, StringComparison.Ordinal);
        Assert.Contains("  memory:\n    startupMb: 16384\n    dynamic: false\n", yaml, StringComparison.Ordinal);
        Assert.Contains("    window: fullScreen\n", yaml, StringComparison.Ordinal);
        Assert.Contains("    accentColor: '#E39B2E'\n", yaml, StringComparison.Ordinal);
        Assert.Contains("# Steam", yaml, StringComparison.Ordinal);
        Assert.Equal(MachineProfileYaml.Write(profile, _validator.PolicyTypes), MachineProfileYaml.Write(read, _validator.PolicyTypes));
        Assert.Equal(profile.Compute, read.Compute);
        Assert.Equal(profile.Display!.Guest, read.Display!.Guest);
        Assert.Equal(profile.Backup!.Triggers, read.Backup!.Triggers);
        Assert.Equal(["Valve.Steam", "discord"], read.Install!.Select(item => item.Id));
    }

    [Fact]
    public void AHandWrittenFile_Reads()
    {
        const string yaml = """
            schemaVersion: 1
            name: Small
            os: linux
            account:
              adminAccountName: ops
              linux:
                sshAuthorizedKeys: []
                packages: [htop, git]
            compute:
              processors: 2
              memory: { startupMb: 2048, maximumMb: 4096 }
            network:
              switch: LAN
            """;

        var profile = _validator.Normalize(MachineProfileYaml.Read(yaml));

        Assert.Equal(InstallOs.Linux, profile.Os);
        Assert.Equal(["htop", "git"], profile.Account!.Linux!.Packages!);
        Assert.Equal(new MachineMemory(2048, 4096, true), profile.Compute!.Memory);
        Assert.Equal("LAN", profile.Network!.Switch);
    }

    [Theory]
    [InlineData("schemaVersion: 1\nname: x\n", "os is required")]
    [InlineData("schemaVersion: 1\nname: x\nos: macos\n", "os must be windows or linux")]
    [InlineData("schemaVersion: 1\nname: x\nos: windows\ncompute:\n  cpus: 4\n", "compute.cpus: is not a field")]
    [InlineData("schemaVersion: 1\nname: x\nos: windows\ncompute:\n  processors: many\n", "compute.processors: has a value of the wrong kind")]
    [InlineData("schemaVersion: 1\nname: x\nos: windows\ngpu:\n  adapter: x\n", "missing required properties")]
    [InlineData("schemaVersion: 1\nname: x\nos: windows\nfirmware: true\n", "firmware must be a mapping")]
    [InlineData("schemaVersion: 1\nname: x\nos: windows\nmonitors: all\n", "does not have \"monitors\"")]
    public void AFileWithTheWrongShape_SaysWhere(string yaml, string expected)
    {
        var error = Assert.Throws<ProfileFormatException>(() => MachineProfileYaml.Read(yaml));

        Assert.Contains(expected, error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void QuotedNumbersStayText()
    {
        var profile = MachineProfileYaml.Read("schemaVersion: 1\nname: x\nos: windows\naccount:\n  computerName: '1234-A'\n");

        Assert.Equal("1234-A", profile.Account!.ComputerName);
    }

    [Fact]
    public void TheRules_ReportEachProblemByField()
    {
        var bad = Sample() with
        {
            Account = new MachineAccount("Administrator", null, "nope", "1234", new WindowsInstallSettings("Windows 11 Pro"), null),
            Display = new MachineDisplay(new MachineGuestDisplay(null, "orange", new MachineWallpaper("#000000", "a.png"))),
            Compute = new MachineCompute(0, new MachineMemory(1000, 512, true), null, 0),
            Gpu = new MachineGpu(true, null, new MachineGpuShare(0)),
            Backup = new MachineBackup(true),
            Install = [new ProfileItem("not valid")],
        };

        var error = Assert.Throws<LifecycleValidationException>(() => _validator.Normalize(bad));
        var fields = error.Errors.Select(issue => issue.Field).ToList();

        Assert.Contains("account.adminAccountName", fields);
        Assert.Contains("account.locale", fields);
        Assert.Contains("account.computerName", fields);
        Assert.Contains("display.guest.accentColor", fields);
        Assert.Contains("display.guest.wallpaper", fields);
        Assert.Contains("compute.processors", fields);
        Assert.Contains("compute.memory.maximumMb", fields);
        Assert.Contains("compute.memory.dynamic", fields);
        Assert.Contains("compute.diskSizeGb", fields);
        Assert.Contains("gpu.share", fields);
        Assert.Contains("backup.target", fields);
        Assert.Contains("install[0]", fields);
    }

    [Fact]
    public void BackupRetention_KeepsAtLeastOne()
    {
        var none = Sample() with { Backup = new MachineBackup(true, "NAS", Retention: new BackupRetention(0, 0, 0)) };

        Assert.Equal("backup.retention", Assert.Single(Assert.Throws<LifecycleValidationException>(() => _validator.Normalize(none)).Errors).Field);
    }

    [Fact]
    public void Windows11WithoutATpm_NeedsTheHardwareBypass()
    {
        var noTpm = Sample() with { Firmware = new MachineFirmware(Tpm: false) };
        var bypassed = noTpm with { Account = noTpm.Account! with { Windows = new WindowsInstallSettings("Windows 11 Pro", BypassHardwareChecks: true) } };

        var error = Assert.Throws<LifecycleValidationException>(() => _validator.Normalize(noTpm));
        Assert.Equal("firmware.tpm", Assert.Single(error.Errors).Field);
        Assert.False(_validator.Normalize(bypassed).Firmware!.Tpm);
    }

    [Fact]
    public void Linux_TakesNoGpuShareOrWindowsSoftware()
    {
        var linux = new MachineProfile("Server", InstallOs.Linux, Gpu: new MachineGpu(true), Install: [new ProfileItem("Git.Git")]);

        var fields = Assert.Throws<LifecycleValidationException>(() => _validator.Normalize(linux)).Errors.Select(issue => issue.Field);

        Assert.Equal(["gpu.enabled", "install"], fields);
    }

    [Fact]
    public void Normalizing_TidiesWithoutChangingMeaning()
    {
        var profile = _validator.Normalize(new MachineProfile(
            "  Tidy  ",
            InstallOs.Windows,
            "   ",
            new MachineAccount(TimeZone: "  "),
            Compute: new MachineCompute(Memory: new MachineMemory(4096, 8192, Dynamic: false)),
            Network: new MachineNetwork("  LAN  "),
            Install: [new ProfileItem("Git.Git"), new ProfileItem("git.git")]));

        Assert.Equal(("Tidy", (string?)null), (profile.Name, profile.Description));
        Assert.Null(profile.Account!.TimeZone);
        Assert.Equal(MachineProfileValidator.DefaultWindowsEdition, profile.Account.Windows!.DefaultEdition);
        Assert.Null(profile.Compute!.Memory!.MaximumMb);
        Assert.Equal("LAN", profile.Network!.Switch);
        Assert.Equal("Git.Git", Assert.Single(profile.Install!).Id);
    }
}
