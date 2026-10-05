using HyperHarbor.Host.Service.Installation;

namespace HyperHarbor.Host.Tests.Installation;

public sealed class HostCommandLineTests
{
    [Fact]
    public void NoArguments_FromExplorer_OpensTheLauncher_AndFromATerminal_RunsTheHost()
    {
        Assert.Equal(HostMode.Launcher, HostCommandLine.Parse([], startedFromConsole: false).Mode);
        Assert.Equal(HostMode.Host, HostCommandLine.Parse([], startedFromConsole: true).Mode);
    }

    [Theory]
    [InlineData(nameof(HostMode.Tray), "--tray")]
    [InlineData(nameof(HostMode.Install), "install", "--port", "48444")]
    [InlineData(nameof(HostMode.Uninstall), "uninstall", "--remove-data")]
    [InlineData(nameof(HostMode.SaveWakeDiagnostics), "save-wake-diagnostics", "C:\\temp")]
    [InlineData(nameof(HostMode.Help), "--help")]
    [InlineData(nameof(HostMode.UpdateRun), "update-run")]
    [InlineData(nameof(HostMode.SelfTest), "--self-test", "C:\\data", "C:\\result.json")]
    [InlineData(nameof(HostMode.ApplyWakeFixes), "--apply-wake-fixes", "wol-nic,wol-sleep")]
    [InlineData(nameof(HostMode.ConsoleSetup), "--setup-console")]
    [InlineData(nameof(HostMode.ConsoleSetup), "--remove-console", "C:\\data", "C:\\result.txt")]
    [InlineData(nameof(HostMode.ListVms), "--DataDirectory", "C:\\data", "--list-vms")]
    [InlineData(nameof(HostMode.Host), "--Api:Port=48444")]
    [InlineData(nameof(HostMode.Host), "--DataDirectory", "C:\\data")]
    public void Arguments_ChooseTheMode(string expected, params string[] args)
    {
        Assert.Equal(Enum.Parse<HostMode>(expected), HostCommandLine.Parse(args, startedFromConsole: false).Mode);
    }

    [Fact]
    public void Verbs_AreRemovedFromTheArgumentsThatRemain()
    {
        Assert.Equal(["--Api:Port=48444"], HostCommandLine.Parse(["run", "--Api:Port=48444"], startedFromConsole: true).Arguments);
        Assert.Equal(["--remove-data"], HostCommandLine.Parse(["uninstall", "--remove-data"], startedFromConsole: true).Arguments);
    }

    [Fact]
    public void Helpers_KeepTheirFullArgumentList()
    {
        string[] args = ["--apply-wake-fixes", "wol-nic", "C:\\result.json"];

        Assert.Equal(args, HostCommandLine.Parse(args, startedFromConsole: false).Arguments);
    }

    [Fact]
    public void InstallOptions_ParsePublicAndElevatedSwitches()
    {
        var options = InstallCommand.Options.Parse(
            ["--port", "48444", "--root", "C:\\HH", "--quiet", "--elevated", "--tray-user", "S-1-5-21-1-2-3-1001", "--result-file", "C:\\r.txt", "--parent-pid", "42"]);

        Assert.Equal(new InstallCommand.Options(48444, "C:\\HH", Quiet: true, Elevated: true, TrayUser: "S-1-5-21-1-2-3-1001", ResultFile: "C:\\r.txt", ParentProcessId: 42), options);
    }

    [Theory]
    [InlineData("--port", "0")]
    [InlineData("--port", "70000")]
    [InlineData("--port")]
    [InlineData("--bogus")]
    [InlineData("--elevated")]
    public void InstallOptions_RejectBadInput(params string[] args)
    {
        Assert.Throws<ArgumentException>(() => InstallCommand.Options.Parse(args));
    }
}
