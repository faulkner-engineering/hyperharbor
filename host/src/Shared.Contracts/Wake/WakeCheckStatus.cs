namespace HyperHarbor.Shared.Contracts.Wake;

/// <summary>Outcome of a readiness check. Schema: WakeCheckStatus.</summary>
public enum WakeCheckStatus
{
    Pass,
    Warn,
    Fail,
}
