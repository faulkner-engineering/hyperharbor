// Words the window shows for each state. Pure functions, so tests check them directly.

import type { HostUpdateStatus } from "./bridge";

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
