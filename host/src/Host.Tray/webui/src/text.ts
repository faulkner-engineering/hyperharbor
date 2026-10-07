// Words the window shows for each state. Pure functions, so tests check them directly.

import type { HostLeanMetrics, HostLeanPlanSummary, HostLeanRunSummary, HostLeanStatus, HostUpdateProgress, HostUpdateStatus } from "./bridge";

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
