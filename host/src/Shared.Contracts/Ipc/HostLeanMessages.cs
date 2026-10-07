namespace HyperHarbor.Shared.Contracts.Ipc;

/// <summary>Which profile the Lean host actions run: the shipped host-gaming profile, or the undo profile made from earlier applies.</summary>
public static class HostLeanSources
{
    public const string Lean = "lean";
    public const string Undo = "undo";

    public static bool IsKnown(string? source) => source is Lean or Undo;
}

/// <summary>One change a dry run found, or one an apply made. Handler is services, startup, registry, power, appx, or programs.</summary>
public sealed record HostLeanChangeLine(string Handler, string Item, string Text);

/// <summary>Idle memory and process count at one moment. Idle is false when the CPU was busy during the sample.</summary>
public sealed record HostLeanMetrics(DateTimeOffset At, int UsedMemoryMb, int ProcessCount, bool Idle);

/// <summary>What a dry run found. Nothing was changed.</summary>
/// <param name="CanApply">True while the findings are recent and the host still matches them, so an apply is allowed.</param>
/// <param name="Kept">Things the profile asked for that a guard or the startup allowlist kept, each with the reason.</param>
/// <param name="AlreadyInPlace">Items the profile covers that need no change.</param>
public sealed record HostLeanPlanSummary(
    string Source,
    DateTimeOffset At,
    bool CanApply,
    IReadOnlyList<HostLeanChangeLine> Changes,
    IReadOnlyList<string> Kept,
    IReadOnlyList<string> Problems,
    int AlreadyInPlace);

/// <summary>The outcome of the last apply (or undo).</summary>
/// <param name="Changed">Items changed successfully.</param>
/// <param name="RestorePoint">Set on the first apply: the restore point and registry export made before it.</param>
public sealed record HostLeanRunSummary(
    string Source,
    DateTimeOffset At,
    bool Scheduled,
    int Changed,
    IReadOnlyList<string> Problems,
    HostLeanMetrics? Before,
    HostLeanMetrics? After,
    string? RestorePoint);

/// <summary>Everything the tray shows about the Lean host action. Sent whole on every change.</summary>
/// <param name="Supported">False when the host does not run with administrator rights (a console run from source).</param>
/// <param name="Busy">The action in progress: dryRun, apply, or undo; null when idle.</param>
/// <param name="Applied">True after the first successful apply, when the restore point and registry export exist.</param>
public sealed record HostLeanStatus(
    bool Supported,
    string? UnsupportedReason,
    string? Busy,
    string ProfileName,
    bool Applied,
    bool UndoAvailable,
    bool ScheduleEnabled,
    DateTimeOffset? NextScheduled,
    HostLeanPlanSummary? DryRun,
    HostLeanRunSummary? LastRun);

/// <summary>The Lean host status, sent on connect and after every change.</summary>
public sealed record HostLeanStatusMessage(HostLeanStatus Status) : TrayMessage;

/// <summary>Asks for <see cref="HostLeanStatusMessage"/>.</summary>
public sealed record HostLeanQueryMessage : TrayMessage;

/// <summary>Reads the host and reports what the profile would change, without changing anything. <see cref="HostLeanSources"/>.</summary>
public sealed record HostLeanDryRunMessage(string Source) : TrayMessage;

/// <summary>Applies the profile. Refused unless a dry run of the same profile on the same host state was done recently.</summary>
public sealed record HostLeanApplyMessage(string Source) : TrayMessage;

/// <summary>Turns the monthly re-apply on or off.</summary>
public sealed record SetHostLeanScheduleMessage(bool Enabled) : TrayMessage;
