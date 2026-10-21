using HyperHarbor.Host.Core.Backup;
using HyperHarbor.Host.Core.Lifecycle;
using HyperHarbor.Host.Core.Transfer;
using HyperHarbor.Host.Tests.Lifecycle;
using HyperHarbor.Shared.Contracts.Hosts;
using HyperHarbor.Shared.Contracts.Vms;

namespace HyperHarbor.Host.Tests.Transfer;

public sealed class TransferPreflightTests
{
    private const string Root = @"E:\VMs";
    private const string ExternalId = "1d6e5b3c-0000-4000-8000-000000000001";
    private static readonly Guid VmId = Guid.Parse("0b9a6f53-1c2d-4e8f-a1b2-3c4d5e6f7a8b");

    private static readonly IReadOnlyList<VirtualSwitch> Switches =
    [
        new(CimHyperVHost.DefaultSwitchId, "Default Switch", true),
        new(ExternalId, "External", false),
    ];

    [Fact]
    public void Move_KeepsTheIdNameAndMac_AndPlacesTheVmInItsOwnFolder()
    {
        var plan = TransferPreflight.Evaluate(Preview(), TransferMode.Move, Facts());

        Assert.True(plan.CanAccept, string.Join(" ", plan.Errors.Select(error => error.Message)));
        Assert.Empty(plan.Warnings);
        Assert.Equal(VmId, plan.VmId);
        Assert.Equal("Web", plan.Name);
        Assert.Equal(@"E:\VMs\Web", plan.VmFolder);
        Assert.Equal(
            [@"E:\VMs\Web\Virtual Hard Disks\Web.vhdx", @"E:\VMs\Web\Virtual Hard Disks\Data.vhdx"],
            plan.Disks.Select(disk => disk.TargetPath));
        Assert.Equal(new TransferAdapterPlan(ExternalId, "00155D010203"), Assert.Single(plan.Adapters));
    }

    [Fact]
    public void Move_IsRefused_WhenThisHostAlreadyHasTheVmsId()
    {
        var plan = TransferPreflight.Evaluate(Preview(), TransferMode.Move, Facts(existing: [(VmId, "Web")]));

        Assert.False(plan.CanAccept);
        Assert.Equal("vmId", Assert.Single(plan.Errors).Field);
    }

    [Fact]
    public void Copy_OfAVmThisHostAlreadyHas_GetsANewIdDynamicMacsAndACopyName()
    {
        var plan = TransferPreflight.Evaluate(Preview(), TransferMode.Copy, Facts(existing: [(VmId, "Web")]));

        Assert.True(plan.CanAccept);
        Assert.Null(plan.VmId);
        Assert.Equal("Web (copy)", plan.Name);
        Assert.Equal(@"E:\VMs\Web (copy)", plan.VmFolder);
        Assert.Null(Assert.Single(plan.Adapters).StaticMacAddress);
        Assert.Contains(plan.Warnings, warning => warning.Field == "identity");
        Assert.Contains(plan.Warnings, warning => warning.Field == "name");
    }

    [Fact]
    public void Move_ToAHostWithAnotherVmOfTheSameName_IsRenamed()
    {
        var plan = TransferPreflight.Evaluate(Preview(), TransferMode.Move, Facts(existing: [(Guid.NewGuid(), "WEB")]));

        Assert.True(plan.CanAccept);
        Assert.Equal("Web (moved)", plan.Name);
        Assert.Equal("name", Assert.Single(plan.Warnings).Field);
    }

    [Fact]
    public void ExistingFolder_IsNeverReused()
    {
        var plan = TransferPreflight.Evaluate(Preview(), TransferMode.Move, Facts(folders: ["Web", "Web (moved)"]));

        Assert.Equal("Web (moved 2)", plan.Name);
        Assert.Equal(@"E:\VMs\Web (moved 2)", plan.VmFolder);
    }

