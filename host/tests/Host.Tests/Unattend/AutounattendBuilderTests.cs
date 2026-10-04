using System.Xml.Linq;
using HyperHarbor.Host.Core.Unattend;
using HyperHarbor.Shared.Contracts.Unattend;

namespace HyperHarbor.Host.Tests.Unattend;

public sealed class AutounattendBuilderTests
{
    private const string AdminPassword = "One-Time-Pass1!";
    private static readonly XNamespace Ns = "urn:schemas-microsoft-com:unattend";

    private static UnattendProfile Profile(WindowsInstallSettings? settings = null) => new(
        "windows-workstation", "Windows Workstation", true, InstallOs.Windows, "hhadmin", "Central Standard Time", "en-US",
        settings ?? new WindowsInstallSettings("Windows 11 Pro"), null);

    private static (string Text, XDocument Document) Build(WindowsInstallSettings? settings = null, string edition = "Windows 11 Pro")
    {
        var text = AutounattendBuilder.Build(new WindowsInstall(Profile(settings), edition, "DEV-BOX", AdminPassword, "hh-owner"));
        return (text, XDocument.Parse(text));
    }

    private static XElement Component(XDocument document, string pass, string name) =>
        document.Root!.Elements(Ns + "settings").Single(settings => (string?)settings.Attribute("pass") == pass)
            .Elements(Ns + "component").Single(component => (string?)component.Attribute("name") == name);

    private static IReadOnlyList<string> FirstLogonCommands(XDocument document) =>
        Component(document, "oobeSystem", "Microsoft-Windows-Shell-Setup")
            .Element(Ns + "FirstLogonCommands")!.Elements(Ns + "SynchronousCommand")
            .OrderBy(command => (int)command.Element(Ns + "Order")!)
            .Select(command => (string)command.Element(Ns + "CommandLine")!)
            .ToList();

    [Fact]
    public void PasswordAppearsOnlyInWindowsHiddenForm()
    {
        var (text, document) = Build();

        Assert.DoesNotContain(AdminPassword, text, StringComparison.Ordinal);
        var hidden = AutounattendBuilder.HidePassword(AdminPassword);
        Assert.Equal(System.Text.Encoding.Unicode.GetBytes(AdminPassword + "Password"), Convert.FromBase64String(hidden));
        var passwords = document.Descendants(Ns + "Password").ToList();
        Assert.Equal(2, passwords.Count);
        Assert.All(passwords, password =>
        {
            Assert.Equal(hidden, (string?)password.Element(Ns + "Value"));
            Assert.Equal("false", (string?)password.Element(Ns + "PlainText"));
        });
    }

    [Fact]
    public void InstallsTheEditionOnAUefiLayoutWithItsGenericKey()
    {
        var (_, document) = Build();
        var setup = Component(document, "windowsPE", "Microsoft-Windows-Setup");

        var metaData = setup.Descendants(Ns + "MetaData").Single();
        Assert.Equal("/IMAGE/NAME", (string?)metaData.Element(Ns + "Key"));
        Assert.Equal("Windows 11 Pro", (string?)metaData.Element(Ns + "Value"));
        Assert.Equal("VK7JG-NPHTM-C97JM-9MPGT-3V66T", (string?)setup.Descendants(Ns + "ProductKey").Single().Element(Ns + "Key"));
        Assert.Equal(["EFI", "MSR", "Primary"], setup.Descendants(Ns + "CreatePartition").Select(partition => (string)partition.Element(Ns + "Type")!));
        Assert.Equal("3", (string?)setup.Descendants(Ns + "InstallTo").Single().Element(Ns + "PartitionID"));
    }

    [Fact]
    public void SkipsOobeAndSignsInTheAdministratorOnce()
    {
        var (_, document) = Build();
        var shell = Component(document, "oobeSystem", "Microsoft-Windows-Shell-Setup");

        Assert.Equal("true", (string?)shell.Element(Ns + "OOBE")!.Element(Ns + "HideOnlineAccountScreens"));
        Assert.Equal("hhadmin", (string?)shell.Descendants(Ns + "LocalAccount").Single().Element(Ns + "Name"));
        Assert.Equal("Administrators", (string?)shell.Descendants(Ns + "LocalAccount").Single().Element(Ns + "Group"));
        Assert.Equal("1", (string?)shell.Element(Ns + "AutoLogon")!.Element(Ns + "LogonCount"));
        Assert.Equal("DEV-BOX", (string?)Component(document, "specialize", "Microsoft-Windows-Shell-Setup").Element(Ns + "ComputerName"));
        Assert.Equal("Central Standard Time", (string?)Component(document, "specialize", "Microsoft-Windows-Shell-Setup").Element(Ns + "TimeZone"));
    }

