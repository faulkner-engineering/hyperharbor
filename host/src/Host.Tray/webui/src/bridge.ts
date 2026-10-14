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

export interface ConsoleSignInLauncher {
  id: string;
  name: string;
  available: boolean;
  reason?: string | null;
}

/** What the Console sign-in form proposes. The password is never sent to the page. */
export interface ConsoleSignInInfo {
  userName: string;
  launcher: string;
  configured: boolean;
  launchers: ConsoleSignInLauncher[];
}

export type PowerModeId = "consoleIdle" | "balanced" | "sleepWhenIdle";

export interface PowerModeEstimate {
  watts: number;
  alwaysReadyKwh: number;
  alwaysReadyMonthlyCost: number;
  scheduledKwh: number;
  scheduledMonthlyCost: number;
  sleepWatts: number;
}

export interface PowerModeInfo {
  mode: PowerModeId;
  displayMinutes: number;
  scheduleEnabled: boolean;
  /** Local time, HH:mm. */
  sleepTime: string;
  wakeTime: string;
  watts: number;
  ratePerKwh: number;
  applied: boolean;
  estimate: PowerModeEstimate;
  /** Measured while in Console idle; null when not measured or not reported by Windows. */
  cpuPercent?: number | null;
  gpuPercent?: number | null;
}

/** The Windows screensaver as the tray found it on the desktop. */
export interface AmbientScreensaverState {
  active: boolean;
  path?: string | null;
  timeoutSeconds: number;
  policyLocked: boolean;
}

/** Mirrors Shared.Contracts/Ipc/AmbientMessages.cs (AmbientInfo). Null properties are left out by the JSON options. */
export interface AmbientInfo {
  enabled: boolean;
  idleMinutes: number;
  showHostStatus: boolean;
  showRunningVms: boolean;
  showDevices: boolean;
  softLock: boolean;
  /** Buttons joined by "+", for example "lb+rb+start". */
  chord: string;
  supported: boolean;
  unsupportedReason?: string | null;
  screensaverPath?: string | null;
  screensaver?: AmbientScreensaverState | null;
  /** Measured while the screen ran; null before it has run for a minute. */
  cpuPercent?: number | null;
  gpuPercent?: number | null;
  measuredSeconds?: number | null;
  cecDevice?: string | null;
}

/** What the tray writes for the ambient screensaver's page (AmbientScreenData). */
export interface AmbientData {
  hostStatus?: string | null;
  runningVms?: string[] | null;
  devices?: string | null;
  chordHint?: string | null;
  backup?: string | null;
  gaming?: string | null;
}

/** The host's Tailscale state (HostTailscale in docs/api.yaml). */
export interface HostTailscale {
  state: "notInstalled" | "unreachable" | "needsLogin" | "stopped" | "running" | "other";
  error?: string | null;
  ipv4?: string | null;
  dnsName?: string | null;
  tagged: boolean;
  tag: string;
  upCommand: string;
  policySnippet: string;
  config: { localApiPort: number; controlUrl?: string | null };
}

export interface GameReadyStatus {
  readAt?: string | null;
  /** "reading", "fix:<card id>", or null when idle. */
  busy?: string | null;
  error?: string | null;
  cards: GameReadyCard[];
  wakeTest: GameReadyWakeTest;
  consoleSignIn?: ConsoleSignInInfo | null;
  powerMode?: PowerModeInfo | null;
  ambient?: AmbientInfo | null;
  tailscale?: HostTailscale | null;
}

export interface TrayViewDevice {
  id: string;
  name: string;
  fingerprint: string;
  pairedAt: string;
}

export type GameVmAction = "pause" | "save" | "keep";

export interface GameVmPolicy {
  vmId: string;
  action: GameVmAction;
  alwaysPause?: boolean;
}

/** Game-aware mode choices (GameAwareSettings in docs/api.yaml). */
export interface GameAwareSettings {
  enabled: boolean;
  defaultAction: GameVmAction;
  allowlist: string[];
  ignoreList: string[];
  vmPolicies: GameVmPolicy[];
  /** Launchers added to the built-in ones. */
  launchers?: string[];
}

