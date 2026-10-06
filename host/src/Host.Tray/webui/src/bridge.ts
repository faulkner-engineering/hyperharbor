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
