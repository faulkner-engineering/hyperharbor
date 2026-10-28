import type { GuestVolumeEncryption } from "$lib/api/client";

const STATES: Record<GuestVolumeEncryption["state"], string> = {
  decrypted: "is not encrypted",
  encrypted: "is encrypted with BitLocker",
  encrypting: "is encrypting",
  decrypting: "is decrypting",
  paused: "has paused converting",
};

/** "C: (Windows) is decrypting, 42% still encrypted". */
export function volumeLine(volume: GuestVolumeEncryption): string {
  const name = volume.operatingSystem ? `${volume.mountPoint} (Windows)` : volume.mountPoint;
  const state = STATES[volume.state];
  const converting = volume.state === "encrypting" || volume.state === "decrypting" || volume.state === "paused";
  return converting ? `${name} ${state}, ${volume.encryptedPercent}% still encrypted` : `${name} ${state}`;
}

/** Whether some volume is encrypted, or on its way there, so turning encryption off does something. */
export const canTurnOff = (volumes: GuestVolumeEncryption[]): boolean =>
  volumes.some((volume) => volume.state === "encrypted" || volume.state === "encrypting" || volume.state === "paused");

/** Whether Windows is still converting a volume, so the state is worth reading again. */
export const inProgress = (volumes: GuestVolumeEncryption[]): boolean =>
  volumes.some((volume) => volume.state === "encrypting" || volume.state === "decrypting");
