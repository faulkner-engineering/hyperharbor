namespace HyperHarbor.Shared.Contracts.Ipc;

/// <summary>
/// What the tray saw about the foreground window on the signed-in desktop. Plain facts, so the decision is testable.
/// </summary>
/// <param name="NotificationState">SHQueryUserNotificationState: 3 is a full-screen Direct3D application, 4 is presentation mode.</param>
/// <param name="CoversMonitor">The window fills the whole monitor it is on.</param>
/// <param name="HasBorder">The window has a frame or caption (a windowed app, even a maximized one).</param>
/// <param name="IsShell">The window belongs to the desktop or the taskbar.</param>
/// <param name="IsOwnWindow">The window belongs to HyperHarbor.</param>
/// <param name="ProcessName">Lower case, no ".exe"; null when it could not be read.</param>
/// <param name="Lineage">Names of the process's parents, nearest first.</param>
public sealed record ForegroundSignals(
    int? NotificationState,
    bool CoversMonitor,
    bool HasBorder,
    bool IsShell,
    bool IsOwnWindow,
    string? ProcessName,
    IReadOnlyList<string> Lineage);

/// <summary>From the tray, about every two seconds: what is in the foreground. The service decides whether it is a game.</summary>
public sealed record GameSignalsMessage(ForegroundSignals Signals) : TrayMessage;

/// <summary>The host window asks for <see cref="GameAwareStatusMessage"/>.</summary>
public sealed record GameAwareQueryMessage : TrayMessage;

/// <summary>The service's game-aware settings and state, sent in answer to <see cref="GameAwareQueryMessage"/> and after a change.</summary>
public sealed record GameAwareStatusMessage(Hosts.GameAwareInfo Info) : TrayMessage;

/// <summary>Saves the game-aware settings from the host window.</summary>
public sealed record GameAwareApplyMessage(Hosts.GameAwareSettings Settings) : TrayMessage;

/// <summary>"This was not a game": ignore the program that started the game state, and resume the VMs.</summary>
public sealed record GameAwareNotAGameMessage : TrayMessage;
