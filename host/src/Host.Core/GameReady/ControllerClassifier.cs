using System.Text.RegularExpressions;

namespace HyperHarbor.Host.Core.GameReady;

/// <summary>
/// Decides whether a Plug and Play node is a game controller or a wireless receiver for one, and how it connects.
/// Pure and name based: Windows exposes no "is a game controller" flag for the USB and HID nodes that matter for wake.
/// A node with a generic name (USB Composite Device) is never picked, because "powercfg /deviceenablewake" works by
/// name and would arm every device that shares it.
/// </summary>
public static partial class ControllerClassifier
{
    // Names that identify a game controller on their own.
    [GeneratedRegex(@"xbox|xinput|game\s*controller|gamepad|joystick|dualshock|dualsense|8bitdo|gamesir|joy-con|pro controller|steam controller", RegexOptions.IgnoreCase)]
    private static partial Regex StrongName();

    // "Wireless Controller" is how Sony pads and many third-party pads name themselves; the bare word "controller" also
    // names host controllers and storage, so it counts only for the device classes game controllers use.
    [GeneratedRegex(@"\bcontroller\b", RegexOptions.IgnoreCase)]
    private static partial Regex WeakName();

    [GeneratedRegex(@"host controller|xhci|ehci|ohci|bluetooth|keyboard|mouse|\bbus\b|storage|nvm|audio|display|graphics|network|ethernet|touch|\bhub\b|smart card|management", RegexOptions.IgnoreCase)]
    private static partial Regex NotAController();

    [GeneratedRegex(@"adapter|receiver|dongle|2\.4\s*g", RegexOptions.IgnoreCase)]
    private static partial Regex ReceiverName();

    [GeneratedRegex(@"VID[&_]([0-9A-F]{4,8})[&_]PID[&_]([0-9A-F]{4})", RegexOptions.IgnoreCase)]
    private static partial Regex VendorAndProduct();

    private static readonly HashSet<string> GamePadClasses = new(StringComparer.OrdinalIgnoreCase)
    {
        "HIDClass", "XboxComposite", "XnaComposite", "Xbox", "XUSB",
    };

    private static readonly Dictionary<string, string> BrandByVendor = new(StringComparer.OrdinalIgnoreCase)
    {
        ["054C"] = "Sony",
        ["057E"] = "Nintendo",
        ["2DC8"] = "8BitDo",
        ["3537"] = "GameSir",
        ["28DE"] = "Valve",
    };

    public static ClassifiedNode? Classify(PnpNode node)
    {
        ArgumentNullException.ThrowIfNull(node);

        if (string.IsNullOrWhiteSpace(node.Name))
        {
            return null;
        }

        var strong = StrongName().IsMatch(node.Name);
        var weak = !strong && WeakName().IsMatch(node.Name) && node.PnpClass is { } pnpClass && GamePadClasses.Contains(pnpClass);
        if (!strong && !weak)
        {
            return null;
        }

        if (NotAController().IsMatch(node.Name))
        {
            return null;
        }

        var kind = ReceiverName().IsMatch(node.Name) ? ControllerKind.Receiver : ControllerKind.Controller;
        return new ClassifiedNode(node, kind, ConnectionOf(node, kind), BrandOf(node));
    }

    /// <summary>The vendor and product ids of a node ("045E:02FE"), or null when the instance id carries none.</summary>
    public static string? VendorProductKey(string instanceId)
    {
        var match = VendorAndProduct().Match(instanceId);
        
        // Bluetooth ids carry the vendor as eight digits (VID&0002054C: source 0002, vendor 054C).
        return match.Success ? $"{match.Groups[1].Value[^4..]}:{match.Groups[2].Value}".ToUpperInvariant() : null;
    }

    private static ControllerConnection ConnectionOf(PnpNode node, ControllerKind kind)
    {
        var id = node.InstanceId;
        var enumerator = id.Split('\\', 2)[0];

        // Bluetooth HID devices enumerate under BTHENUM or BTHLE, or as HID\{00001124-0000-1000-8000-00805F9B34FB}_...
        if (enumerator.StartsWith("BTH", StringComparison.OrdinalIgnoreCase)
            || id.Contains("00001124-0000-1000-8000-00805F9B34FB", StringComparison.OrdinalIgnoreCase))
        {
            return ControllerConnection.Bluetooth;
        }

        if (enumerator.Equals("USB", StringComparison.OrdinalIgnoreCase))
        {
            return ControllerConnection.Usb;
        }

        // Xbox pads on an Xbox Wireless Adapter are not USB devices of their own; they enumerate under the adapter's bus.
        return kind == ControllerKind.Controller && node.Name.Contains("wireless", StringComparison.OrdinalIgnoreCase)
            ? ControllerConnection.Receiver
            : ControllerConnection.Unknown;
    }

    private static string? BrandOf(PnpNode node)
    {
        var name = node.Name;
        if (name.Contains("xbox", StringComparison.OrdinalIgnoreCase) || name.Contains("xinput", StringComparison.OrdinalIgnoreCase))
        {
            return "Xbox";
        }

        if (name.Contains("dualshock", StringComparison.OrdinalIgnoreCase) || name.Contains("dualsense", StringComparison.OrdinalIgnoreCase))
        {
            return "Sony";
        }

        if (name.Contains("8bitdo", StringComparison.OrdinalIgnoreCase))
        {
            return "8BitDo";
        }

        if (name.Contains("gamesir", StringComparison.OrdinalIgnoreCase))
        {
            return "GameSir";
        }

        var key = VendorProductKey(node.InstanceId);
        return key is not null && BrandByVendor.TryGetValue(key[..4], out var brand) ? brand : null;
    }
}
