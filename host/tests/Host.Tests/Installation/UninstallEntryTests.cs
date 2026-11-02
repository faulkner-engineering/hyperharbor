using HyperHarbor.Host.Core.Installation;
using HyperHarbor.Host.Service.Installation;
using Microsoft.Win32;

namespace HyperHarbor.Host.Tests.Installation;

/// <summary>
/// The Installed apps entry that winget reads: the values, and writing and updating them in a key under HKCU (the real
/// entry is under HKLM, which needs elevation). The layout uses real junctions in a temporary folder.
/// </summary>
public sealed class UninstallEntryTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "hyperharbor-tests", Guid.NewGuid().ToString("N"));
    private readonly string _keyPath = $@"Software\HyperHarborTests\{Guid.NewGuid():N}";
    private readonly InstallLayout _layout;

    public UninstallEntryTests()
    {
        Directory.CreateDirectory(_root);
        _layout = new InstallLayout(Path.Combine(_root, "HyperHarbor"));
    }

    public void Dispose()
    {
        Registry.CurrentUser.DeleteSubKeyTree(_keyPath, throwOnMissingSubKey: false);
        foreach (var link in Directory.EnumerateDirectories(_root, "*", SearchOption.AllDirectories).Where(Junction.IsJunction).ToList())
        {
            Junction.Delete(link);
        }

        Directory.Delete(_root, recursive: true);
    }

    [Fact]
    public void Values_HaveWhatWingetAndSettingsNeed()
    {
        var layout = new InstallLayout(@"C:\Program Files\HyperHarbor");

        var values = UninstallEntry.Values(layout, SemanticVersion.Parse("0.2.6"));

        Assert.Equal("HyperHarbor Host", values["DisplayName"]);
        Assert.Equal("0.2.6", values["DisplayVersion"]);
        Assert.Equal("Faulkner Engineering LLC", values["Publisher"]);
        Assert.Equal(@"C:\Program Files\HyperHarbor", values["InstallLocation"]);
        Assert.Equal(@"C:\Program Files\HyperHarbor\current\HyperHarbor.Host.exe,0", values["DisplayIcon"]);
        Assert.Equal("https://faulkner.engineering/hyperharbor", values["URLInfoAbout"]);
        Assert.Equal("\"C:\\Program Files\\HyperHarbor\\current\\HyperHarbor.Host.exe\" uninstall", values["UninstallString"]);
        Assert.Equal("\"C:\\Program Files\\HyperHarbor\\current\\HyperHarbor.Host.exe\" uninstall --quiet", values["QuietUninstallString"]);
        Assert.Equal(1, values["NoModify"]);
        Assert.Equal(1, values["NoRepair"]);

        // The executable is not there, so no size is claimed.
        Assert.False(values.ContainsKey("EstimatedSize"));
    }

    [Fact]
    public void ProductCode_IsTheServiceName_WhichTheWingetManifestNames()
    {
        Assert.Equal("HyperHarborHost", UninstallEntry.ProductCode);
    }

    [Fact]
    public void QuietUninstallString_ParsesAsAQuietUninstall()
    {
        var command = (string)UninstallEntry.Values(_layout, SemanticVersion.Parse("1.0.0"))["QuietUninstallString"];
        var arguments = command[(command.LastIndexOf('"') + 2)..].Split(' ');

        var (mode, rest) = HostCommandLine.Parse(arguments, startedFromConsole: false);

        Assert.Equal(HostMode.Uninstall, mode);
        Assert.True(InstallCommand.Options.Parse(rest).Quiet);
        Assert.False(InstallCommand.Options.Parse(rest).RemoveData);
    }

    [Fact]
    public void Write_StoresStringsAndDwords_WithTheExecutableSize()
    {
        var version = Install("0.2.5", bytes: 4096);

        UninstallEntry.Write(Registry.CurrentUser, _keyPath, _layout, version);

        using var key = Registry.CurrentUser.OpenSubKey(_keyPath)!;
        Assert.Equal("0.2.5", key.GetValue("DisplayVersion"));
        Assert.Equal(RegistryValueKind.String, key.GetValueKind("QuietUninstallString"));
        Assert.Equal(RegistryValueKind.DWord, key.GetValueKind("NoModify"));
        Assert.Equal(RegistryValueKind.DWord, key.GetValueKind("NoRepair"));
        Assert.Equal(4, key.GetValue("EstimatedSize"));
    }

    [Fact]
    public void UpdateVersion_FollowsTheCurrentJunction_AfterASelfUpdate()
    {
        UninstallEntry.Write(Registry.CurrentUser, _keyPath, _layout, Install("0.2.5", bytes: 1024));
        Install("0.2.6", bytes: 2048);

        var written = UninstallEntry.UpdateVersion(Registry.CurrentUser, _keyPath, _layout);

        Assert.Equal(SemanticVersion.Parse("0.2.6"), written);
        using var key = Registry.CurrentUser.OpenSubKey(_keyPath)!;
        Assert.Equal("0.2.6", key.GetValue("DisplayVersion"));
        Assert.Equal(2, key.GetValue("EstimatedSize"));
        Assert.Equal("Faulkner Engineering LLC", key.GetValue("Publisher"));
    }

    [Fact]
    public void UpdateVersion_DoesNothing_WithoutAnEntry()
    {
        Install("0.2.6", bytes: 1024);

        Assert.Null(UninstallEntry.UpdateVersion(Registry.CurrentUser, _keyPath, _layout));
        Assert.Null(Registry.CurrentUser.OpenSubKey(_keyPath));
    }

    /// <summary>Stages a stand-in executable of <paramref name="bytes"/> as <paramref name="version"/> and makes it current.</summary>
    private SemanticVersion Install(string version, int bytes)
    {
        var source = Path.Combine(_root, $"source-{version}.exe");
        File.WriteAllBytes(source, new byte[bytes]);
        var parsed = SemanticVersion.Parse(version);
        _layout.Stage(source, parsed);
        _layout.Activate(parsed);
        return parsed;
    }
}
