// Words the window shows for each state. Pure functions, so tests check them directly.

import type {
  HostTailscale,
  RestoreWizardStatus,
  ControllerWakeState,
  GameReadyCard,
  GameReadyController,
  GameReadyStatus,
  GameReadyWakeTest,
  HostLeanMetrics,
  HostLeanPlanSummary,
  HostLeanRunSummary,
  HostLeanStatus,
  HostUpdateProgress,
  HostUpdateStatus,
  TrayBackupStatus,
  GameAwareInfo,
  GameAwareSettings,
  GameVmAction,
} from "./bridge";

export const MIN_PASSPHRASE = 8;
export const MAX_PASSPHRASE = 256;

/** The same rules as AdminPassphrase.Validate in the tray, which checks again. Null when it is acceptable. */
export function passphraseProblem(passphrase: string, confirm: string): string | null {
  if (passphrase.length < MIN_PASSPHRASE) return `Use at least ${MIN_PASSPHRASE} characters.`;
  if (passphrase.length > MAX_PASSPHRASE) return `Use at most ${MAX_PASSPHRASE} characters.`;
  if (passphrase.trim().length === 0) return "The passphrase cannot be only spaces.";
  if (passphrase !== confirm) return "The passphrases do not match.";
  return null;
}

export function deviceCount(count: number): string {
  if (count === 0) return "No devices are paired yet.";
  return count === 1 ? "1 device is paired." : `${count} devices are paired.`;
}

/** "AB12CD34EF56..." as "AB12 CD34 EF56 7890", the first 16 hex digits. */
export function shortFingerprint(fingerprint: string): string {
  return (fingerprint.replace(/[^0-9a-fA-F]/g, "").slice(0, 16).match(/.{1,4}/g) ?? []).join(" ").toUpperCase();
}

export function updateModeText(update: HostUpdateStatus): string {
  switch (update.mode) {
    case "notify":
      return "Updates download automatically; you choose when to install.";
    case "off":
      return "Automatic checks are off.";
    default:
      return update.maintenanceTime
        ? `Updates install automatically when nothing is in progress, or after ${update.maintenanceTime}.`
        : "Updates install automatically when nothing is in progress.";
  }
}

/** One line about where the update stands. */
export function updateSummary(update: HostUpdateStatus, checking: boolean): string {
  if (!update.supported) return update.message ?? "This host does not update itself.";
  if (checking || update.activity === "checking") return "Checking for updates…";
  switch (update.activity) {
    case "preparing":
      return `Downloading and testing version ${update.availableVersion}…`;
    case "ready":
      return update.mode === "auto"
        ? `Version ${update.availableVersion} is ready. It installs when nothing is in progress, or at the maintenance time.`
        : `Version ${update.availableVersion} is ready to install. The host restarts.`;
    case "installing":
      return `Installing version ${update.availableVersion}. The host restarts and is back in a minute or two.`;
    default:
      return update.message ?? (update.lastCheck ? "Up to date." : "Not checked yet.");
  }
}

export type StepState = "done" | "active" | "waiting";

/** The update's steps and where each stands, for the stepper. */
export function updateSteps(update: HostUpdateStatus, checking: boolean): { label: string; state: StepState }[] {
  const labels = ["Check", "Download and test", "Ready", "Install"];
  const active =
    checking || update.activity === "checking"
      ? 0
      : update.activity === "preparing"
        ? 1
        : update.activity === "ready"
          ? 2
          : update.activity === "installing"
            ? 3
            : -1;
  return labels.map((label, index) => ({
    label,
    state: active < 0 ? "waiting" : index < active ? "done" : index === active ? "active" : "waiting",
  }));
}

/** "4:05" until the moment, or "0:00" once it has passed. */
export function countdown(expiresAt: string, now: number): string {
  const seconds = Math.max(0, Math.ceil((Date.parse(expiresAt) - now) / 1000));
  return `${Math.floor(seconds / 60)}:${String(seconds % 60).padStart(2, "0")}`;
}

/** "123456" as "123 456". */
export function formatPin(pin: string): string {
  return pin.length === 6 ? `${pin.slice(0, 3)} ${pin.slice(3)}` : pin;
}

const MB = 1024 * 1024;

