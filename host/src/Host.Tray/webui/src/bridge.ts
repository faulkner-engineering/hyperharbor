// The page talks to the tray (TrayApplicationContext) through WebView2's message channel. These types mirror
// Host.Tray/Window/BridgeMessages.cs and TrayCommands.cs; bridge.fixtures.json, written by the .NET tests, keeps
// them in step (see bridge.test.ts).

export type HostUpdateMode = "auto" | "notify" | "off";
export type HostUpdateActivity = "idle" | "checking" | "preparing" | "ready" | "installing";
export type HostUpdateStep = "downloading" | "verifying" | "testing";

/** api.yaml HostUpdateProgress (API 1.15.0). */
export interface HostUpdateProgress {
  step: HostUpdateStep;
  bytesDone: number;
  bytesTotal: number;
}

/** GET /host/update (api.yaml HostUpdateStatus). */
export interface HostUpdateStatus {
  supported: boolean;
  mode: HostUpdateMode;
  channel: string;
  channels: string[];
  maintenanceTime: string | null;
  currentVersion: string;
  availableVersion: string | null;
  notesUrl: string | null;
  activity: HostUpdateActivity;
  lastCheck: string | null;
  message: string | null;
  lastResult: string | null;
  rolledBack: string[];
  /** While preparing a version (API 1.15.0); left out otherwise. */
  progress?: HostUpdateProgress | null;
}

export interface HostLeanChangeLine {
  handler: string;
  item: string;
  text: string;
}

export interface HostLeanMetrics {
  at: string;
  usedMemoryMb: number;
  processCount: number;
  idle: boolean;
}

/** Mirrors Shared.Contracts/Ipc/HostLeanMessages.cs. Null properties are left out by the JSON options, so they are optional here. */
export interface HostLeanPlanSummary {
  source: "lean" | "undo";
  at: string;
  canApply: boolean;
  changes: HostLeanChangeLine[];
  kept: string[];
  problems: string[];
  alreadyInPlace: number;
}

export interface HostLeanRunSummary {
  source: "lean" | "undo";
  at: string;
  scheduled: boolean;
  changed: number;
  problems: string[];
  before?: HostLeanMetrics | null;
  after?: HostLeanMetrics | null;
  restorePoint?: string | null;
}

export interface HostLeanStatus {
  supported: boolean;
  unsupportedReason?: string | null;
  /** dryRun, apply, or undo; null when idle. */
  busy?: "dryRun" | "apply" | "undo" | null;
  profileName: string;
  applied: boolean;
  undoAvailable: boolean;
  scheduleEnabled: boolean;
  nextScheduled?: string | null;
  dryRun?: HostLeanPlanSummary | null;
  lastRun?: HostLeanRunSummary | null;
}

/** Mirrors Shared.Contracts/Ipc/GameReadyMessages.cs. Null properties are left out by the JSON options, so they are optional here. */
export type GameReadyState = "ready" | "attention" | "manual" | "unavailable" | "comingSoon";

export interface GameReadyCheck {
  id: string;
  title: string;
  status: "pass" | "warn" | "fail";
  detail?: string | null;
  canFix: boolean;
}

export type ControllerWakeState = "armed" | "canArm" | "cannotWake";

export interface GameReadyController {
  id: string;
  name: string;
  kind: "controller" | "receiver";
  connection: "usb" | "receiver" | "bluetooth" | "unknown";
  wake: ControllerWakeState;
  note?: string | null;
}

export interface GameReadyCard {
  id: string;
  title: string;
  state: GameReadyState;
  summary: string;
  checks: GameReadyCheck[];
  controllers: GameReadyController[];
  hints: string[];
  canFix: boolean;
  fixBlockedReason?: string | null;
  canTestWake: boolean;
}

export interface GameReadyWakeTest {
  state: "idle" | "countdown" | "sleeping" | "done" | "failed";
  sleepAt?: string | null;
  resumedAt?: string | null;
  outcome?: "controller" | "otherDevice" | "otherSource" | "noSource" | null;
  source?: string | null;
  message?: string | null;
}

export interface GameReadyStatus {
  readAt?: string | null;
  /** "reading", "fix:<card id>", or null when idle. */
  busy?: string | null;
  error?: string | null;
  cards: GameReadyCard[];
  wakeTest: GameReadyWakeTest;
}

export interface TrayViewDevice {
  id: string;
  name: string;
  fingerprint: string;
  pairedAt: string;
}

export interface TrayViewState {
  connected: boolean;
  /** Null while the service is not connected. */
  passphraseConfigured: boolean | null;
  devices: TrayViewDevice[];
  vmFolder: { folder: string; isDefault: boolean } | null;
  isoFolder: string | null;
  backupFolder: string | null;
  consoleReady: boolean;
  packageSearchReady: boolean;
  update: HostUpdateStatus | null;
  /** Null while the service is not connected. */
  hostLean: HostLeanStatus | null;
  /** Null while the service is not connected. */
  gameReady: GameReadyStatus | null;
  /** Work in progress: passphrase, console, packageSearch, folder:vm|iso|backup, device:<id>, update. */
  busy: string[];
  pairing: { pairingId: string; deviceName: string; pin: string; expiresAt: string } | null;
  dataDirectory: string;
}

export type ToastKind = "info" | "success" | "error";

export type HostMessage =
  | { type: "state"; value: TrayViewState }
  | { type: "toast"; kind: ToastKind; title: string; text: string }
  | { type: "navigate"; page: string };

export type FolderKind = "vm" | "iso" | "backup";

export type PageMessage =
  | { type: "ready" }
  | { type: "setPassphrase"; passphrase: string }
  | { type: "removeDevice"; deviceId: string }
  | { type: "chooseFolder"; folder: FolderKind }
  | { type: "setUpConsole" }
  | { type: "setUpPackageSearch" }
  | { type: "checkUpdate" }
  | { type: "installUpdate" }
  | { type: "hostLeanDryRun"; source: "lean" | "undo" }
  | { type: "hostLeanApply"; source: "lean" | "undo" }
  | { type: "setHostLeanSchedule"; enabled: boolean }
  | { type: "gameReadyRefresh" }
  | { type: "gameReadyFix"; cardId: string }
  | { type: "gameReadyTestWake"; delaySeconds: number }
  | { type: "setChannel"; channel: string }
  | { type: "open"; target: "logs" | "audit" }
  | { type: "openUrl"; url: string }
  | { type: "cancelPairing"; pairingId: string }
  | { type: "close" };

interface WebViewChannel {
  postMessage(message: unknown): void;
  addEventListener(type: "message", listener: (event: { data: unknown }) => void): void;
}

function channel(): WebViewChannel | null {
  return (window as unknown as { chrome?: { webview?: WebViewChannel } }).chrome?.webview ?? null;
}

/** Sends a message to the tray; does nothing outside WebView2 (for example in a browser while developing). */
export function send(message: PageMessage): void {
  channel()?.postMessage(message);
}

/** Calls the handler with every message from the tray. */
export function listen(handler: (message: HostMessage) => void): void {
  channel()?.addEventListener("message", (event) => handler(event.data as HostMessage));
}
