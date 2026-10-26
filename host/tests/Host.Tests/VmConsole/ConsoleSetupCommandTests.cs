using System.Security.Principal;
using HyperHarbor.Host.Core.Identity;
using HyperHarbor.Host.Service.VmConsole;
using HyperHarbor.Host.Tray;

namespace HyperHarbor.Host.Tests.VmConsole;

public sealed class ConsoleSetupCommandTests
{
    private static readonly SecurityIdentifier User = new("S-1-5-21-1111111111-2222222222-3333333333-1001");

    [Fact]
    public void Parse_WithoutOptions_KeepsTheEarlierForm()
    {
        Assert.Equal(
            new ConsoleSetupCommand.Arguments(SetUp: true, ForUser: null, HostIdentityStore.DefaultDataDirectory, ResultFile: null),
            ConsoleSetupCommand.Parse(["--setup-console"]));
        Assert.Equal(
            new ConsoleSetupCommand.Arguments(SetUp: false, ForUser: null, "C:\\data", "C:\\result.txt"),
            ConsoleSetupCommand.Parse(["--remove-console", "C:\\data", "C:\\result.txt"]));
    }

    [Fact]
    public void Parse_ReadsTheUserOption_WhereverItIs()
    {
        var expected = new ConsoleSetupCommand.Arguments(SetUp: true, User, "C:\\data", "C:\\result.txt");

        Assert.Equal(expected, ConsoleSetupCommand.Parse(["--setup-console", "--for-user=" + User.Value, "C:\\data", "C:\\result.txt"]));
        Assert.Equal(expected, ConsoleSetupCommand.Parse(["--setup-console", "C:\\data", "C:\\result.txt", "--for-user=" + User.Value]));
    }

    [Theory]
    [InlineData("--setup-console", "--for-user=not-a-sid")]
    [InlineData("--setup-console", "--for-user=")]
    [InlineData("--setup-console", "--for-user=S-1-5-21-1-2-3-1001", "--for-user=S-1-5-21-1-2-3-1002")]
    [InlineData("--setup-console", "C:\\data", "C:\\result.txt", "C:\\extra")]
    [InlineData("--apply-wake-fixes", "wol-nic")]
    public void Parse_RejectsMalformedArguments(params string[] args)
    {
        Assert.Null(ConsoleSetupCommand.Parse(args));
    }

    [Fact]
    public void TrayArguments_NameTheUser_OnlyForAPortableHost()
    {
        Assert.Equal(["--setup-console", "--for-user=" + User.Value, "C:\\data"], ConsoleAccessSetup.Arguments("C:\\data", serviceInstalled: false, User));
        Assert.Equal(["--setup-console", "C:\\data"], ConsoleAccessSetup.Arguments("C:\\data", serviceInstalled: true, User));
        Assert.Equal(["--setup-console", "C:\\data"], ConsoleAccessSetup.Arguments("C:\\data", serviceInstalled: false, user: null));
    }
}
