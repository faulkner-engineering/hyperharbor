using HyperHarbor.Host.Core.Lifecycle;
using HyperHarbor.Host.Core.Performance;
using HyperHarbor.Shared.Contracts.Vms;

namespace HyperHarbor.Host.Tests.Performance;

public sealed class GpuIdentityTests
{
    [Theory]
    [InlineData(@"PCI\VEN_10DE&DEV_2704&SUBSYS_00000000&REV_A1\4&2", GpuVendor.Nvidia)]
    [InlineData(@"pci\ven_1002&dev_744c", GpuVendor.Amd)]
    [InlineData(@"PCI\VEN_8086&DEV_9B41", GpuVendor.Intel)]
    [InlineData(@"PCI\VEN_1414&DEV_008E", GpuVendor.Other)]
    [InlineData(null, GpuVendor.Other)]
    public void Vendor_ComesFromThePciVendorId(string? pnp, GpuVendor expected)
    {
        Assert.Equal(expected, GpuIdentity.VendorOf(pnp));
    }

    [Fact]
    public void PartitionableGpuNames_MapToTheirDeviceInstanceId()
    {
        Assert.Equal(
            @"PCI\VEN_8086&DEV_9B41&SUBSYS_22BE17AA&REV_02\3&11583659&0&10",
            GpuIdentity.DeviceInstanceId(FakeHostGpuReader.IntelPath));
        Assert.Null(GpuIdentity.DeviceInstanceId("not a device path"));
    }

    [Theory]
    [InlineData(1_000_000_000UL, 50, 500_000_000UL)]
    [InlineData(1_000_000_000UL, 1, 10_000_000UL)]
    [InlineData(1_000_000_000UL, 100, 1_000_000_000UL)]
    [InlineData(ulong.MaxValue, 100, ulong.MaxValue)]
    [InlineData(ulong.MaxValue, 50, 9_223_372_036_854_775_807UL)]
    public void Shares_AreComputedWithoutOverflow(ulong maximum, int percent, ulong expected)
    {
        Assert.Equal(expected, GpuIdentity.Share(maximum, percent));
    }

    /// <summary>The HostResource Add-VMGpuPartitionAdapter -InstancePath stored on this host, 2026-10-04.</summary>
    [Fact]
    public void PartitionableGpuPath_IsTheWmiObjectPath_AsHyperVStoresIt()
    {
        const string name = @"\\?\PCI#VEN_8086&DEV_9B41&SUBSYS_22BE17AA&REV_02#3&11583659&0&10#{064092b3-625e-43bf-9eb5-dc845897dd59}\GPUPARAV";

        Assert.Equal(
            @"\\DESKTOP-65QRD0H\root\virtualization\v2:Msvm_PartitionableGpu.CreationClassName=""Msvm_PartitionableGpu"",Name=""\\\\?\\PCI#VEN_8086&DEV_9B41&SUBSYS_22BE17AA&REV_02#3&11583659&0&10#{064092b3-625e-43bf-9eb5-dc845897dd59}\\GPUPARAV""",
            GpuIdentity.PartitionableGpuPath(name, "DESKTOP-65QRD0H"));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(101)]
    public void SharesOutsideOneToHundred_AreRejected(int percent)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => GpuIdentity.Share(100, percent));
    }
}

public sealed class PerformanceValidatorTests
{
    private static readonly HostCapacity Host = new(16, 65536, 40000);
    private static readonly IReadOnlyList<HostGpuInfo> Gpus = new FakeHostGpuReader().Gpus;

    private static LifecycleValidationException Invalid(PerformanceSettings settings) =>
        Assert.Throws<LifecycleValidationException>(() => PerformanceValidator.Check(settings, Gpus, Host, 4096));

    [Fact]
    public void Defaults_AreFilledIn()
    {
        var (settings, plan) = PerformanceValidator.Check(new PerformanceSettings(4, 8192), Gpus, Host, 4096);

        Assert.Equal(new MmioSettings(1024, 32768), settings.Mmio);
        Assert.Equal(50, settings.Gpu!.VramPercent);
        Assert.Equal(FakeHostGpuReader.IntelPath, settings.Gpu.InstancePath);
        Assert.False(settings.Rdp!.HardwareEncoding);
        Assert.Equal(500_000_000UL, plan.Gpu.Vram);
        Assert.Null(plan.Gpu.InstancePath);
    }

    [Theory]
    [InlineData(0, 8192, "processorCount")]
    [InlineData(64, 8192, "processorCount")]
    [InlineData(4, 1, "memoryMb")]
    public void ProcessorsAndMemory_AreCheckedLikeComputeSettings(int processors, long memoryMb, string field)
    {
        Assert.Contains(Invalid(new PerformanceSettings(processors, memoryMb)).Errors, issue => issue.Field == field);
    }

    [Fact]
    public void SharesMmioAndFolders_AreChecked()
    {
        var error = Invalid(new PerformanceSettings(4, 8192,
            new GpuPartitionShare(VramPercent: 0, EncodePercent: 101),
            new MmioSettings(64, 512),
            MoveStorageTo: "relative\folder"));

        var fields = error.Errors.Select(issue => issue.Field).ToList();
        Assert.Contains("gpu.vramPercent", fields);
        Assert.Contains("gpu.encodePercent", fields);
        Assert.Contains("mmio.lowGapMb", fields);
        Assert.Contains("mmio.highGapMb", fields);
        Assert.Contains("moveStorageTo", fields);
    }

    [Fact]
    public void AnUnknownGpu_IsAFieldError()
    {
        Assert.Contains(Invalid(new PerformanceSettings(4, 8192, new GpuPartitionShare(@"\?\PCI#VEN_10DE#1#{x}\GPUPARAV"))).Errors,
            issue => issue.Field == "gpu.instancePath");
    }

    [Fact]
    public void MemoryAboveTheReserve_WarnsUntilAcknowledged()
    {
        Assert.Throws<ResourceWarningsException>(() => PerformanceValidator.Check(new PerformanceSettings(4, 60000), Gpus, Host, 4096));

        var (settings, _) = PerformanceValidator.Check(new PerformanceSettings(4, 60000, AcknowledgeWarnings: true), Gpus, Host, 4096);
        Assert.False(settings.AcknowledgeWarnings);
    }
}