/** What preparing a version is doing, and how far it is (a fraction, or null when it cannot tell). */
export function progressText(progress: HostUpdateProgress): { text: string; fraction: number | null } {
  switch (progress.step) {
    case "verifying":
      return { text: "Checking the downloaded package…", fraction: null };
    case "testing":
      return { text: "Testing the new version on a copy of this host's data…", fraction: null };
    default: {
      const fraction = progress.bytesTotal > 0 ? Math.min(1, progress.bytesDone / progress.bytesTotal) : null;
      const done = (progress.bytesDone / MB).toFixed(0);
      const total = (progress.bytesTotal / MB).toFixed(0);
      return { text: `Downloading: ${done} of ${total} MB (${Math.floor((fraction ?? 0) * 100)}%)`, fraction };
    }
  }
}

const HANDLER_LABELS: Record<string, string> = {
  services: "Services",
  startup: "Startup entries",
  registry: "Settings",
  power: "Power",
  appx: "Apps",
  programs: "Programs",
};

export function handlerLabel(handler: string): string {
  return HANDLER_LABELS[handler] ?? handler;
}

/** The dry run's changes grouped by handler, in a fixed order. */
export function groupChanges(plan: HostLeanPlanSummary): { handler: string; label: string; lines: HostLeanPlanSummary["changes"] }[] {
  const order = Object.keys(HANDLER_LABELS);
  const handlers = [...new Set(plan.changes.map((line) => line.handler))].sort((a, b) => {
    const left = order.indexOf(a);
    const right = order.indexOf(b);
    return (left < 0 ? order.length : left) - (right < 0 ? order.length : right) || a.localeCompare(b);
  });
  return handlers.map((handler) => ({ handler, label: handlerLabel(handler), lines: plan.changes.filter((line) => line.handler === handler) }));
}

/** One line about where the Lean host action stands. */
export function leanSummary(status: HostLeanStatus): string {
  if (!status.supported) return status.unsupportedReason ?? "The Lean host action needs the installed HyperHarbor service.";
  switch (status.busy) {
    case "dryRun":
      return "Reading this PC. Nothing is changed.";
    case "apply":
      return "Applying the profile. This can take a few minutes.";
    case "undo":
      return "Undoing the earlier changes.";
  }
  const plan = status.dryRun;
  if (plan) {
    const count = plan.changes.length;
    const what = plan.source === "undo" ? "undo" : "profile";
    if (count === 0) return `Dry run done: this PC already matches the ${what}. Nothing to change.`;
    return plan.canApply
      ? `Dry run done: ${count === 1 ? "1 change" : `${count} changes`} to make. Review them, then apply.`
      : "The dry run is out of date. Run it again before applying.";
  }
  return status.applied ? "Applied. Run a dry run to see what has drifted." : "Not applied yet. Start with a dry run.";
}

/** "+12" or "-340", or "0" when nothing changed. */
export function signed(value: number): string {
  return value > 0 ? `+${value}` : value < 0 ? `-${Math.abs(value)}` : "0";
}

export function metricsText(metrics: HostLeanMetrics): string {
  const memory = metrics.usedMemoryMb >= 1024 ? `${(metrics.usedMemoryMb / 1024).toFixed(1)} GB` : `${metrics.usedMemoryMb} MB`;
  return `${memory} in use, ${metrics.processCount} processes${metrics.idle ? "" : " (CPU was busy)"}`;
}

/** Before and after of the last run, with the change. Null when either was not recorded. */
export function runDelta(run: HostLeanRunSummary): { memory: string; processes: string } | null {
  if (!run.before || !run.after) return null;
  return {
    memory: `${signed(run.after.usedMemoryMb - run.before.usedMemoryMb)} MB`,
    processes: signed(run.after.processCount - run.before.processCount),
  };
}

export type CardTone = "ok" | "warn" | "busy" | "neutral";

/** What the card on the Game ready page is doing now: a fix running, or the first read of the PC. */
export function cardBusy(status: GameReadyStatus, card: GameReadyCard): boolean {
  return status.busy === `fix:${card.id}` || (card.id === "consoleSignIn" && status.busy === "consoleSignIn") || (card.id === "powerMode" && status.busy === "powerMode") || (card.id === "ambientScreen" && status.busy === "ambient");
}

export function cardTone(status: GameReadyStatus, card: GameReadyCard): CardTone {
  if (cardBusy(status, card)) return "busy";
  switch (card.state) {
    case "ready":
      return "ok";
    case "attention":
      return "warn";
    default:
      return "neutral";
  }
}

/** The card's status line; a card still being fixed says so, and a coming-soon card says that first. */
export function cardStatus(status: GameReadyStatus, card: GameReadyCard): string {
  if (cardBusy(status, card)) return card.id === "consoleSignIn" || card.id === "powerMode" || card.id === "ambientScreen" ? "Working…" : "Fixing…";
  if (card.state === "comingSoon") return "Coming soon";
  if (card.state === "manual") return "Check by hand";
  return card.summary;
}

