import { getElevation, getJob, hasProblemCode, ProblemCodes, type VmJob } from "$lib/api/client";

interface PendingElevation {
  hostKey: string;
  resolve: (elevated: boolean) => void;
}

/**
 * The passphrase prompt shared by every elevated action. ElevationDialog shows it while `pending`
 * is set; `expiresAt` holds when each host's elevation ends, for the header badge.
 */
class ElevationState {
  pending = $state<PendingElevation | null>(null);
  expiresAt = $state<Record<string, string>>({});

  /** Shows the prompt. Resolves to true once the device is elevated, or false if the user cancels. */
  request(hostKey: string): Promise<boolean> {
    // A second request while one is open waits for the same answer.
    const open = this.pending;
    if (open && open.hostKey === hostKey) {
      return new Promise((resolve) => {
        const first = open.resolve;
        open.resolve = (elevated) => {
          first(elevated);
          resolve(elevated);
        };
      });
    }

    open?.resolve(false);
    return new Promise((resolve) => {
      this.pending = { hostKey, resolve };
    });
  }

  finish(elevated: boolean, expiresAt?: string) {
    const pending = this.pending;
    if (!pending) return;
    if (elevated && expiresAt) {
      this.expiresAt = { ...this.expiresAt, [pending.hostKey]: expiresAt };
    }
    this.pending = null;
    pending.resolve(elevated);
  }

  forget(hostKey: string) {
    const rest = { ...this.expiresAt };
    delete rest[hostKey];
    this.expiresAt = rest;
  }
}

export const elevation = new ElevationState();

/** Thrown when the user cancels the passphrase prompt. Dialogs treat it as "nothing happened". */
export class ElevationCancelled extends Error {
  constructor() {
    super("Cancelled.");
  }
}

/**
 * Runs `action`. If the host asks for elevation, shows the passphrase prompt once and runs it again.
 */
export async function withElevation<T>(hostKey: string, action: () => Promise<T>): Promise<T> {
  try {
    return await action();
  } catch (error) {
    if (!hasProblemCode(error, ProblemCodes.elevationRequired)) throw error;
    elevation.forget(hostKey);
    if (!(await elevation.request(hostKey))) throw new ElevationCancelled();
    return await action();
  }
}

/**
 * Makes sure the device is elevated before a request that should not be sent twice, such as an
 * upload of several gigabytes: the host refuses an unelevated one before reading its body.
 */
export async function ensureElevated(hostKey: string): Promise<void> {
  const status = await getElevation(hostKey);
  if (status.active) return;
  if (!status.configured) {
    throw new Error(
      "No admin passphrase is set on this host. Set one from the HyperHarbor Host window on the host (double-click the tray icon).",
    );
  }
  elevation.forget(hostKey);
  if (!(await elevation.request(hostKey))) throw new ElevationCancelled();
}

/** Polls a job until it is no longer running, reporting each update. */
export async function waitForJob(
  hostKey: string,
  job: VmJob,
  onUpdate: (job: VmJob) => void,
  intervalMs = 1000,
): Promise<VmJob> {
  let current = job;
  onUpdate(current);
  while (current.state === "running") {
    await new Promise((resolve) => setTimeout(resolve, intervalMs));
    current = await getJob(hostKey, current.id);
    onUpdate(current);
  }
  return current;
}

/** Megabytes to gigabytes for display, for example 4096 to "4". */
export const toGb = (mb: number) => Math.round((mb / 1024) * 10) / 10;

/** Gigabytes from a form to the even number of megabytes Hyper-V needs. */
export const toMb = (gb: number) => Math.max(2, Math.round((gb * 1024) / 2) * 2);
