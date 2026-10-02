namespace HyperHarbor.Shared.Contracts.Wake;

/// <summary>Scheduled sleep time for a wake test. Schema: WakeTestScheduled.</summary>
public sealed record WakeTestScheduled(DateTimeOffset SleepAt);