/** How many of the cards that can be evaluated are ready, for the page's headline. */
export function gameReadySummary(status: GameReadyStatus): string {
  if (!status.readAt) return "Reading this PC…";
  const evaluated = status.cards.filter((card) => card.state === "ready" || card.state === "attention");
  const ready = evaluated.filter((card) => card.state === "ready").length;
  if (evaluated.length === 0) return "Nothing to check yet.";
  return ready === evaluated.length ? "Everything checked is ready for games." : `${ready} of ${evaluated.length} checks are ready.`;
}

export function wakeLabel(wake: ControllerWakeState): string {
  switch (wake) {
    case "armed":
      return "Armed";
    case "canArm":
      return "Can arm";
    default:
      return "Cannot wake";
  }
}

export function connectionLabel(controller: GameReadyController): string {
  const what = controller.kind === "receiver" ? "Receiver" : "Controller";
  switch (controller.connection) {
    case "usb":
      return controller.kind === "receiver" ? "Receiver on USB" : "Wired or 2.4 GHz dongle";
    case "receiver":
      return "Through a wireless receiver";
    case "bluetooth":
      return "Bluetooth";
    default:
      return what;
  }
}

/** The wake test line shown on the controller card; null when no test has run. */
export function wakeTestText(test: GameReadyWakeTest, now: number = Date.now()): string | null {
  switch (test.state) {
    case "countdown": {
      const seconds = test.sleepAt ? Math.max(0, Math.ceil((Date.parse(test.sleepAt) - now) / 1000)) : 0;
      return `The PC goes to sleep in ${seconds} ${seconds === 1 ? "second" : "seconds"}. Press a controller button to wake it.`;
    }
    case "sleeping":
      return "Going to sleep. Press a controller button to wake the PC.";
    case "done":
    case "failed":
      return test.message ?? null;
    default:
      return null;
  }
}

export interface BackupCardText {
  status: string;
  tone: CardTone;
  lines: string[];
}

/** The Backups card: whether anything is backed up, and what needs attention. */
export function backupCardText(backup: TrayBackupStatus | null, now: number = Date.now()): BackupCardText {
  if (backup === null) return { status: "Unknown until the service runs.", tone: "neutral", lines: [] };
  if (!backup.configured) {
    const lines = ["Add a backup target from a paired device (Host menu, Backups), and save its recovery key."];
    if (backup.pendingKeys > 0) lines.unshift("A target is waiting for its recovery key to be confirmed.");
    return { status: "Not set up", tone: "warn", lines };
  }

  const failed = backup.items.filter((item) => item.outcome === "failed");
  const overdue = backup.items.filter((item) => item.overdue);
  const lines = failed.map((item) => `${item.name} to ${item.targetName} failed${item.reason ? `: ${item.reason}` : "."}`);
  for (const item of overdue) lines.push(`${item.name} has no good backup on ${item.targetName} for a while.`);
  if (backup.pendingKeys > 0) lines.push("A target is waiting for its recovery key to be confirmed.");
  if (backup.items.length > 0 && lines.length === 0) {
    const latest = backup.items
      .map((item) => item.lastSuccessAt)
      .filter((at): at is string => !!at)
      .sort()
      .at(-1);
    lines.push(latest ? `Last good backup ${backupAge(latest, now)}.` : "No backup has finished yet.");
  }

  if (backup.running) return { status: "Backing up…", tone: "busy", lines };
  if (failed.length > 0 || overdue.length > 0) return { status: "Needs attention", tone: "warn", lines };
  return { status: backup.items.length === 0 ? "Ready" : "Up to date", tone: "ok", lines };
}

function backupAge(iso: string, now: number): string {
  const minutes = Math.max(0, Math.round((now - new Date(iso).getTime()) / 60_000));
  if (minutes < 1) return "just now";
  if (minutes < 60) return `${minutes} ${minutes === 1 ? "minute" : "minutes"} ago`;
  const hours = Math.round(minutes / 60);
  if (hours < 24) return `${hours} ${hours === 1 ? "hour" : "hours"} ago`;
  const days = Math.round(hours / 24);
  return days === 1 ? "yesterday" : `${days} days ago`;
}

export const ACTION_LABELS: Record<GameVmAction, string> = {
  pause: "Pause",
  save: "Save and stop",
  keep: "Keep running",
};

