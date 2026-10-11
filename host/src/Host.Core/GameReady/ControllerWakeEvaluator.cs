using System.Text.RegularExpressions;

namespace HyperHarbor.Host.Core.GameReady;

/// <summary>
/// Merges the nodes of each physical controller or receiver and decides, from "powercfg /devicequery wake_armed" and
/// "wake_programmable", whether it is Armed, Can arm, or Cannot wake. Pure, so every branch is unit tested.
/// </summary>
public static partial class ControllerWakeEvaluator
{
    public const string BluetoothNote =
        "Bluetooth cannot wake this PC from sleep. Connect the controller with its USB cable, or use a wireless receiver " +
        "(an Xbox Wireless Adapter, or the 2.4 GHz dongle that came with the controller).";

    public const string ReceiverNote =
        "This receiver does not report wake support to Windows. Check the maker's site for a firmware update for the receiver, " +
        "or switch the controller to XInput mode, which many receivers need before they can wake a PC.";

    public const string UnreportedNote =
        "Windows does not report wake support for this device. A firmware update or XInput mode may add it; otherwise wake the PC " +
        "with a keyboard, mouse, or receiver that supports it.";

    public const string ViaReceiverNote = "Wakes the PC through its wireless receiver.";

    public const string NoReceiverNote = "No wireless receiver was found for this controller, so nothing can wake the PC for it.";

    // powercfg shows two devices that share a name as "name", "name (001)", "name (002)", and so on.
    [GeneratedRegex(@"^(?<base>.*?)(?: \(\d{3}\))?$", RegexOptions.Singleline)]
    private static partial Regex NumberedName();

    private static readonly string[] GenericNames =
    [
        "HID-compliant game controller",
        "XINPUT compatible HID device",
        "HID-compliant device",
        "USB Input Device",
    ];

    public static IReadOnlyList<GameController> Build(
        IEnumerable<PnpNode> nodes,
        IReadOnlySet<string> armed,
        IReadOnlySet<string> programmable)
    {
        ArgumentNullException.ThrowIfNull(nodes);
        ArgumentNullException.ThrowIfNull(armed);
        ArgumentNullException.ThrowIfNull(programmable);

        var classified = nodes.Select(ControllerClassifier.Classify).OfType<ClassifiedNode>().ToList();
        var groups = classified
            .GroupBy(node => GroupKey(node))
            .Select(group => Describe(group.Key, group.ToList(), armed, programmable))
            .ToList();

        // A controller that reaches the PC through a receiver is woken by the receiver; it has no entry of its own.
        var receivers = groups.Where(group => group.Kind == ControllerKind.Receiver).ToList();
        for (var i = 0; i < groups.Count; i++)
        {
            var group = groups[i];
            if (group.Kind == ControllerKind.Controller && group.Connection == ControllerConnection.Receiver && group.Wake == ControllerWake.CannotWake)
            {
                groups[i] = receivers.Count == 0
                    ? group with { Note = NoReceiverNote }
                    : group with { Wake = Best(receivers.Select(receiver => receiver.Wake)), Note = ViaReceiverNote };
            }
        }

        return groups
            .OrderBy(group => group.Kind)
            .ThenBy(group => group.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    /// <summary>
    /// The powercfg names that belong to <paramref name="deviceName"/>: the name itself and its numbered forms.
    /// </summary>
    public static IEnumerable<string> EntriesFor(string deviceName, IEnumerable<string> powercfgNames) =>
        powercfgNames.Where(entry => string.Equals(BaseName(entry), deviceName, StringComparison.OrdinalIgnoreCase));

    public static string BaseName(string powercfgName) => NumberedName().Match(powercfgName.Trim()).Groups["base"].Value;

    private static string GroupKey(ClassifiedNode node)
    {
        var key = ControllerClassifier.VendorProductKey(node.Node.InstanceId) ?? node.Node.Name.ToUpperInvariant();
        // The HID interfaces of a USB device enumerate under HID, not USB, so only Bluetooth separates devices.
        var link = node.Connection == ControllerConnection.Bluetooth ? "bluetooth" : "wired";
        return $"{node.Kind}:{link}:{key}".ToLowerInvariant();
    }

    private static GameController Describe(
        string id,
        List<ClassifiedNode> nodes,
        IReadOnlySet<string> armed,
        IReadOnlySet<string> programmable)
    {
        var first = nodes[0];
        var kind = first.Kind;
        var connection = nodes.Select(node => node.Connection).FirstOrDefault(c => c != ControllerConnection.Unknown, ControllerConnection.Unknown);
        var plain = nodes.Select(node => node.Node).ToList();
        var brand = nodes.Select(node => node.Brand).FirstOrDefault(b => b is not null);

        var names = plain.Select(node => node.Name).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        var entries = names.SelectMany(name => EntriesFor(name, programmable.Concat(armed))).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        var armTargets = entries.Where(entry => !armed.Contains(entry)).ToList();

        var wake = connection == ControllerConnection.Bluetooth
            ? ControllerWake.CannotWake
            : entries.Count == 0
                ? ControllerWake.CannotWake
                : armTargets.Count == 0 ? ControllerWake.Armed : ControllerWake.CanArm;

        return new GameController(
            id,
            DisplayName(names),
            kind,
            connection,
            brand,
            wake,
            wake == ControllerWake.CannotWake ? NoteFor(kind, connection) : null,
            plain,
            wake == ControllerWake.CanArm ? armTargets : []);
    }

    private static string? NoteFor(ControllerKind kind, ControllerConnection connection) => connection switch
    {
        ControllerConnection.Bluetooth => BluetoothNote,
        ControllerConnection.Receiver => null,
        _ => kind == ControllerKind.Receiver ? ReceiverNote : UnreportedNote,
    };

    /// <summary>The most specific name of the group: a name that is not one of the generic HID names, else the first.</summary>
    private static string DisplayName(List<string> names) =>
        names.OrderBy(name => GenericNames.Contains(name, StringComparer.OrdinalIgnoreCase) ? 1 : 0).ThenBy(name => name, StringComparer.OrdinalIgnoreCase).First();

    private static ControllerWake Best(IEnumerable<ControllerWake> states) => states.Min();
}
