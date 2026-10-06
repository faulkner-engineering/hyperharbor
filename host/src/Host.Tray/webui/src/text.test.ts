import { describe, expect, it } from "vitest";
import type { HostUpdateStatus } from "./bridge";
import { countdown, deviceCount, formatPin, passphraseProblem, shortFingerprint, updateSteps, updateSummary } from "./text";

const update = (overrides: Partial<HostUpdateStatus> = {}): HostUpdateStatus => ({
  supported: true,
  mode: "auto",
  channel: "stable",
  channels: ["beta", "stable"],
  maintenanceTime: null,
  currentVersion: "0.1.4",
  availableVersion: null,
  notesUrl: null,
  activity: "idle",
  lastCheck: "2026-10-05T12:00:00Z",
  message: null,
  lastResult: null,
  rolledBack: [],
  ...overrides,
});

describe("passphraseProblem", () => {
  it("follows the tray's rules", () => {
    expect(passphraseProblem("short", "short")).toMatch(/at least 8/);
    expect(passphraseProblem("        ", "        ")).toMatch(/only spaces/);
    expect(passphraseProblem("x".repeat(257), "x".repeat(257))).toMatch(/at most 256/);
    expect(passphraseProblem("correct horse", "correct hose")).toMatch(/do not match/);
    expect(passphraseProblem("correct horse", "correct horse")).toBeNull();
  });
});

describe("words", () => {
  it("counts devices", () => {
    expect(deviceCount(0)).toBe("No devices are paired yet.");
    expect(deviceCount(1)).toBe("1 device is paired.");
    expect(deviceCount(3)).toBe("3 devices are paired.");
  });

  it("shortens fingerprints and spaces PINs", () => {
    expect(shortFingerprint("ab:12:cd:34:ef:56:78:90:aa:bb")).toBe("AB12 CD34 EF56 7890");
    expect(formatPin("440141")).toBe("440 141");
  });

  it("counts down to a moment and stops at zero", () => {
    const now = Date.parse("2026-10-05T12:00:00Z");
    expect(countdown("2026-10-05T12:02:05Z", now)).toBe("2:05");
    expect(countdown("2026-10-05T11:59:00Z", now)).toBe("0:00");
  });
});

describe("updates", () => {
  it("says where an update stands", () => {
    expect(updateSummary(update(), false)).toBe("Up to date.");
    expect(updateSummary(update({ lastCheck: null }), false)).toBe("Not checked yet.");
    expect(updateSummary(update(), true)).toBe("Checking for updates…");
    expect(updateSummary(update({ activity: "preparing", availableVersion: "0.1.5" }), false)).toBe(
      "Downloading and testing version 0.1.5…",
    );
    expect(updateSummary(update({ activity: "ready", mode: "notify", availableVersion: "0.1.5" }), false)).toMatch(/ready to install/);
    expect(updateSummary(update({ supported: false, message: "Install the host first." }), false)).toBe("Install the host first.");
  });

  it("marks the steps done, active, or waiting", () => {
    const states = (status: HostUpdateStatus, checking = false) => updateSteps(status, checking).map((step) => step.state);
    expect(states(update(), true)).toEqual(["active", "waiting", "waiting", "waiting"]);
    expect(states(update({ activity: "preparing" }))).toEqual(["done", "active", "waiting", "waiting"]);
    expect(states(update({ activity: "ready" }))).toEqual(["done", "done", "active", "waiting"]);
    expect(states(update({ activity: "installing" }))).toEqual(["done", "done", "done", "active"]);
  });
});
