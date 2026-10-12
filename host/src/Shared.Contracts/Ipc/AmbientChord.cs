namespace HyperHarbor.Shared.Contracts.Ipc;

/// <summary>
/// The XInput buttons a soft lock chord is made of, written as names joined by "+" (for example "lb+rb+start").
/// The values are XInput's button bits, so a pad's button word can be tested against <see cref="Parse"/>'s mask directly.
/// </summary>
public static class AmbientChord
{
    public const string Default = "lb+rb+start";

    /// <summary>A chord needs at least two buttons, so a single stray press never counts, and at most four.</summary>
    public const int MinimumButtons = 2;

    public const int MaximumButtons = 4;

    // The order of the stored form and the description: shoulders and sticks first, then Back and Start, the D-pad, the face buttons.
    private static readonly (string Name, string Label, ushort Bit)[] Buttons =
    [
        ("lb", "LB", 0x0100),
        ("rb", "RB", 0x0200),
        ("ls", "Left stick", 0x0040),
        ("rs", "Right stick", 0x0080),
        ("back", "Back", 0x0020),
        ("start", "Start", 0x0010),
        ("up", "D-pad up", 0x0001),
        ("down", "D-pad down", 0x0002),
        ("left", "D-pad left", 0x0004),
        ("right", "D-pad right", 0x0008),
        ("a", "A", 0x1000),
        ("b", "B", 0x2000),
        ("x", "X", 0x4000),
        ("y", "Y", 0x8000),
    ];

    /// <returns>The buttons' bits, or null when the text is not a chord of <see cref="MinimumButtons"/> to <see cref="MaximumButtons"/> known, distinct buttons.</returns>
    public static ushort? Parse(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        ushort mask = 0;
        var count = 0;
        foreach (var part in text.Split('+', StringSplitOptions.TrimEntries))
        {
            var button = Buttons.FirstOrDefault(known => known.Name.Equals(part, StringComparison.OrdinalIgnoreCase));
            if (button.Name is null || (mask & button.Bit) != 0)
            {
                return null;
            }

            mask |= button.Bit;
            count++;
        }

        return count is >= MinimumButtons and <= MaximumButtons ? mask : null;
    }

    /// <summary>The chord in the form that is stored: lower case, in a fixed order. Null when it is not valid.</summary>
    public static string? Normalize(string? text) =>
        Parse(text) is { } mask ? string.Join('+', Buttons.Where(button => (mask & button.Bit) != 0).Select(button => button.Name)) : null;

    /// <summary>The chord for a person, for example "LB + RB + Start". Empty when the text is not a chord.</summary>
    public static string Describe(string? text) =>
        Parse(text) is { } mask ? string.Join(" + ", Buttons.Where(button => (mask & button.Bit) != 0).Select(button => button.Label)) : string.Empty;

    /// <summary>True when one pad holds every button of the chord. Other buttons may be held too.</summary>
    public static bool IsHeld(IEnumerable<ushort> pads, ushort mask) => mask != 0 && pads.Any(pad => (pad & mask) == mask);
}

/// <summary>Names the ambient screensaver file shares between the service (which installs it) and the tray (which recognizes it).</summary>
public static class AmbientScreensaver
{
    public const string FileName = "HyperHarbor Ambient.scr";
}