    [Fact]
    public void FirstLogonEnablesRemoteDesktopCreatesTheUserAccountAndSignsOut()
    {
        var commands = FirstLogonCommands(Build().Document);

        Assert.Contains(commands, command => command.Contains("fDenyTSConnections /t REG_DWORD /d 0", StringComparison.Ordinal));
        Assert.Contains(commands, command => command.Contains("Enable-NetFirewallRule -Group '@FirewallAPI.dll,-28752'", StringComparison.Ordinal));
        var account = Assert.Single(commands, command => command.Contains("New-LocalUser -Name 'hh-owner'", StringComparison.Ordinal));
        Assert.Contains("Add-LocalGroupMember -SID 'S-1-5-32-555' -Member 'hh-owner'", account, StringComparison.Ordinal);
        Assert.Equal("shutdown /l", commands[^1]);
        Assert.All(commands, command => Assert.True(command.Length <= 1024, command));
    }

    [Fact]
    public void PrivacyToggles_AddOnlyTheChosenPolicies()
    {
        var all = FirstLogonCommands(Build().Document);
        var none = FirstLogonCommands(Build(new WindowsInstallSettings("Windows 11 Pro", DisableTelemetry: false, DisableAdvertisingId: false, DisableLocation: false, DisableConsumerFeatures: false)).Document);

        Assert.Contains(all, command => command.Contains("AllowTelemetry", StringComparison.Ordinal));
        Assert.Contains(all, command => command.Contains("DisableWindowsConsumerFeatures", StringComparison.Ordinal));
        Assert.DoesNotContain(none, command => command.Contains(@"SOFTWARE\Policies", StringComparison.Ordinal));
    }

    [Fact]
    public void HardwareCheckBypass_OnlyWhenChosen()
    {
        var (withBypass, _) = Build(new WindowsInstallSettings("Windows 11 Pro", BypassHardwareChecks: true));
        var (withoutBypass, _) = Build();

        Assert.Contains(@"LabConfig /v BypassTPMCheck", withBypass, StringComparison.Ordinal);
        Assert.Contains(@"LabConfig /v BypassSecureBootCheck", withBypass, StringComparison.Ordinal);
        Assert.DoesNotContain("LabConfig", withoutBypass, StringComparison.Ordinal);
    }

    [Fact]
    public void EditionNamesAreEscapedAsXml()
    {
        var (text, document) = Build(edition: "Windows 11 <Pro> & \"Co\"");

        Assert.Equal("Windows 11 <Pro> & \"Co\"", (string?)document.Descendants(Ns + "MetaData").Single().Element(Ns + "Value"));
        Assert.Contains("&lt;Pro&gt; &amp;", text, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("Windows 11 Pro", "VK7JG-NPHTM-C97JM-9MPGT-3V66T")]
    [InlineData("Windows 11 Pro N", "2B87N-8KFHP-DKV6R-Y2C8J-PKCKT")]
    [InlineData("Windows 10 Home", "YTMG3-N6DKC-DKB77-7M9GH-8HVX7")]
    [InlineData("Windows 11 Pro Education", null)]
    [InlineData("Windows 11 Home Single Language", null)]
    [InlineData("Windows Server 2025 Standard", null)]
    public void GenericKeys_MatchWholeEditionNames(string edition, string? expected)
    {
        Assert.Equal(expected, AutounattendBuilder.GenericKey(edition));
    }

    [Theory]
    [InlineData("Dev Box", "DEV-BOX")]
    [InlineData("windows 11 developer workstation", "WINDOWS-11-DEVE")]
    [InlineData("12345", "HYPERHARBOR-VM")]
    [InlineData("---", "HYPERHARBOR-VM")]
    public void ComputerNames_AreNetBiosSafe(string vmName, string expected)
    {
        Assert.Equal(expected, AutounattendBuilder.ComputerName(vmName));
    }

    [Fact]
    public void AccountNamesThatCouldBreakTheCommandAreRefused()
    {
        Assert.Throws<ArgumentException>(() => AutounattendBuilder.Build(new WindowsInstall(Profile(), "Windows 11 Pro", "PC", AdminPassword, "x'; Remove-Item")));
    }
}
