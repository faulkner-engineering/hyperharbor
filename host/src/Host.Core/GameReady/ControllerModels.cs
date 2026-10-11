namespace HyperHarbor.Host.Core.GameReady;

/// <summary>One present device from Plug and Play (Win32_PnPEntity), before anyone decides whether it is a game controller.</summary>
/// <param name="InstanceId">PNPDeviceID, for example USB\VID_045E&amp;PID_02FE\5&amp;1d6f1b0c&amp;0&amp;1.</param>
public sealed record PnpNode(string Name, string InstanceId, string? PnpClass, string? Manufacturer);

public enum ControllerKind
{
    Controller,
    Receiver,
}

/// <summary>Receiver means a controller that reaches the PC through a wireless receiver or adapter.</summary>
public enum ControllerConnection
{
    Usb,
    Receiver,
    Bluetooth,
    Unknown,
}

public enum ControllerWake
{
    /// <summary>Windows lets the device wake the PC (powercfg wake_armed).</summary>
    Armed,

    /// <summary>Windows can allow it but does not yet (powercfg wake_programmable only).</summary>
    CanArm,

    /// <summary>Windows does not list the device for wake at all, or the connection cannot wake the PC.</summary>
    CannotWake,
}

/// <summary>A node that <see cref="ControllerClassifier"/> recognized.</summary>
/// <param name="Brand">Xbox, Sony, 8BitDo, GameSir, Nintendo, Valve, or null when unknown.</param>
public sealed record ClassifiedNode(PnpNode Node, ControllerKind Kind, ControllerConnection Connection, string? Brand);

/// <summary>
/// One controller or receiver as the user sees it. The Plug and Play nodes of one physical device (the USB device, its
/// HID interfaces) are merged.
/// </summary>
/// <param name="ArmTargets">The exact powercfg names that "powercfg /deviceenablewake" must be given to arm the device.</param>
public sealed record GameController(
    string Id,
    string Name,
    ControllerKind Kind,
    ControllerConnection Connection,
    string? Brand,
    ControllerWake Wake,
    string? Note,
    IReadOnlyList<PnpNode> Nodes,
    IReadOnlyList<string> ArmTargets);
