namespace HyperHarbor.Shared.Contracts.Ipc;

/// <summary>What the tray found on this desktop about the Windows screensaver the Ambient screen uses.</summary>
/// <param name="Active">Windows has a screensaver turned on.</param>
/// <param name="Path">SCRNSAVE.EXE, or null when none is set.</param>
/// <param name="TimeoutSeconds">The idle time before it starts; 0 when none is set.</param>
/// <param name="PolicyLocked">Group Policy sets the screensaver, so HyperHarbor cannot change it.</param>
public sealed record AmbientScreensaverState(bool Active, string? Path, int TimeoutSeconds, bool PolicyLocked);

/// <summary>
/// The Ambient screen card: the saved choices, the screensaver as the tray found it, the measured cost of the screen
/// while it ran, and whether an HDMI-CEC adapter is present (detection only; standby is not built yet).
/// </summary>
/// <param name="Supported">False when the screensaver file cannot be installed (not the installed service).</param>
/// <param name="Chord">Buttons that dismiss the screen when <paramref name="SoftLock"/> is on, for example "lb+rb+start".</param>
/// <param name="Screensaver">Null until the tray has reported.</param>
/// <param name="CpuPercent">Share of all processors the screen used while it ran; null when it has not run since a measurement.</param>
/// <param name="GpuPercent">Share of the GPU engines it used; null when Windows does not report it.</param>
/// <param name="MeasuredSeconds">How long the screen had run when the figures were taken.</param>
/// <param name="CecDevice">The name of a CEC adapter, or null when none was found.</param>
public sealed record AmbientInfo(
    bool Enabled,
    int IdleMinutes,
    bool ShowHostStatus,
    bool ShowRunningVms,
    bool ShowDevices,
    bool SoftLock,
    string Chord,
    bool Supported,
    string? UnsupportedReason,
    string? ScreensaverPath,
    AmbientScreensaverState? Screensaver,
    double? CpuPercent,
    double? GpuPercent,
    int? MeasuredSeconds,
    string? CecDevice);

/// <summary>Saves the Ambient screen choices. Needs the Console idle power mode; the idle time must be shorter than the display timeout.</summary>
public sealed record GameReadyAmbientApplyMessage(
    bool Enabled,
    int IdleMinutes,
    bool ShowHostStatus,
    bool ShowRunningVms,
    bool ShowDevices,
    bool SoftLock,
    string Chord) : TrayMessage;

/// <summary>From the tray: the screensaver as this desktop has it now, and the screen's measured cost when it has run.</summary>
public sealed record AmbientReportMessage(
    AmbientScreensaverState Screensaver,
    double? CpuPercent,
    double? GpuPercent,
    int? MeasuredSeconds) : TrayMessage;

/// <summary>From the tray, while the screen runs: asks for <see cref="AmbientSnapshotMessage"/>.</summary>
public sealed record AmbientSnapshotQueryMessage : TrayMessage;

/// <summary>What the optional widgets show. Only names and counts; nothing that identifies a device.</summary>
/// <param name="RunningVms">Names of the virtual machines that are running.</param>
/// <param name="PairedDevices">How many devices are paired.</param>
/// <param name="InUse">A paired device used this host in the last minutes.</param>
public sealed record AmbientSnapshotMessage(IReadOnlyList<string> RunningVms, int PairedDevices, bool InUse) : TrayMessage;