export interface GameAwareInfo {
  settings: GameAwareSettings;
  state: {
    enabled: boolean;
    gaming: boolean;
    process?: string | null;
    reason?: string | null;
    since?: string | null;
    changedVms: string[];
    sessionVms: string[];
  };
  vms: { vmId: string; name: string; gpuPartition: boolean }[];
}

/** What the Restore page sends to check a backup location (RestoreWizardForm). Secrets are sent once and not kept by the page. */
export interface RestoreWizardForm {
  kind: "local" | "s3" | "azure";
  name: string;
  recoveryKey: string;
  path?: string;
  endpoint?: string;
  bucket?: string;
  region?: string;
  prefix?: string;
  useTls?: boolean;
  accessKey?: string;
  secretKey?: string;
  azureAccount?: string;
  azureContainer?: string;
  azureKey?: string;
}

export interface RestoreWizardSnapshot {
  id: string;
  startTime: string;
  totalSize: number;
}

export interface RestoreWizardVm {
  vmId: string;
  name?: string | null;
  snapshots: RestoreWizardSnapshot[];
}

/** The Restore page's state (RestoreWizardStatus). Phase: idle, attaching, ready, or restoring. */
export interface RestoreWizardStatus {
  phase: "idle" | "attaching" | "ready" | "restoring";
  step: string;
  percent: number;
  error?: string | null;
  targetName?: string | null;
  vms: RestoreWizardVm[];
  hostSnapshots: RestoreWizardSnapshot[];
  messages: string[];
  restartRequired: boolean;
}

/** One backed-up item on one target (TrayBackupItem). */
export interface TrayBackupItem {
  name: string;
  targetName: string;
  /** succeeded, partial, skipped, or failed; absent when it never ran. */
  outcome?: string | null;
  finishedAt?: string | null;
  reason?: string | null;
  lastSuccessAt?: string | null;
  overdue: boolean;
  runId?: string | null;
}

/** The backup summary from the service (TrayBackupStatus). */
export interface TrayBackupStatus {
  configured: boolean;
  pendingKeys: number;
  running: boolean;
  waiting: number;
  items: TrayBackupItem[];
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
  /** Null while the service is not connected. */
  backup: TrayBackupStatus | null;
  /** Null while the service is not connected. */
  gameAware: GameAwareInfo | null;
  /** Null while the service is not connected. */
  restoreWizard: RestoreWizardStatus | null;
  /** Work in progress: passphrase, console, packageSearch, folder:vm|iso|backup, device:<id>, update. */
  busy: string[];
  pairing: { pairingId: string; deviceName: string; pin: string; expiresAt: string } | null;
  dataDirectory: string;
}

export type ToastKind = "info" | "success" | "error";

export type HostMessage =
  | { type: "state"; value: TrayViewState }
  | { type: "toast"; kind: ToastKind; title: string; text: string }
  | { type: "navigate"; page: string }
  // Sent to the ambient screen only: its data (the tray writes it) and the unlock chord hint.
  | ({ type: "data" } & AmbientData)
  | { type: "hint"; seconds: number };

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
  | { type: "gameAwareApply"; settings: GameAwareSettings }
  | { type: "gameAwareNotAGame" }
  | { type: "backupRunNow" }
  | { type: "restoreWizardAttach"; form: RestoreWizardForm }
  | { type: "restoreWizardRestore"; kind: "host" | "vm"; snapshotId: string; name?: string; start?: boolean }
  | { type: "restoreWizardReset" }
  | { type: "gameReadyFix"; cardId: string }
  | { type: "gameReadyTestWake"; delaySeconds: number }
  | { type: "gameReadyConsoleSignInEnable"; userName: string; launcher: string }
  | { type: "gameReadyConsoleSignInDisable"; deleteUser: boolean }
  | {
      type: "gameReadyPowerModeApply";
      mode: PowerModeId;
      displayMinutes: number;
      scheduleEnabled: boolean;
      sleepTime: string;
      wakeTime: string;
      watts: number;
      ratePerKwh: number;
    }
  | {
      type: "gameReadyAmbientApply";
      enabled: boolean;
      idleMinutes: number;
      showHostStatus: boolean;
      showRunningVms: boolean;
      showDevices: boolean;
      softLock: boolean;
      chord: string;
    }
  | { type: "gameReadySleepNow" }
  | { type: "gameReadyTailscaleSettings"; localApiPort: number; controlUrl?: string }
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
