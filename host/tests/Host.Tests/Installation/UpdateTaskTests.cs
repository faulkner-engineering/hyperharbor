using System.Xml.Linq;
using HyperHarbor.Host.Core.Installation;
using HyperHarbor.Host.Service.Installation;

namespace HyperHarbor.Host.Tests.Installation;

public sealed class UpdateTaskTests
{
    private static readonly XNamespace Task = "http://schemas.microsoft.com/windows/2004/02/mit/task";

    [Fact]
    public void Definition_RunsTheHelperAsSystemAtStartup_EvenOnBattery()
    {
        var layout = new InstallLayout(@"C:\Program Files\HyperHarbor");

        var definition = UpdateTask.Definition(layout).Root!;

        Assert.Equal(@"C:\Program Files\HyperHarbor\hh-update.exe", definition.Descendants(Task + "Command").Single().Value);
        Assert.Equal("update-run", definition.Descendants(Task + "Arguments").Single().Value);
        Assert.Equal("S-1-5-18", definition.Descendants(Task + "UserId").Single().Value);
        Assert.Single(definition.Descendants(Task + "BootTrigger"));

        // Updates and recovery must run on a laptop host that is unplugged.
        Assert.Equal("false", definition.Descendants(Task + "DisallowStartIfOnBatteries").Single().Value);
        Assert.Equal("false", definition.Descendants(Task + "StopIfGoingOnBatteries").Single().Value);
    }
}
