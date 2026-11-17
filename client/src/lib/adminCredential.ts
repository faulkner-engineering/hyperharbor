import { errorMessage, hasProblemCode, ProblemCodes, type Vm, type VmAdminCredential } from "#lib/api/client.ts";

/** After this long without an answer, a check or a save says so (docs/ui-principles.md, deadlines). */
export const SLOW_AFTER_MS = 20_000;

/** Where the steps for an administrator account that expired with a password nobody knows are written down. */
export const RECOVERY_GUIDE = "github.com/faulkner-engineering/hyperharbor/blob/main/docs/recover-vm-administrator.md";

function when(iso: string): string {
  return new Date(iso).toLocaleString([], { month: "long", day: "numeric", hour: "numeric", minute: "2-digit" });
}

function day(iso: string): string {
  return new Date(iso).toLocaleDateString([], { month: "long", day: "numeric", year: "numeric" });
}

/** Whose account it is: the host keeps one it made from expiring, and only reports on one the user supplied. */
export function accountOrigin(credential: VmAdminCredential): string {
  return credential.managed
    ? "HyperHarbor created this account when it installed the VM, and only the host knows its password."
    : "You gave HyperHarbor this account when you set the VM up.";
}

/** What the host last saw when it signed in with the stored credential. */
export function stateLine(credential: VmAdminCredential, vmName: string): string {
  const at = credential.checkedAt ? ` (${when(credential.checkedAt)})` : "";
  switch (credential.state) {
    case "working":
      return `${vmName} accepted it when HyperHarbor last signed in${at}.`;
    case "rejected":
      return `${vmName} did not accept it when HyperHarbor last tried${at}.`;
    default:
      return "HyperHarbor has not signed in with it since the host was updated. Check now to see whether it works.";
  }
}

/** When the account's password expires, or null when there is nothing to say (Linux, or never read). */
export function expiryLine(credential: VmAdminCredential, vm: Pick<Vm, "guestOs">, now: number = Date.now()): string | null {
  const expires = credential.passwordExpiresAt;
  if (expires) {
    return Date.parse(expires) <= now ? `Its password expired on ${day(expires)}.` : `Its password expires on ${day(expires)}.`;
  }

  return credential.state === "working" && vm.guestOs.family === "windows" ? "Its password does not expire." : null;
}

/** Whether turning off password expiry can be offered: a Windows account that works and still has a date. */
export const canStopExpiry = (credential: VmAdminCredential, vm: Pick<Vm, "guestOs">): boolean =>
  credential.state === "working" && credential.passwordExpiresAt !== null && vm.guestOs.family !== "linux";

/** What to tell the user when a check or a save fails. */
export function adminCredentialError(error: unknown, vmName: string): string {
  if (hasProblemCode(error, ProblemCodes.adminCredentialRejected)) {
    return `${vmName} did not accept that user name and password. Nothing was changed. (${errorMessage(error)})`;
  }
  if (hasProblemCode(error, ProblemCodes.vmNotRunning)) return `${vmName} is not running. Start it, then try again.`;
  return errorMessage(error);
}
