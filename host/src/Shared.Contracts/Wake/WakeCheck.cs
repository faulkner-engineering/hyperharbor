namespace HyperHarbor.Shared.Contracts.Wake;

/// <summary>Single Wake-on-LAN readiness check. Schema: WakeCheck.</summary>
/// <param name="Id">Stable identifier, for example nicWakeOnMagicPacket.</param>
public sealed record WakeCheck(string Id, string Title, WakeCheckStatus Status, string? Detail, bool AutoFixAvailable);
