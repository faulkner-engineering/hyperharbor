import type { GameAwareInfo, GameAwareSettings, GameAwareState, GameVmAction } from "$lib/api/client";

export const ACTION_LABELS: Record<GameVmAction, string> = {
  pause: "Pause",
  save: "Save and stop",
  keep: "Keep running",
};

/** Program names as the host stores them: lower case, no folder, no ".exe". */
export function normalizeProgram(text: string): string {
  const file = text.trim().replace(/\\/g, "/").split("/").pop() ?? "";
  const lower = file.toLowerCase();
  return lower.endsWith(".exe") ? lower.slice(0, -4) : lower;
}

/** Adds programs typed or pasted as a list (commas, semicolons, or lines). Duplicates and empty entries are dropped. */
export function addPrograms(list: readonly string[], text: string): string[] {
  const added = text
    .split(/[\n,;]+/)
    .map(normalizeProgram)
    .filter((name) => name.length > 0);
  return [...new Set([...list, ...added])];
}

/** The settings with one VM's action changed. Choosing the default removes the VM's own entry. */
export function withVmAction(
  settings: GameAwareSettings,
  vmId: string,
  action: GameVmAction,
  alwaysPause: boolean,
): GameAwareSettings {
  const others = settings.vmPolicies.filter((policy) => policy.vmId !== vmId);
  const isDefault = action === settings.defaultAction && !alwaysPause;
  return { ...settings, vmPolicies: isDefault ? others : [...others, { vmId, action, alwaysPause }] };
}

/** The action a VM gets, and whether a GPU partition forced it. */
export function effectiveAction(info: GameAwareInfo, vmId: string): { action: GameVmAction; alwaysPause: boolean; forced: boolean } {
  const own = info.settings.vmPolicies.find((policy) => policy.vmId === vmId);
  const forced = info.vms.find((vm) => vm.vmId === vmId)?.gpuPartition ?? false;
  return {
    action: forced ? "pause" : (own?.action ?? info.settings.defaultAction),
    alwaysPause: own?.alwaysPause ?? false,
    forced,
  };
}

/** The line the client shows while the host is gaming, or null when there is nothing to say. */
export function gamingNotice(state: GameAwareState): string | null {
  if (!state.gaming) return null;
  const game = state.process ? ` (${state.process})` : "";
  const parts = [`The host is gaming${game}.`];
  if (state.changedVms.length > 0) parts.push(`Paused or saved: ${state.changedVms.join(", ")}.`);
  if (state.sessionVms.length > 0) parts.push(`Still running because you are connected: ${state.sessionVms.join(", ")}. They may be slow.`);
  return parts.join(" ");
}
