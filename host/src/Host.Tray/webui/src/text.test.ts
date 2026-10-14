import { describe, expect, it } from "vitest";
import type { HostTailscale, RestoreWizardStatus, GameAwareInfo, GameAwareSettings, HostLeanRunSummary, HostLeanStatus, HostUpdateStatus, TrayBackupItem, TrayBackupStatus } from "./bridge";
import { tailscaleStatus, restoreStatus, snapshotLabel, addPrograms, backupCardText, effectiveAction, gameAwareStatus, normalizeProgram, withVmAction, countdown, deviceCount, formatPin, groupChanges, leanSummary, metricsText, passphraseProblem, progressText, runDelta, shortFingerprint, signed, updateSteps, updateSummary } from "./text";

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

describe("backupCardText", () => {
  const now = new Date("2026-10-12T12:00:00Z").getTime();
  const item = (overrides: Partial<TrayBackupItem> = {}): TrayBackupItem => ({
    name: "Dev",
    targetName: "Local",
    outcome: "succeeded",
    overdue: false,
    lastSuccessAt: "2026-10-12T09:00:00Z",
    ...overrides,
  });
  const status = (overrides: Partial<TrayBackupStatus> = {}): TrayBackupStatus => ({
    configured: true,
    pendingKeys: 0,
    running: false,
    waiting: 0,
    items: [item()],
    ...overrides,
  });

  it("is unknown without the service", () => {
    expect(backupCardText(null, now).tone).toBe("neutral");
  });

  it("asks to set up when nothing is configured, and mentions a waiting key", () => {
    const text = backupCardText(status({ configured: false, items: [], pendingKeys: 1 }), now);

    expect(text.tone).toBe("warn");
    expect(text.lines[0]).toContain("recovery key");
  });

  it("is up to date with the age of the last good backup", () => {
    const text = backupCardText(status(), now);

    expect(text).toEqual({ status: "Up to date", tone: "ok", lines: ["Last good backup 3 hours ago."] });
  });

  it("needs attention for a failure, with its reason, and for an overdue item", () => {
    const failed = backupCardText(status({ items: [item({ outcome: "failed", reason: "The disk is full." })] }), now);
    const overdue = backupCardText(status({ items: [item({ overdue: true })] }), now);

    expect(failed.status).toBe("Needs attention");
    expect(failed.lines).toEqual(["Dev to Local failed: The disk is full."]);
    expect(overdue.lines).toEqual(["Dev has no good backup on Local for a while."]);
  });

  it("shows a run in progress", () => {
    expect(backupCardText(status({ running: true }), now).tone).toBe("busy");
  });
});

describe("game-aware helpers", () => {
  const settings = (overrides: Partial<GameAwareSettings> = {}): GameAwareSettings => ({
    enabled: true,
    defaultAction: "pause",
    allowlist: [],
    ignoreList: [],
    vmPolicies: [],
    ...overrides,
  });
  const info = (overrides: Partial<GameAwareSettings> = {}, gaming = false): GameAwareInfo => ({
    settings: settings(overrides),
    state: { enabled: true, gaming, process: gaming ? "eldenring" : null, changedVms: [], sessionVms: [] },
    vms: [
      { vmId: "a", name: "Plain", gpuPartition: false },
      { vmId: "g", name: "Gpu", gpuPartition: true },
    ],
  });

  it("normalizes program names and merges pasted lists", () => {
    expect(normalizeProgram(" C:\\Games\\Foo.EXE ")).toBe("foo");
    expect(addPrograms(["foo"], "Foo.exe, bar;\nBaz.exe\n\n")).toEqual(["foo", "bar", "baz"]);
  });

  it("stores a VM's own action and drops it when it matches the default again", () => {
    const changed = withVmAction(settings(), "a", "save", false);
    expect(changed.vmPolicies).toEqual([{ vmId: "a", action: "save", alwaysPause: false }]);
    expect(withVmAction(changed, "a", "pause", false).vmPolicies).toEqual([]);
  });

  it("forces pause for a GPU VM", () => {
    const stored = info({ vmPolicies: [{ vmId: "g", action: "keep", alwaysPause: false }] });
    expect(effectiveAction(stored, "g")).toEqual({ action: "pause", alwaysPause: false, forced: true });
  });

  it("describes the mode", () => {
    expect(gameAwareStatus(null).tone).toBe("neutral");
    expect(gameAwareStatus(info({ enabled: false }))).toEqual({ status: "Off", tone: "neutral" });
    expect(gameAwareStatus(info())).toEqual({ status: "On, no game running", tone: "ok" });
    expect(gameAwareStatus(info({}, true))).toEqual({ status: "Game running (eldenring)", tone: "busy" });
  });
});

describe("restore page text", () => {
  const wizard = (overrides: Partial<RestoreWizardStatus> = {}): RestoreWizardStatus => ({
    phase: "idle", step: "", percent: 0, error: null, targetName: null, vms: [], hostSnapshots: [], messages: [], restartRequired: false, ...overrides,
  });

  it("labels a snapshot with its size", () => {
    expect(snapshotLabel({ startTime: "2026-10-11T02:00:00Z", totalSize: 64 * 1024 ** 3 })).toMatch(/\(64\.0 GB\)$/);
    expect(snapshotLabel({ startTime: "2026-10-11T02:00:00Z", totalSize: 5 * 1024 ** 2 })).toMatch(/\(5 MB\)$/);
  });

  it("describes each phase", () => {
    expect(restoreStatus(null).tone).toBe("neutral");
    expect(restoreStatus(wizard())).toEqual({ status: "Enter where your backups are", tone: "neutral" });
    expect(restoreStatus(wizard({ error: "Wrong key." }))).toEqual({ status: "The backups could not be opened", tone: "warn" });
    expect(restoreStatus(wizard({ phase: "attaching" })).tone).toBe("busy");
    expect(restoreStatus(wizard({ phase: "restoring" })).tone).toBe("busy");
    expect(restoreStatus(wizard({ phase: "ready" }))).toEqual({ status: "Choose what to restore", tone: "ok" });
    expect(restoreStatus(wizard({ phase: "ready", error: "x" })).tone).toBe("warn");
  });
});

describe("tailscale card text", () => {
  const info = (overrides: Partial<HostTailscale> = {}): HostTailscale => ({
    state: "running",
    ipv4: "100.64.0.1",
    dnsName: "pc.tail1.ts.net",
    tagged: true,
    tag: "tag:hyperharbor-host",
    upCommand: "tailscale up --advertise-tags=tag:hyperharbor-host",
    config: { localApiPort: 41112 },
    ...overrides,
  });

  it("treats a missing Tailscale as optional, not as a failure", () => {
    expect(tailscaleStatus(info({ state: "notInstalled" }))).toEqual({ status: "Not installed (optional)", tone: "neutral" });
    expect(tailscaleStatus(null).tone).toBe("neutral");
  });

  it("asks for attention when it is installed but not usable", () => {
    for (const state of ["unreachable", "needsLogin", "stopped"] as const) {
      expect(tailscaleStatus(info({ state })).tone).toBe("warn");
    }
  });

  it("names the host on the tailnet, and notes a missing tag", () => {
    expect(tailscaleStatus(info())).toEqual({ status: "On the tailnet as pc.tail1.ts.net", tone: "ok" });
    expect(tailscaleStatus(info({ tagged: false }))).toEqual({ status: "On the tailnet, not tagged", tone: "warn" });
  });
});
