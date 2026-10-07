import { describe, expect, it } from "vitest";
import type { HostLeanRunSummary, HostLeanStatus, HostUpdateStatus } from "./bridge";
import { countdown, deviceCount, formatPin, groupChanges, leanSummary, metricsText, passphraseProblem, progressText, runDelta, shortFingerprint, signed, updateSteps, updateSummary } from "./text";

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

  it("says how far preparing a version has come", () => {
    const MB = 1024 * 1024;
    expect(progressText({ step: "downloading", bytesDone: 50 * MB, bytesTotal: 200 * MB })).toEqual({
      text: "Downloading: 50 of 200 MB (25%)",
      fraction: 0.25,
    });
    expect(progressText({ step: "verifying", bytesDone: 1, bytesTotal: 1 }).fraction).toBeNull();
    expect(progressText({ step: "testing", bytesDone: 1, bytesTotal: 1 }).text).toMatch(/Testing the new version/);
  });
});

const leanStatus = (overrides: Partial<HostLeanStatus> = {}): HostLeanStatus => ({
  supported: true,
  busy: null,
  profileName: "Host gaming",
  applied: false,
  undoAvailable: false,
  scheduleEnabled: false,
  ...overrides,
});

const planOf = (changes: { handler: string; item: string; text: string }[], canApply = true) => ({
  source: "lean" as const,
  at: "2026-10-05T12:00:00Z",
  canApply,
  changes,
  kept: [],
  problems: [],
  alreadyInPlace: 0,
});

describe("leanSummary", () => {
  it("covers each state", () => {
    expect(leanSummary(leanStatus({ supported: false, unsupportedReason: "Needs the service." }))).toBe("Needs the service.");
    expect(leanSummary(leanStatus({ busy: "dryRun" }))).toMatch(/Nothing is changed/);
    expect(leanSummary(leanStatus({ busy: "apply" }))).toMatch(/Applying/);
    expect(leanSummary(leanStatus({ busy: "undo" }))).toMatch(/Undoing/);
    expect(leanSummary(leanStatus())).toMatch(/Start with a dry run/);
    expect(leanSummary(leanStatus({ applied: true }))).toMatch(/Applied/);
  });

  it("describes a dry run by its changes and whether it is still current", () => {
    const one = [{ handler: "services", item: "DiagTrack", text: "x" }];
    expect(leanSummary(leanStatus({ dryRun: planOf([]) }))).toMatch(/Nothing to change/);
    expect(leanSummary(leanStatus({ dryRun: planOf(one) }))).toMatch(/1 change to make/);
    expect(leanSummary(leanStatus({ dryRun: planOf([...one, ...one]) }))).toMatch(/2 changes/);
    expect(leanSummary(leanStatus({ dryRun: planOf(one, false) }))).toMatch(/out of date/);
  });
});

describe("groupChanges", () => {
  it("orders handlers in a fixed order and keeps unknown ones last", () => {
    const groups = groupChanges(
      planOf([
        { handler: "other", item: "a", text: "" },
        { handler: "appx", item: "b", text: "" },
        { handler: "services", item: "c", text: "" },
        { handler: "services", item: "d", text: "" },
      ]),
    );
    expect(groups.map((group) => group.label)).toEqual(["Services", "Apps", "other"]);
    expect(groups[0].lines).toHaveLength(2);
  });
});

describe("lean numbers", () => {
  it("signs deltas and describes metrics", () => {
    expect(signed(5)).toBe("+5");
    expect(signed(-5)).toBe("-5");
    expect(signed(0)).toBe("0");
    expect(metricsText({ at: "", usedMemoryMb: 512, processCount: 90, idle: true })).toBe("512 MB in use, 90 processes");
    expect(metricsText({ at: "", usedMemoryMb: 6144, processCount: 90, idle: false })).toBe("6.0 GB in use, 90 processes (CPU was busy)");
  });

  it("gives a delta only when both samples exist", () => {
    const run: HostLeanRunSummary = {
      source: "lean",
      at: "",
      scheduled: false,
      changed: 1,
      problems: [],
      before: { at: "", usedMemoryMb: 100, processCount: 10, idle: true },
    };
    expect(runDelta(run)).toBeNull();
    expect(runDelta({ ...run, after: { at: "", usedMemoryMb: 80, processCount: 12, idle: true } })).toEqual({ memory: "-20 MB", processes: "+2" });
  });
});
