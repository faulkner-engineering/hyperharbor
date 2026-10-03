using HyperHarbor.Host.Core.Wake;
using HyperHarbor.Host.Service.Wake;

namespace HyperHarbor.Host.Tests.Wake;

/// <summary>
/// The elevated helper takes check IDs from its command line. It must refuse anything outside the
/// fixable allowlist before it checks elevation or touches the system.
/// </summary>
public class WakeFixCommandTests
{
    [Theory]
    [InlineData("")]
    [InlineData(",")]
    [InlineData("noSuchCheck")]
    [InlineData("powershell -c calc")]
    public async Task UnknownCheckIds_AreRejected(string checkIds)
    {
        Assert.Equal(WakeFixCommand.ExitInvalid, await WakeFixCommand.RunAsync(checkIds, resultFile: null));
    }

    [Fact]
    public async Task OneUnknownIdAmongValidOnes_RejectsAll()
    {
        var valid = WakeCheckIds.Fixable.First();

        Assert.Equal(WakeFixCommand.ExitInvalid, await WakeFixCommand.RunAsync($"{valid},noSuchCheck", resultFile: null));
    }

    [Fact]
    public async Task ValidIds_WithoutElevation_ChangeNothing()
    {
        if (Environment.IsPrivilegedProcess)
        {
            // An elevated test run would apply real fixes; this case only makes sense unelevated.
            return;
        }

        var resultFile = Path.Combine(Path.GetTempPath(), "hyperharbor-tests", Guid.NewGuid().ToString("N") + ".json");

        Assert.Equal(WakeFixCommand.ExitNotElevated, await WakeFixCommand.RunAsync(string.Join(',', WakeCheckIds.Fixable), resultFile));
        Assert.False(File.Exists(resultFile));
    }
}
