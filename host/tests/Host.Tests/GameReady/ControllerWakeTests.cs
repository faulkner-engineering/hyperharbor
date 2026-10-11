using HyperHarbor.Host.Core.GameReady;

namespace HyperHarbor.Host.Tests.GameReady;

public sealed class ControllerWakeTests
{
    private static PnpNode Node(string name, string id, string? pnpClass = "HIDClass") => new(name, id, pnpClass, null);

    private static HashSet<string> Set(params string[] names) => new(names, StringComparer.OrdinalIgnoreCase);

    [Theory]
    [InlineData("Xbox Wireless Controller", @"USB\VID_045E&PID_02FD\1", "XboxComposite", ControllerKind.Controller, ControllerConnection.Usb, "Xbox")]
    [InlineData("Xbox Wireless Adapter for Windows", @"USB\VID_045E&PID_02FE\1", "USB", ControllerKind.Receiver, ControllerConnection.Usb, "Xbox")]
    [InlineData("Xbox One Game Controller", @"SWD\XBOXGIP\1", "XboxComposite", ControllerKind.Controller, ControllerConnection.Unknown, "Xbox")]
    [InlineData("Xbox Wireless Controller", @"XBOXGIP\1", "XboxComposite", ControllerKind.Controller, ControllerConnection.Receiver, "Xbox")]
    [InlineData("HID-compliant game controller", @"HID\VID_3537&PID_1010\1", "HIDClass", ControllerKind.Controller, ControllerConnection.Unknown, "GameSir")]
    [InlineData("GameSir G7 Receiver", @"USB\VID_3537&PID_1010\1", "USB", ControllerKind.Receiver, ControllerConnection.Usb, "GameSir")]
    [InlineData("8BitDo Ultimate 2.4G Adapter", @"USB\VID_2DC8&PID_3106\1", "USB", ControllerKind.Receiver, ControllerConnection.Usb, "8BitDo")]
    [InlineData("DualSense Wireless Controller", @"USB\VID_054C&PID_0CE6\1", "HIDClass", ControllerKind.Controller, ControllerConnection.Usb, "Sony")]
    [InlineData("Wireless Controller", @"USB\VID_054C&PID_09CC\1", "HIDClass", ControllerKind.Controller, ControllerConnection.Usb, "Sony")]
    [InlineData("HID-compliant game controller", @"HID\{00001124-0000-1000-8000-00805F9B34FB}_VID&0002054C_PID&0CE6\1", "HIDClass", ControllerKind.Controller, ControllerConnection.Bluetooth, "Sony")]
    public void GameDevices_AreRecognized_WithTheirConnection(string name, string id, string pnpClass, ControllerKind kind, ControllerConnection connection, string brand)
    {
        var classified = ControllerClassifier.Classify(Node(name, id, pnpClass));

        Assert.NotNull(classified);
        Assert.Equal(kind, classified.Kind);
        Assert.Equal(connection, classified.Connection);
        Assert.Equal(brand, classified.Brand);
    }

    [Theory]
    [InlineData("USB xHCI Compliant Host Controller", @"PCI\VEN_8086&DEV_1\1", "USB")]
    [InlineData("Standard NVM Express Controller", @"PCI\VEN_144D&DEV_A808\1", "SCSIAdapter")]
    [InlineData("HID-compliant mouse", @"HID\VID_046D&PID_C08B\1", "Mouse")]
    [InlineData("HID Keyboard Device", @"HID\VID_046D&PID_C33A\1", "Keyboard")]
    [InlineData("Intel(R) Wireless Bluetooth(R)", @"USB\VID_8087&PID_0033\1", "Bluetooth")]
    [InlineData("USB Composite Device", @"USB\VID_054C&PID_0CE6\1", "USB")]
    [InlineData("Realtek PCIe GbE Family Controller", @"PCI\VEN_10EC&DEV_8168\1", "Net")]
    [InlineData("Wireless Controller", @"ACPI\PNP0A08\1", "System")]
    public void OtherDevices_AreNotGameControllers(string name, string id, string pnpClass)
    {
        Assert.Null(ControllerClassifier.Classify(Node(name, id, pnpClass)));
    }

    private const string PadId = @"USB\VID_045E&PID_02FD\1";

    [Fact]
    public void ADeviceListedAsArmed_IsArmed()
    {
        var result = ControllerWakeEvaluator.Build(
            [Node("Xbox Wireless Controller", PadId, "XboxComposite")],
            Set("Xbox Wireless Controller"),
            Set("Xbox Wireless Controller"));

        var controller = Assert.Single(result);
        Assert.Equal(ControllerWake.Armed, controller.Wake);
        Assert.Empty(controller.ArmTargets);
        Assert.Null(controller.Note);
    }

    [Fact]
    public void AProgrammableDeviceThatIsNotArmed_CanBeArmed_AndNamesWhatToArm()
    {
        var result = ControllerWakeEvaluator.Build(
            [Node("Xbox Wireless Controller", PadId, "XboxComposite")],
            Set(),
            Set("Xbox Wireless Controller"));

        var controller = Assert.Single(result);
        Assert.Equal(ControllerWake.CanArm, controller.Wake);
        Assert.Equal(["Xbox Wireless Controller"], controller.ArmTargets);
    }

