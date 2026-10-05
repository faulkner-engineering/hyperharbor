import {
  connectHost,
  enableHostRemoteDesktop,
  getHostRemoteDesktop,
  type HostRemoteDesktop,
} from "$lib/api/client";
import { withElevation } from "$lib/lifecycle.svelte";

export type HostRemoteDesktopOutcome = "opened" | "declined" | "unsupported";

/**
 * Opens Remote Desktop to the host itself. When the host does not accept connections yet,
 * `confirmEnable` asks the user first; turning it on needs elevation (withElevation prompts
 * for the passphrase, and throws ElevationCancelled if the user cancels).
 */
export async function openHostRemoteDesktop(
  key: string,
  confirmEnable: (state: HostRemoteDesktop) => Promise<boolean>,
): Promise<{ outcome: HostRemoteDesktopOutcome; state: HostRemoteDesktop }> {
  let state = await getHostRemoteDesktop(key);
  if (!state.supported) return { outcome: "unsupported", state };

  if (!state.enabled || !state.firewallOpen) {
    if (!(await confirmEnable(state))) return { outcome: "declined", state };
    state = await withElevation(key, () => enableHostRemoteDesktop(key));
  }

  await connectHost(key);
  return { outcome: "opened", state };
}
