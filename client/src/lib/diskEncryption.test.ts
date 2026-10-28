import { describe, expect, it } from "vitest";
import type { GuestVolumeEncryption } from "$lib/api/client";
import { canTurnOff, inProgress, volumeLine } from "./diskEncryption";

const volume = (state: GuestVolumeEncryption["state"], extra: Partial<GuestVolumeEncryption> = {}): GuestVolumeEncryption => ({
  mountPoint: "C:",
  operatingSystem: true,
  state,
  encryptedPercent: state === "encrypted" ? 100 : 0,
  ...extra,
});

describe("disk encryption", () => {
  it("describes each volume", () => {
    expect(volumeLine(volume("encrypted"))).toBe("C: (Windows) is encrypted with BitLocker");
    expect(volumeLine(volume("decrypting", { encryptedPercent: 42 }))).toBe("C: (Windows) is decrypting, 42% still encrypted");
    expect(volumeLine(volume("decrypted", { mountPoint: "D:", operatingSystem: false }))).toBe("D: is not encrypted");
  });

  it("offers turning off only while something is encrypted", () => {
    expect(canTurnOff([volume("decrypted"), volume("encrypted", { mountPoint: "D:" })])).toBe(true);
    expect(canTurnOff([volume("paused")])).toBe(true);
    expect(canTurnOff([volume("decrypting")])).toBe(false);
    expect(canTurnOff([volume("decrypted")])).toBe(false);
  });

  it("keeps reading while Windows converts", () => {
    expect(inProgress([volume("decrypting")])).toBe(true);
    expect(inProgress([volume("encrypted"), volume("decrypted")])).toBe(false);
  });
});