    [Fact]
    public void ADeviceWindowsDoesNotListForWake_CannotWake_AndAReceiverGetsFirmwareAdvice()
    {
        var result = ControllerWakeEvaluator.Build(
            [
                Node("Xbox Wireless Adapter for Windows", @"USB\VID_045E&PID_02FE\1", "USB"),
                Node("8BitDo Ultimate Controller", @"USB\VID_2DC8&PID_3106\1"),
            ],
            Set(),
            Set("HID-compliant mouse"));

        Assert.All(result, device => Assert.Equal(ControllerWake.CannotWake, device.Wake));
        Assert.Contains("firmware update", result.Single(device => device.Kind == ControllerKind.Receiver).Note);
        Assert.Contains("XInput mode", result.Single(device => device.Kind == ControllerKind.Receiver).Note);
        Assert.NotNull(result.Single(device => device.Kind == ControllerKind.Controller).Note);
    }

    [Fact]
    public void ABluetoothController_CannotWake_EvenWhenWindowsLists_AndTheNoteSuggestsACableOrReceiver()
    {
        var result = ControllerWakeEvaluator.Build(
            [Node("HID-compliant game controller", @"HID\{00001124-0000-1000-8000-00805F9B34FB}_VID&0002054C_PID&0CE6\1")],
            Set("HID-compliant game controller"),
            Set("HID-compliant game controller"));

        var controller = Assert.Single(result);
        Assert.Equal(ControllerWake.CannotWake, controller.Wake);
        Assert.Contains("Bluetooth cannot wake", controller.Note);
        Assert.Contains("receiver", controller.Note);
    }

    [Fact]
    public void NumberedPowercfgNames_AreMatched_AndOnlyTheUnarmedOnesAreTargets()
    {
        var result = ControllerWakeEvaluator.Build(
            [Node("HID-compliant game controller", @"USB\VID_054C&PID_0CE6\1")],
            Set("HID-compliant game controller (001)"),
            Set("HID-compliant game controller (001)", "HID-compliant game controller (002)", "HID-compliant mouse (004)"));

        var controller = Assert.Single(result);
        Assert.Equal(ControllerWake.CanArm, controller.Wake);
        Assert.Equal(["HID-compliant game controller (002)"], controller.ArmTargets);
    }

    [Fact]
    public void TheNodesOfOneDevice_AreMerged_AndTheSpecificNameIsShown()
    {
        var result = ControllerWakeEvaluator.Build(
            [
                Node("HID-compliant game controller", @"HID\VID_054C&PID_0CE6&IG_00\1"),
                Node("DualSense Wireless Controller", @"USB\VID_054C&PID_0CE6\1"),
            ],
            Set(),
            Set("DualSense Wireless Controller"));

        // The HID node and the USB node share a vendor and product id, so they are one device.
        var device = Assert.Single(result);
        Assert.Equal("DualSense Wireless Controller", device.Name);
        Assert.Equal(ControllerConnection.Usb, device.Connection);
        Assert.Equal(2, device.Nodes.Count);
        Assert.Equal(ControllerWake.CanArm, device.Wake);
    }

    [Fact]
    public void AControllerBehindAReceiver_FollowsTheReceiversWakeState()
    {
        var nodes = new[]
        {
            Node("Xbox Wireless Adapter for Windows", @"USB\VID_045E&PID_02FE\1", "USB"),
            Node("Xbox Wireless Controller", @"XBOXGIP\1", "XboxComposite"),
        };

        var armed = ControllerWakeEvaluator.Build(nodes, Set("Xbox Wireless Adapter for Windows"), Set("Xbox Wireless Adapter for Windows"));
        var canArm = ControllerWakeEvaluator.Build(nodes, Set(), Set("Xbox Wireless Adapter for Windows"));
        var none = ControllerWakeEvaluator.Build([nodes[1]], Set(), Set());

        Assert.Equal(ControllerWake.Armed, armed.Single(d => d.Kind == ControllerKind.Controller).Wake);
        Assert.Equal(ControllerWake.CanArm, canArm.Single(d => d.Kind == ControllerKind.Controller).Wake);
        Assert.Equal(ControllerWake.CannotWake, none.Single().Wake);
        Assert.Equal(ControllerWakeEvaluator.NoReceiverNote, none.Single().Note);
    }

    [Theory]
    [InlineData("HID-compliant mouse (004)", "HID-compliant mouse")]
    [InlineData("HID-compliant mouse", "HID-compliant mouse")]
    [InlineData("Intel(R) Ethernet (2) I219-V", "Intel(R) Ethernet (2) I219-V")]
    public void BaseName_RemovesOnlyTheNumberPowercfgAdds(string powercfgName, string expected)
    {
        Assert.Equal(expected, ControllerWakeEvaluator.BaseName(powercfgName));
    }
}
