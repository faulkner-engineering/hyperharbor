using System.Text;
using DiscUtils.Iso9660;
using HyperHarbor.Host.Core.Unattend;
using HyperHarbor.Shared.Contracts.Unattend;

namespace HyperHarbor.Host.Tests.Unattend;

public sealed class SeedIsoAndInspectorTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "hyperharbor-tests", Guid.NewGuid().ToString("N"));

    public SeedIsoAndInspectorTests() => Directory.CreateDirectory(_directory);

    public void Dispose() => Directory.Delete(_directory, recursive: true);

    [Fact]
    public void WindowsSeed_HasAutounattendAtTheRootWithItsCaseKept()
    {
        var path = SeedIso.PathFor(Path.Combine(_directory, "Dev Box.vhdx"));
        var install = new WindowsInstall(
            new UnattendProfile("windows-burner", "Windows Burner", true, InstallOs.Windows, "hhadmin", "UTC", "en-US", new WindowsInstallSettings("Windows 11 Pro"), null),
            "Windows 11 Pro", "DEV-BOX", "One-Time-Pass1!", "hh-owner");

        SeedIso.ForWindows(path, install);

        using var stream = File.OpenRead(path);
        using var reader = new CDReader(stream, joliet: true);
        Assert.Equal(SeedIso.WindowsVolumeLabel, reader.VolumeLabel);
        Assert.Contains(reader.GetFiles(@"\"), file => file.TrimStart('\\') == "Autounattend.xml");
        Assert.Equal(AutounattendBuilder.Build(install), Encoding.UTF8.GetString(reader.ReadAllBytes("Autounattend.xml")));
    }

    [Fact]
    public void LinuxSeed_IsACidataVolumeWithUserDataAndMetaData()
    {
        var path = SeedIso.PathFor(Path.Combine(_directory, "Dev Box.vhdx"));
        var install = new LinuxInstall(
            new UnattendProfile("ubuntu-dev-server", "Ubuntu Dev Server", true, InstallOs.Linux, "hhadmin", "Etc/UTC", "en-US", null, new LinuxInstallSettings()),
            "dev-box", Sha512Crypt.Hash("One-Time-Pass1!"));

        SeedIso.ForLinux(path, install);

        using var stream = File.OpenRead(path);
        using var reader = new CDReader(stream, joliet: true);
        Assert.Equal("CIDATA", reader.VolumeLabel);
        Assert.Equal("CIDATA", PrimaryLabel(path));
        Assert.Equal(CloudInitBuilder.UserData(install), Read(reader, "user-data"));
        Assert.Equal(CloudInitBuilder.MetaData(install), Read(reader, "meta-data"));
    }

    [Fact]
    public void Inspector_ReadsWindowsEditionsFromInstallWim()
    {
        var iso = BuildIso("windows.iso", new Dictionary<string, byte[]>
        {
            [@"sources\install.wim"] = TestIsos.Wim("Windows 11 Home", "Windows 11 Pro"),
            ["setup.exe"] = [0],
        });

        var inspection = new IsoInspector().Inspect(iso);

        Assert.Equal(InstallOs.Windows, inspection.Os);
        Assert.Equal(["Windows 11 Home", "Windows 11 Pro"], inspection.Editions);
    }

    [Fact]
    public void Inspector_RecognizesUbuntuInstallers()
    {
        var iso = BuildIso("ubuntu.iso", new Dictionary<string, byte[]>
        {
            [@"casper\vmlinuz"] = [0],
            [@".disk\info"] = Encoding.UTF8.GetBytes("Ubuntu-Server 24.04.1 LTS \"Noble Numbat\" - Release amd64 (20240827)\n"),
        });

        var inspection = new IsoInspector().Inspect(iso);

        Assert.Equal(InstallOs.Linux, inspection.Os);
        Assert.StartsWith("Ubuntu-Server 24.04.1 LTS", inspection.Distribution, StringComparison.Ordinal);
        Assert.Empty(inspection.Editions);
    }

    [Fact]
    public void Inspector_ReportsOtherImagesAsUnknown()
    {
        var iso = BuildIso("other.iso", new Dictionary<string, byte[]> { ["readme.txt"] = [1, 2, 3] });
        var notAnIso = Path.Combine(_directory, "random.iso");
        File.WriteAllBytes(notAnIso, new byte[64 * 1024]);

        Assert.Null(new IsoInspector().Inspect(iso).Os);
        Assert.Null(new IsoInspector().Inspect(notAnIso).Os);
    }

    [Fact]
    public void WimEditions_IgnoresFilesThatAreNotWims()
    {
        Assert.Empty(IsoInspector.WimEditions(new MemoryStream(new byte[512])));
    }

    /// <summary>Opt-in: HH_WINDOWS_ISO=path to a real Windows ISO (UDF only, several gigabytes). Read only.</summary>
    [EnvironmentFact("HH_WINDOWS_ISO")]
    public void Inspector_ReadsARealWindowsIso()
    {
        var inspection = new IsoInspector().Inspect(Environment.GetEnvironmentVariable("HH_WINDOWS_ISO")!);

        Assert.Equal(InstallOs.Windows, inspection.Os);
        Assert.NotEmpty(inspection.Editions);
        Assert.All(inspection.Editions, edition => Assert.StartsWith("Windows", edition, StringComparison.Ordinal));
    }

    /// <summary>Joliet names without an extension come back from DiscUtils with a trailing period; Linux strips it.</summary>
    private static string Read(CDReader reader, string name) =>
        Encoding.UTF8.GetString(reader.ReadAllBytes(reader.FileExists(name) ? name : name + "."));

    /// <summary>The ISO 9660 (non-Joliet) volume label.</summary>
    private static string PrimaryLabel(string path)
    {
        using var stream = File.OpenRead(path);
        using var reader = new CDReader(stream, joliet: false);
        return reader.VolumeLabel ?? string.Empty;
    }

    private string BuildIso(string name, Dictionary<string, byte[]> files)
    {
        var iso = Path.Combine(_directory, name);
        TestIsos.Write(iso, files);
        return iso;
    }
}
