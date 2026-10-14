using System.Text.Json.Serialization;

namespace HyperHarbor.Shared.Contracts.Backup;

/// <summary>Schema: RestoreTestOutcome.</summary>
public enum RestoreTestOutcome
{
    Passed,
    Failed,
    Skipped,
}

/// <summary>When the restore test runs. Schema: RestoreTestSettings.</summary>
/// <param name="IntervalDays">Days between scheduled tests; 30 is monthly.</param>
public sealed record RestoreTestSettings(
    [property: JsonRequired] bool Enabled,
    [property: JsonRequired] int IntervalDays,
    [property: JsonRequired] BackupTimeWindow Window);

/// <summary>One VM's test. Schema: RestoreTestResult.</summary>
/// <param name="Reason">Why a test failed or was skipped.</param>
public sealed record RestoreTestResult(
    Guid RunId,
    string VmName,
    string TargetId,
    BackupRunTrigger Trigger,
    DateTimeOffset StartedAt,
    DateTimeOffset FinishedAt,
    RestoreTestOutcome Outcome,
    IReadOnlyList<string> Warnings,
    Guid? VmId = null,
    string? SnapshotId = null,
    string? Reason = null);

/// <summary>Schema: RestoreTestStatus.</summary>
/// <param name="Current">The VM being tested, while a test runs.</param>
/// <param name="Testable">How many VMs have a backup to test.</param>
/// <param name="NextDue">When the next scheduled test is due; omitted when the test is off.</param>
/// <param name="Results">Newest first.</param>
public sealed record RestoreTestStatus(
    RestoreTestSettings Settings,
    bool Running,
    int Testable,
    IReadOnlyList<RestoreTestResult> Results,
    string? Current = null,
    DateTimeOffset? NextDue = null);