/** Program names as the service stores them: lower case, no folder, no ".exe". */
export function normalizeProgram(text: string): string {
  const file = text.trim().replace(/\\/g, "/").split("/").pop() ?? "";
  const lower = file.toLowerCase();
  return lower.endsWith(".exe") ? lower.slice(0, -4) : lower;
}

/** Adds programs typed as a list (commas, semicolons, or lines); duplicates and empty entries are dropped. */
export function addPrograms(list: readonly string[], text: string): string[] {
  const added = text
    .split(/[\n,;]+/)
    .map(normalizeProgram)
    .filter((name) => name.length > 0);
  return [...new Set([...list, ...added])];
}

/** The settings with one VM's action changed; choosing the default removes the VM's own entry. */
export function withVmAction(settings: GameAwareSettings, vmId: string, action: GameVmAction, alwaysPause: boolean): GameAwareSettings {
  const others = settings.vmPolicies.filter((policy) => policy.vmId !== vmId);
  const isDefault = action === settings.defaultAction && !alwaysPause;
  return { ...settings, vmPolicies: isDefault ? others : [...others, { vmId, action, alwaysPause }] };
}

/** The action a VM gets; a GPU partition forces pause. */
export function effectiveAction(info: GameAwareInfo, vmId: string): { action: GameVmAction; alwaysPause: boolean; forced: boolean } {
  const own = info.settings.vmPolicies.find((policy) => policy.vmId === vmId);
  const forced = info.vms.find((vm) => vm.vmId === vmId)?.gpuPartition ?? false;
  return { action: forced ? "pause" : (own?.action ?? info.settings.defaultAction), alwaysPause: own?.alwaysPause ?? false, forced };
}

/** The status line of the Game-aware page. */
/** "11 Oct 2026, 02:00 (64 GB)" for a snapshot in a list. */
export function snapshotLabel(snapshot: { startTime: string; totalSize: number }): string {
  const when = new Date(snapshot.startTime).toLocaleString(undefined, { dateStyle: "medium", timeStyle: "short" });
  const gb = snapshot.totalSize / 1024 ** 3;
  const size = gb >= 1 ? `${gb.toFixed(gb >= 100 ? 0 : 1)} GB` : `${Math.max(1, Math.round(snapshot.totalSize / 1024 ** 2))} MB`;
  return `${when} (${size})`;
}

/** The Restore card's status line. */
export function restoreStatus(wizard: RestoreWizardStatus | null): { status: string; tone: CardTone } {
  if (wizard === null) return { status: "Unknown until the service runs.", tone: "neutral" };
  switch (wizard.phase) {
    case "attaching":
      return { status: "Opening the backups…", tone: "busy" };
    case "restoring":
      return { status: "Restoring…", tone: "busy" };
    case "ready":
      return { status: wizard.error ? "The last restore failed" : "Choose what to restore", tone: wizard.error ? "warn" : "ok" };
    default:
      return { status: wizard.error ? "The backups could not be opened" : "Enter where your backups are", tone: wizard.error ? "warn" : "neutral" };
  }
}

/** The Tailscale card's status line on the Overview. Tailscale is optional, so "not installed" is neutral. */
export function tailscaleStatus(info: HostTailscale | null | undefined): { status: string; tone: CardTone } {
  if (!info) return { status: "Unknown until the service runs.", tone: "neutral" };
  switch (info.state) {
    case "notInstalled":
      return { status: "Not installed (optional)", tone: "neutral" };
    case "unreachable":
      return { status: "Installed, not answering", tone: "warn" };
    case "needsLogin":
      return { status: "Sign in to Tailscale", tone: "warn" };
    case "stopped":
      return { status: "Turned off", tone: "warn" };
    case "running":
      return info.tagged
        ? { status: `On the tailnet as ${info.dnsName ?? info.ipv4 ?? "this PC"}`, tone: "ok" }
        : { status: "On the tailnet, not tagged", tone: "warn" };
    default:
      return { status: "Starting", tone: "busy" };
  }
}

export function gameAwareStatus(info: GameAwareInfo | null): { status: string; tone: CardTone } {
  if (info === null) return { status: "Unknown until the service runs.", tone: "neutral" };
  if (!info.settings.enabled) return { status: "Off", tone: "neutral" };
  if (!info.state.gaming) return { status: "On, no game running", tone: "ok" };
  const game = info.state.process ? ` (${info.state.process})` : "";
  return { status: `Game running${game}`, tone: "busy" };
}
