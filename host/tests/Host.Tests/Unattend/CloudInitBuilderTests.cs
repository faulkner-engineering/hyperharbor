using HyperHarbor.Host.Core.Unattend;
using HyperHarbor.Shared.Contracts.Unattend;
using YamlDotNet.RepresentationModel;

namespace HyperHarbor.Host.Tests.Unattend;

public sealed class CloudInitBuilderTests
{
    private const string Key = "ssh-ed25519 AAAAC3NzaC1lZDI1NTE5AAAAIDk4Zk2uK7xVQbV3mN6PZ8o2jQfV0uKZp1Gk3m8rT0aB user@laptop";

    private static LinuxInstall Install(LinuxInstallSettings? settings = null) => new(
        new UnattendProfile("ubuntu-dev-server", "Ubuntu Dev Server", true, InstallOs.Linux, "hhadmin", "America/Chicago", "en-US", null,
            settings ?? new LinuxInstallSettings([Key], ["git", "build-essential"], InstallDesktop: true)),
        "dev-box",
        Sha512Crypt.Hash("One-Time-Pass1!"));

    private static YamlMappingNode Autoinstall(string userData)
    {
        Assert.StartsWith("#cloud-config\n", userData, StringComparison.Ordinal);
        var stream = new YamlStream();
        stream.Load(new StringReader(userData));
        return (YamlMappingNode)((YamlMappingNode)stream.Documents[0].RootNode)["autoinstall"];
    }

    private static IReadOnlyList<string> Sequence(YamlNode node) =>
        ((YamlSequenceNode)node).Children.Select(child => ((YamlScalarNode)child).Value!).ToList();

    [Fact]
    public void UserData_IsAnAutoinstallWithAHashedPasswordAndSsh()
    {
        var install = Install();
        var autoinstall = Autoinstall(CloudInitBuilder.UserData(install));

        Assert.Equal("1", ((YamlScalarNode)autoinstall["version"]).Value);
        Assert.Equal("en_US.UTF-8", ((YamlScalarNode)autoinstall["locale"]).Value);
        Assert.Equal("America/Chicago", ((YamlScalarNode)autoinstall["timezone"]).Value);
        var identity = (YamlMappingNode)autoinstall["identity"];
        Assert.Equal("hhadmin", ((YamlScalarNode)identity["username"]).Value);
        Assert.Equal("dev-box", ((YamlScalarNode)identity["hostname"]).Value);
        Assert.Equal(install.AdminPasswordHash, ((YamlScalarNode)identity["password"]).Value);
        var ssh = (YamlMappingNode)autoinstall["ssh"];
        Assert.Equal("true", ((YamlScalarNode)ssh["install-server"]).Value);
        Assert.Equal("true", ((YamlScalarNode)ssh["allow-pw"]).Value);
        Assert.Equal([Key], Sequence(ssh["authorized-keys"]));
    }

    [Fact]
    public void Packages_IncludeHyperVToolsTheDesktopAndTheProfilesOwn()
    {
        var packages = Sequence(Autoinstall(CloudInitBuilder.UserData(Install()))["packages"]);

        Assert.Equal(CloudInitBuilder.HyperVToolsPackage, packages[0]);
        Assert.Contains("xrdp", packages);
        Assert.Contains("git", packages);
        Assert.Contains("build-essential", packages);
    }

    [Fact]
    public void WithoutDesktopOrKeys_LeavesThemOut()
    {
        var autoinstall = Autoinstall(CloudInitBuilder.UserData(Install(new LinuxInstallSettings([], [], InstallDesktop: false))));

        Assert.Equal([CloudInitBuilder.HyperVToolsPackage], Sequence(autoinstall["packages"]));
        Assert.False(((YamlMappingNode)autoinstall["ssh"]).Children.ContainsKey("authorized-keys"));
    }

    [Fact]
    public void ValuesWithQuotesStayScalarsAndLineBreaksAreRefused()
    {
        var quoted = Install(new LinuxInstallSettings(["ssh-ed25519 AAAA it's mine"], [], false));
        Assert.Equal(["ssh-ed25519 AAAA it's mine"], Sequence(((YamlMappingNode)Autoinstall(CloudInitBuilder.UserData(quoted))["ssh"])["authorized-keys"]));

        var broken = Install(new LinuxInstallSettings(["ssh-ed25519 AAAA\nruncmd: [rm]"], [], false));
        Assert.Throws<ArgumentException>(() => CloudInitBuilder.UserData(broken));
    }

    [Fact]
    public void PlaintextPasswordIsRefused()
    {
        var install = Install() with { AdminPasswordHash = "One-Time-Pass1!" };

        Assert.Throws<ArgumentException>(() => CloudInitBuilder.UserData(install));
    }

    [Fact]
    public void MetaData_NamesTheInstance()
    {
        Assert.Equal("instance-id: 'hyperharbor-dev-box'\nlocal-hostname: 'dev-box'\n", CloudInitBuilder.MetaData(Install()));
    }

    [Theory]
    [InlineData("Dev Box", "dev-box")]
    [InlineData("Ubuntu_24.04 (test)", "ubuntu-24-04-test")]
    [InlineData("***", "hyperharbor-vm")]
    public void HostNames_AreDnsSafe(string vmName, string expected)
    {
        Assert.Equal(expected, CloudInitBuilder.HostName(vmName));
    }
}
