namespace HyperHarbor.Shared.Contracts.Wake;

/// <summary>Wake-on-LAN readiness report. Schema: WakeReadiness.</summary>
/// <param name="Ready">True when no check has status Fail.</param>
public sealed record WakeReadiness(bool Ready, IReadOnlyList<WakeCheck> Checks);