    [Fact]
    public void NewerConfigurationVersion_IsRefused_NamingTheNewestSupported()
    {
        var plan = TransferPreflight.Evaluate(Preview() with { ConfigurationVersion = "12.0" }, TransferMode.Move,
            Facts(versions: ["8.0", "9.0", "10.0", "11.0"]));

        var error = Assert.Single(plan.Errors);
        Assert.Equal("configurationVersion", error.Field);
        Assert.Contains("11.0", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void UnknownConfigurationVersion_IsRefused()
    {
        var plan = TransferPreflight.Evaluate(Preview() with { ConfigurationVersion = null }, TransferMode.Move, Facts());

        Assert.Equal("configurationVersion", Assert.Single(plan.Errors).Field);
    }

    [Fact]
    public void MoreProcessorsThanThisHostHas_IsRefused()
    {
        var plan = TransferPreflight.Evaluate(Preview(), TransferMode.Move, Facts(capacity: new HostCapacity(2, 32768, 20000)));

        Assert.Equal("processorCount", Assert.Single(plan.Errors).Field);
    }

    [Fact]
    public void TooLittleFreeSpace_IsRefused_AndUnknownSpaceIsNot()
    {
        // 25 GB of disks plus the margin.
        var tight = TransferPreflight.Evaluate(Preview(), TransferMode.Move, Facts(freeMb: 25 * 1024 + 100));
        var unknown = TransferPreflight.Evaluate(Preview(), TransferMode.Move, Facts(freeMb: null));

        Assert.Equal("space", Assert.Single(tight.Errors).Field);
        Assert.True(unknown.CanAccept);
    }

    [Fact]
    public void DisksWithTheSameFileName_AreNumbered()
    {
        var preview = Preview() with
        {
            Disks = [Disk(@"D:\A\Disk.vhdx", 1), Disk(@"D:\B\disk.vhdx", 1), Disk(@"D:\C\Disk.vhdx", 1)],
        };

        var plan = TransferPreflight.Evaluate(preview, TransferMode.Move, Facts());

        Assert.Equal(["Disk.vhdx", "disk (2).vhdx", "Disk (3).vhdx"],plan.Disks.Select(disk => Path.GetFileName(disk.TargetPath)));
    }

    [Theory]
    [InlineData(@"D:\VMs\Web_ABC.avhdx")]
    [InlineData(@"D:\VMs\Windows.iso")]
    [InlineData(@"D:\VMs\disk.vhdx.")]
    public void DiskThatIsNotAPlainVirtualHardDisk_IsRefused(string path)
    {
        var plan = TransferPreflight.Evaluate(Preview() with { Disks = [Disk(path, 1)] }, TransferMode.Move, Facts());

        Assert.Equal("disks", Assert.Single(plan.Errors).Field);
    }

    [Fact]
    public void InvalidName_IsRefused()
    {
        var plan = TransferPreflight.Evaluate(Preview() with { Name = "CON" }, TransferMode.Move, Facts());

        Assert.Equal("name", Assert.Single(plan.Errors).Field);
    }

    [Fact]
    public void MissingSwitch_FallsBackToTheDefaultSwitch_WithAWarning()
    {
        var preview = Preview() with { Adapters = [new VmAdapterDefinition("gone", "Lab", "00155D010203"), new VmAdapterDefinition(null, null, null)] };

        var plan = TransferPreflight.Evaluate(preview, TransferMode.Move, Facts());

        Assert.Equal(CimHyperVHost.DefaultSwitchId, plan.Adapters[0].SwitchId);
        Assert.Null(plan.Adapters[1].SwitchId);
        Assert.Contains(plan.Warnings, warning => warning.Field == "network" && warning.Message.Contains("Lab", StringComparison.Ordinal));
        Assert.Contains(plan.Warnings, warning => warning.Field == "network" && warning.Message.Contains("adapter 2", StringComparison.Ordinal));
    }

    [Fact]
    public void SourceBlockers_AreErrors_AndSourceWarningsArePassedOn()
    {
        var preview = Preview() with
        {
            Blockers = [new TransferBlocker(TransferBlockerCode.Checkpoints, "Web has 1 checkpoint(s).")],
            Warnings = ["The GPU partition is not transferred as it is."],
        };

        var plan = TransferPreflight.Evaluate(preview, TransferMode.Move, Facts());

        Assert.Equal("Web has 1 checkpoint(s).", Assert.Single(plan.Errors).Message);
        Assert.Contains(plan.Warnings, warning => warning.Message.StartsWith("The GPU partition", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("00155D010203", "00155D010203")]
    [InlineData("00-15-5d-01-02-03", "00155D010203")]
    [InlineData("00:15:5D:01:02:03", "00155D010203")]
    [InlineData("000000000000", null)]
    [InlineData("01005E000001", null)]
    [InlineData("00155D01020", null)]
    [InlineData("", null)]
    [InlineData(null, null)]
    public void NormalizeMac_KeepsOnlyUsableUnicastAddresses(string? input, string? expected)
    {
        Assert.Equal(expected, TransferPreflight.NormalizeMac(input));
    }

    [Theory]
    [InlineData("12.0", true)]
    [InlineData("12.00", true)]
    [InlineData("9.0", true)]
    [InlineData("12.1", false)]
    [InlineData("13.0", false)]
    [InlineData("12", false)]
    public void Capabilities_CompareVersionsAsNumbers(string version, bool supported)
    {
        var capabilities = new HostVersionCapabilities(["8.0", "9.0", "12.0", "10.0"]);

        Assert.Equal(supported, capabilities.Supports(version));
        Assert.Equal("12.0", capabilities.Newest);
    }

    [Theory]
    [InlineData(@"F:\HyperHarbor VMs", @"F:\HyperHarbor VMs\Web")]
    [InlineData(null, @"C:\Hyper-V\Config\Web")]
    public async Task Service_UsesTheChosenVmFolder_ElseHyperVsDefaultConfigurationFolder(string? root, string expected)
    {
        var hyperV = new FakeHyperVHost();
        var options = new LifecycleOptions { VmRootFolder = root };
        var service = new TransferPreflightService(
            new FakeVmInventory(), new VmStorageLocation(null, options, hyperV), hyperV, new FakeHostCapacity(), new FakeDiskFiles(),
            new FakeHyperVVersions(), options, _ => []);

        var plan = await service.CheckAsync(Preview(), TransferMode.Move, CancellationToken.None);

        Assert.True(plan.CanAccept, string.Join(" ", plan.Errors.Select(error => error.Message)));
        Assert.Equal(expected, plan.VmFolder);
    }

    private static TransferDisk Disk(string path, long gigabytes) => new(path, gigabytes << 30, "scsi", 0, 0);

    private static TransferPreview Preview() => new(
        VmId, "Web", VmState.Off, 2, "12.0", new ComputeState(4, 4096, 8192, true, false, false, 1), true,
        CimHyperVBuilder.MicrosoftWindowsTemplateId, true, false,
        [new VmAdapterDefinition("other-host-switch-id", "External", "00155D010203")],
        [Disk(@"D:\VMs\Web\Virtual Hard Disks\Web.vhdx", 20), Disk(@"D:\VMs\Web\Virtual Hard Disks\Data.vhdx", 5)],
        0, [], []);

    private static TransferTargetFacts Facts(
        IReadOnlyList<(Guid, string)>? existing = null,
        IReadOnlyList<string>? folders = null,
        long? freeMb = 500 * 1024,
        HostCapacity? capacity = null,
        IReadOnlyList<string>? versions = null) => new(
        existing ?? [],
        Root,
        folders ?? [],
        freeMb,
        capacity ?? new FakeHostCapacity().Capacity,
        4096,
        new HostVersionCapabilities(versions ?? ["8.0", "9.0", "10.0", "11.0", "12.0"]),
        Switches);
}
