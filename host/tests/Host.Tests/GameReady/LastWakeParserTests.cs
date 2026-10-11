using HyperHarbor.Host.Core.GameReady;

namespace HyperHarbor.Host.Tests.GameReady;

public sealed class LastWakeParserTests
{
    private const string Controller = """
        Wake History Count - 1
        Wake History [0]
          Wake Source Count - 1
          Wake Source [0]
            Type: Device
            Instance Path: USB\VID_045E&PID_02FE\5&1d6f1b0c&0&1
            Friendly Name: Xbox Wireless Adapter for Windows
            Description: Xbox Wireless Adapter for Windows
            Manufacturer: Microsoft
        """;

    private static GameController Adapter(ControllerWake wake = ControllerWake.Armed) => new(
        "receiver:usb:045e:02fe",
        "Xbox Wireless Adapter for Windows",
        ControllerKind.Receiver,
        ControllerConnection.Usb,
        "Xbox",
        wake,
        null,
        [new PnpNode("Xbox Wireless Adapter for Windows", @"USB\VID_045E&PID_02FE\5&1d6f1b0c&0&1", "USB", null)],
        []);

    [Fact]
    public void ADeviceSource_IsParsed()
    {
        var result = LastWakeParser.Parse(Controller);

        Assert.Equal(1, result.HistoryCount);
        var source = Assert.Single(result.Sources);
        Assert.True(source.IsDevice);
        Assert.Equal(@"USB\VID_045E&PID_02FE\5&1d6f1b0c&0&1", source.InstancePath);
        Assert.Equal("Xbox Wireless Adapter for Windows", source.Name);
    }

    [Fact]
    public void AnEmptyHistory_HasNoSources()
    {
        var result = LastWakeParser.Parse("Wake History Count - 0\r\n");

        Assert.Equal(0, result.HistoryCount);
        Assert.Empty(result.Sources);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("Der Verlauf der letzten Aktivierung enthält 0 Einträge.")]
    public void MissingOrLocalizedOutput_YieldsNoSources(string? output)
    {
        Assert.Empty(LastWakeParser.Parse(output).Sources);
    }

    [Fact]
    public void AFixedFeature_UsesItsFeatureType_AndCrLfLinesWork()
    {
        var output = "Wake History Count - 1\r\nWake History [0]\r\n  Wake Source Count - 1\r\n  Wake Source [0]\r\n    Type: Fixed Feature\r\n    Fixed Feature Type: Power Button\r\n";

        var source = Assert.Single(LastWakeParser.Parse(output).Sources);

        Assert.False(source.IsDevice);
        Assert.Equal("Fixed Feature", source.Type);
        Assert.Equal("Power Button", source.Name);
    }

    [Fact]
    public void OnlyTheNewestWake_IsRead_AndAllItsSources()
    {
        var output = """
            Wake History Count - 2
            Wake History [0]
              Wake Source Count - 2
              Wake Source [0]
                Type: Fixed Feature
                Fixed Feature Type: Power Button
              Wake Source [1]
                Type: Device
                Instance Path: USB\VID_054C&PID_0CE6\1
                Friendly Name: DualSense Wireless Controller
            Wake History [1]
              Wake Source Count - 1
              Wake Source [0]
                Type: Wake Timer
                Owner: Task Scheduler
            """;

        var result = LastWakeParser.Parse(output);

        Assert.Equal(2, result.HistoryCount);
        Assert.Equal(["Fixed Feature", "Device"], result.Sources.Select(source => source.Type));
    }

    [Fact]
    public void AMatchingVendorAndProduct_IsAttributedToTheController()
    {
        // The source is the HID interface; the controller entry holds the USB node. They share ids.
        var output = Controller.Replace(@"USB\VID_045E&PID_02FE\5&1d6f1b0c&0&1", @"HID\VID_045E&PID_02FE&MI_00\7&1");

        var attribution = WakeAttributor.Attribute(LastWakeParser.Parse(output), [Adapter()]);

        Assert.Equal(WakeAttributionKind.Controller, attribution.Kind);
        Assert.Equal("Xbox Wireless Adapter for Windows", attribution.Controller!.Name);
    }

    [Fact]
    public void ADeviceThatIsNotAController_IsReportedAsAnotherDevice()
    {
        var output = """
            Wake History Count - 1
            Wake History [0]
              Wake Source Count - 1
              Wake Source [0]
                Type: Device
                Instance Path: HID\VID_046D&PID_C08B\1
                Friendly Name: HID-compliant mouse
            """;

        var attribution = WakeAttributor.Attribute(LastWakeParser.Parse(output), [Adapter()]);

        Assert.Equal(WakeAttributionKind.OtherDevice, attribution.Kind);
        Assert.Equal("HID-compliant mouse", attribution.SourceName);
    }

    [Fact]
    public void ThePowerButton_AndAnEmptyHistory_AreNotAttributedToAController()
    {
        var button = LastWakeParser.Parse("Wake History Count - 1\nWake History [0]\n  Wake Source [0]\n    Type: Fixed Feature\n    Fixed Feature Type: Power Button\n");

        Assert.Equal(WakeAttributionKind.OtherSource, WakeAttributor.Attribute(button, [Adapter()]).Kind);
        Assert.Equal(WakeAttributionKind.NoSource, WakeAttributor.Attribute(LastWakeParser.Parse("Wake History Count - 0"), [Adapter()]).Kind);
    }
}
