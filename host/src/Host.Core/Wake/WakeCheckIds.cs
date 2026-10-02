namespace HyperHarbor.Host.Core.Wake;

/// <summary>Stable readiness check identifiers. Part of the API contract (WakeCheck.id).</summary>
public static class WakeCheckIds
{
    public const string WiredAdapter = "wiredAdapter";
    public const string NicWakeOnMagicPacket = "nicWakeOnMagicPacket";
    public const string NicAllowWake = "nicAllowWake";
    public const string SleepKeepsNetwork = "sleepKeepsNetwork";
    public const string FastStartupDisabled = "fastStartupDisabled";

    /// <summary>Checks that <see cref="WakeFixer"/> can change.</summary>
    public static IReadOnlySet<string> Fixable { get; } = new HashSet<string>(StringComparer.Ordinal)
    {
        NicWakeOnMagicPacket,
        NicAllowWake,
        SleepKeepsNetwork,
        FastStartupDisabled,
    };
}
