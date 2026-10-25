import { errorMessage, hasProblemCode, openGuestAdmin, ProblemCodes, type GuestAdminWindow } from "$lib/api/client";
import { withElevation } from "$lib/lifecycle.svelte";

/**
 * Opens a short administrator window in a Windows guest, or returns the open one. Opening needs elevation:
 * withElevation prompts for the admin passphrase when the host asks, and throws ElevationCancelled if the user cancels.
 */
export const requestGuestAdmin = (key: string, vmId: string): Promise<GuestAdminWindow> =>
  withElevation(key, () => openGuestAdmin(key, vmId));

/** The passphrase's words and its digit, shown one by one: "Maple-River-Candle-Orbit-7" has five parts. */
export const passphraseParts = (passphrase: string): string[] => passphrase.split("-");

/** Milliseconds until the window ends, never below zero. */
export function msLeft(expiresAt: string, now: number): number {
  return Math.max(0, Date.parse(expiresAt) - now);
}

/** Time left as minutes and seconds, for example "9:05". */
export function formatLeft(ms: number): string {
  const seconds = Math.ceil(ms / 1000);
  return `${Math.floor(seconds / 60)}:${String(seconds % 60).padStart(2, "0")}`;
}

/** What to tell the user when a window cannot be opened. */
export function guestAdminError(error: unknown, vmName: string): string {
  if (hasProblemCode(error, ProblemCodes.guestAdminUnsupported)) {
    return `Administrator access is for Windows VMs. ${vmName} runs Linux; use sudo there.`;
  }
  if (hasProblemCode(error, ProblemCodes.vmNotRunning)) return `${vmName} is not running. Start it, then try again.`;
  if (hasProblemCode(error, ProblemCodes.vmNotProvisioned)) {
    return `${vmName} is not set up yet. Set it up first; administrator access uses the same setup.`;
  }
  if (hasProblemCode(error, ProblemCodes.guestUnavailable)) {
    return `Windows in ${vmName} is not ready yet. Wait until it has finished starting, then try again.`;
  }
  return errorMessage(error);
}
