using System.Text.Json.Nodes;
using HyperHarbor.Host.Core.Installation;
using HyperHarbor.Host.Core.Profiles;
using HyperHarbor.Host.Core.Unattend;
using HyperHarbor.Shared.Contracts;
using HyperHarbor.Shared.Contracts.Profiles;
using HyperHarbor.Shared.Contracts.Unattend;

namespace HyperHarbor.Host.Tests.Profiles;

/// <summary>Data format 4: older profiles become machine profiles, and the older files stay.</summary>
public sealed class ProfileConsolidationTests : IDisposable
{
    private readonly string _data = Directory.CreateTempSubdirectory("hh-consolidate-").FullName;
    private readonly Guid _alice = Guid.NewGuid();
    private readonly Guid _bob = Guid.NewGuid();

    public void Dispose() => Directory.Delete(_data, recursive: true);

    private MachineProfileStore Machines() =>
        new(_data, new MachineProfileValidator(new ProfileValidator(Catalogs.Default), new UnattendProfileStore(_data)));

    [Fact]
    public void EachUsersOlderProfiles_BecomeMachineProfiles_AndTheOlderFilesStay()
    {
        var unattend = new UnattendProfileStore(_data, () => "Central Standard Time");
        unattend.Create(_alice, new UnattendProfileRequest("Dev", InstallOs.Windows, "devadmin", "UTC", "en-GB", new WindowsInstallSettings("Windows 11 Pro", BypassHardwareChecks: true), null));
        var setup = new SetupProfileStore(_data, Catalogs.Default);
        setup.Create(_alice, new SetupProfile("Dev", "Tools", [new ProfileItem("Git.Git")], null, [new ProfileTweak("explorer.showFileExtensions")], null));
        setup.Create(_bob, new SetupProfile("Office", null, [new ProfileItem("7zip")], null, null, null));

        var results = ProfileConsolidation.Run(_data);

        Assert.All(results, result => Assert.Null(result.Error));
        var alice = Machines().List(_alice).Select(summary => Machines().Get(_alice, summary.Id).Profile).ToList();
        var install = alice.Single(profile => profile.Name == "Dev");
        Assert.Equal(new MachineAccount("devadmin", "UTC", "en-GB", null, new WindowsInstallSettings("Windows 11 Pro", BypassHardwareChecks: true), null), install.Account);
        Assert.Null(install.Install);
        var software = alice.Single(profile => profile.Name == "Dev (setup profile)");
        Assert.Equal(("Tools", InstallOs.Windows, "Git.Git"), (software.Description, software.Os, software.Install!.Single().Id));
        Assert.Null(software.Account);
        Assert.Equal("Office", Assert.Single(Machines().List(_bob)).Name);

        Assert.Single(unattend.List(_alice), profile => !profile.BuiltIn);
        Assert.Single(setup.List(_alice));
        var record = JsonNode.Parse(File.ReadAllText(Path.Combine(_data, MachineProfileStore.FolderName, ProfileConsolidation.RecordFileName)))!.AsArray();
        Assert.Equal(3, record.Count);
    }

    [Fact]
    public void AFileThatDoesNotRead_IsRecordedWithItsProblem_AndTheRestConvert()
    {
        var setup = new SetupProfileStore(_data, Catalogs.Default);
        setup.Create(_alice, new SetupProfile("Good", null, [new ProfileItem("Git.Git")], null, null, null));
        File.WriteAllText(Path.Combine(_data, SetupProfileStore.FolderName, _alice.ToString("D"), "broken.yaml"), "name: [unclosed");

        var results = ProfileConsolidation.Run(_data);

        Assert.NotNull(results.Single(result => result.SourceId == "broken").Error);
        Assert.Equal("Good", Assert.Single(Machines().List(_alice)).Name);
    }

    [Fact]
    public void TheBuiltInInstallProfiles_AreNotConverted_ButServedAsTemplates()
    {
        var unattend = new UnattendProfileStore(_data, () => "Central Standard Time");

        Assert.Empty(ProfileConsolidation.Run(_data));
        Assert.False(File.Exists(Path.Combine(_data, MachineProfileStore.FolderName, ProfileConsolidation.RecordFileName)));

        var templates = new MachineProfileTemplates(unattend).List();
        Assert.Equal(["windows-workstation", "windows-burner", "ubuntu-dev-server"], templates.Select(template => template.Id));
        var burner = templates[1].Profile;
        Assert.True(burner.Account!.Windows!.BypassHardwareChecks);
        Assert.Equal("Central Standard Time", burner.Account.TimeZone);
    }

    [Fact]
    public void TheDataFormatMigration_RunsTheConversion_Once()
    {
        File.WriteAllText(Path.Combine(_data, DataFormat.FileName), """{ "format": 3 }""");
        new SetupProfileStore(_data, Catalogs.Default).Create(_alice, new SetupProfile("Office", null, [new ProfileItem("7zip")], null, null, null));

        DataFormat.EnsureCurrent(_data);
        DataFormat.EnsureCurrent(_data);

        Assert.Equal(4, DataFormat.Read(_data));
        Assert.Equal("Office", Assert.Single(Machines().List(_alice)).Name);
    }
}
