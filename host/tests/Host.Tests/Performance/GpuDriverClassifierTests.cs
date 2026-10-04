using HyperHarbor.Host.Core.Performance;
using HyperHarbor.Shared.Contracts.Vms;

namespace HyperHarbor.Host.Tests.Performance;

/// <summary>Driver file lists in the shape Win32_PnPSignedDriverCIMDataFile reports them (lower case paths).</summary>
public sealed class GpuDriverClassifierTests
{
    private const string Windows = @"C:\WINDOWS";

    [Fact]
    public void Intel_CopiesTheDriverStoreFolderOnly()
    {
        string[] files =
        [
            @"C:\WINDOWS\System32\DriverStore\FileRepository\iigd_dch.inf_amd64_6091bde938afd934\igdumdim64.dll",
            @"c:\windows\system32\driverstore\filerepository\iigd_dch.inf_amd64_6091bde938afd934\igd12umd64.dll",
            @"c:\windows\inf\oem9.inf",
        ];

        var package = GpuDriverClassifier.Classify(GpuVendor.Intel, "30.0.101.1122", files, Windows);

        Assert.Equal([@"C:\WINDOWS\System32\DriverStore\FileRepository\iigd_dch.inf_amd64_6091bde938afd934"], package.DriverStoreFolders);
        Assert.Empty(package.WindowsFiles);
        Assert.Empty(package.WindowsFolders);
        Assert.Equal("30.0.101.1122", package.Version);
    }

    [Fact]
    public void Nvidia_AddsSystem32Files_TheNvDlls_AndTheNvidiaCorporationFolder()
    {
        string[] files =
        [
            @"c:\windows\system32\driverstore\filerepository\nv_dispui.inf_amd64_0123456789abcdef\nvldumdx.dll",
            @"c:\windows\system32\driverstore\filerepository\nv_dispui.inf_amd64_0123456789abcdef\nvlddmkm.sys",
            @"c:\windows\system32\nvapi64.dll",
            @"c:\windows\syswow64\nvapi.dll",
            @"c:\program files\nvidia corporation\control panel client\nvcplui.exe",
        ];

        var package = GpuDriverClassifier.Classify(GpuVendor.Nvidia, "32.0.15.6094", files, Windows,
            ["nvcuda.dll", "nvml.dll", "nvapi64.dll"], nvidiaCorporationFolderExists: true);

        Assert.Single(package.DriverStoreFolders);
        Assert.EndsWith(@"FileRepository\nv_dispui.inf_amd64_0123456789abcdef", package.DriverStoreFolders[0], StringComparison.Ordinal);
        Assert.Equal([@"system32\nvapi64.dll", @"System32\nvcuda.dll", @"System32\nvml.dll", @"syswow64\nvapi.dll"], package.WindowsFiles);
        Assert.Equal([@"System32\drivers\NVIDIA Corporation"], package.WindowsFolders);
    }

    [Fact]
    public void Amd_CopiesFoldersAndSystem32FilesFromThePackage()
    {
        string[] files =
        [
            @"c:\windows\system32\driverstore\filerepository\u0401234.inf_amd64_aaaaaaaaaaaaaaaa\amdxc64.dll",
            @"c:\windows\system32\driverstore\filerepository\u0401234.inf_amd64_bbbbbbbbbbbbbbbb\atiumd64.dll",
            @"c:\windows\system32\amdihk64.dll",
        ];

        var package = GpuDriverClassifier.Classify(GpuVendor.Amd, "31.0.24027.1012", files, Windows, ["nvnotthere.dll"]);

        Assert.Equal(2, package.DriverStoreFolders.Count);
        Assert.Equal([@"system32\amdihk64.dll"], package.WindowsFiles);
        Assert.Empty(package.WindowsFolders);
    }

    [Fact]
    public void OtherVendors_AndPackagesWithoutDriverStoreFiles_AreRefused()
    {
        Assert.Throws<GpuDriverException>(() => GpuDriverClassifier.Classify(GpuVendor.Other, "1.0", [], Windows));
        Assert.Throws<GpuDriverException>(() => GpuDriverClassifier.Classify(GpuVendor.Intel, "1.0", [@"c:\windows\system32\igfx.dll"], Windows));
    }
}
