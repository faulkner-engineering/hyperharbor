using HyperHarbor.Host.Core.Installation;

namespace HyperHarbor.Host.Tests.Installation;

public sealed class WindowsRequirementTests
{
    [Theory]
    [InlineData(10, 0, 19045)] // Windows 10 22H2
    [InlineData(10, 0, 20348)] // Windows Server 2022
    [InlineData(10, 0, 21999)]
    [InlineData(6, 3, 9600)] // Windows 8.1
    public void WindowsBeforeWindows11_IsRefused_WithTheBuildAndTheLastVersionForIt(int major, int minor, int build)
    {
        var problem = WindowsRequirement.Problem(new Version(major, minor, build));

        Assert.NotNull(problem);
        Assert.Contains("needs Windows 11", problem);
        Assert.Contains($"build {build}", problem);
        Assert.Contains("0.2.6 is the last version for Windows 10", problem);
    }

    [Theory]
    [InlineData(10, 0, 22000)] // Windows 11 21H2
    [InlineData(10, 0, 26100)] // Windows 11 24H2 and Windows Server 2025
    [InlineData(10, 0, 26300)]
    public void Windows11_IsAccepted(int major, int minor, int build)
    {
        Assert.Null(WindowsRequirement.Problem(new Version(major, minor, build)));
    }

    [Fact]
    public void TheMachineRunningTheTests_IsAccepted()
    {
        // CI (windows-latest, Windows Server 2025) and development PCs run Windows 11 or later; the host would not start otherwise.
        Assert.Null(WindowsRequirement.ProblemOnThisPc());
    }
}
