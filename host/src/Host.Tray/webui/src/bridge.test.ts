import { describe, expect, it } from "vitest";
import fixtures from "./bridge.fixtures.json";
import type { HostMessage, TrayViewState } from "./bridge";

// Typed samples: TypeScript checks them against bridge.ts, and this test checks that they have exactly the shape the
// tray sends (bridge.fixtures.json, written by TrayWindowTests). A field added on one side and not the other fails.
const fullState: TrayViewState = {
  connected: true,
  passphraseConfigured: true,
  devices: [{ id: "id", name: "Laptop", fingerprint: "AB12", pairedAt: "2026-10-05T12:00:00Z" }],
  vmFolder: { folder: "D:\\VMs", isDefault: false },
  isoFolder: "D:\\ISOs",
  backupFolder: "C:\\Backups",
  consoleReady: true,
  packageSearchReady: false,
  update: {
    supported: true,
    mode: "auto",
    channel: "stable",
    channels: ["beta", "stable"],
    maintenanceTime: "03:00",
    currentVersion: "0.1.4",
    availableVersion: "0.1.5",
    notesUrl: "https://github.com/",
    activity: "preparing",
    lastCheck: "2026-10-05T12:00:00Z",
    message: "Ready.",
    lastResult: "Updated.",
    rolledBack: [],
    progress: { step: "downloading", bytesDone: 1, bytesTotal: 2 },
  },
  hostLean: {
    supported: true,
    unsupportedReason: "Run the installed service to apply changes.",
    busy: "dryRun",
    profileName: "Host gaming",
    applied: true,
    undoAvailable: true,
    scheduleEnabled: true,
    nextScheduled: "2026-11-04T12:00:00Z",
    dryRun: {
      source: "lean",
      at: "2026-10-05T12:00:00Z",
      canApply: true,
      changes: [{ handler: "services", item: "DiagTrack", text: "Startup type Automatic to Disabled" }],
      kept: ["Spooler: a printer is installed"],
      problems: ["Unknown service: Foo"],
      alreadyInPlace: 12,
    },
    lastRun: {
      source: "lean",
      at: "2026-10-05T12:00:00Z",
      scheduled: false,
      changed: 7,
      problems: ["Widgets: access denied"],
      before: { at: "2026-10-05T12:00:00Z", usedMemoryMb: 6144, processCount: 210, idle: true },
      after: { at: "2026-10-05T12:01:00Z", usedMemoryMb: 5120, processCount: 180, idle: false },
      restorePoint: "Restore point 12 and registry export",
    },
  },
  gameReady: {
    readAt: "2026-10-05T12:00:00Z",
    busy: "fix:controllerWake",
    error: "Approve the administrator prompt on this PC, then check again.",
    cards: [
      {
        id: "controllerWake",
        title: "Wake from controller",
        state: "attention",
        summary: "1 device can wake the PC once it is armed.",
        checks: [{ id: "wiredAdapter", title: "Wired network connection", status: "pass", detail: "Connected through Ethernet.", canFix: false }],
        controllers: [
          { id: "receiver:wired:045e:02fe", name: "Xbox Wireless Adapter for Windows", kind: "receiver", connection: "usb", wake: "canArm" },
        ],
        hints: ["Test wake puts this PC to sleep."],
        canFix: true,
        canTestWake: false,
      },
      { id: "sleepPolicy", title: "Sleep policy", state: "comingSoon", summary: "Soon.", checks: [], controllers: [], hints: [], canFix: false, canTestWake: false },
    ],
    wakeTest: {
      state: "done",
      resumedAt: "2026-10-05T12:00:00Z",
      outcome: "controller",
      source: "Xbox Wireless Adapter for Windows",
      message: "Xbox Wireless Adapter for Windows woke the PC.",
    },
    ambient: {
      enabled: true,
      idleMinutes: 5,
      showHostStatus: true,
      showRunningVms: true,
      showDevices: false,
      softLock: true,
      chord: "lb+rb+start",
      supported: true,
      screensaverPath: "C:/Program Files/HyperHarbor/HyperHarbor Ambient.scr",
      screensaver: { active: true, path: "C:/Program Files/HyperHarbor/HyperHarbor Ambient.scr", timeoutSeconds: 300, policyLocked: false },
      cpuPercent: 0.4,
      gpuPercent: 0,
      measuredSeconds: 60,
      cecDevice: "Pulse-Eight USB-CEC Adapter",
    },
  },
  backup: {
    configured: true,
    pendingKeys: 1,
    running: false,
    waiting: 2,
    items: [
      {
        name: "Dev Box",
        targetName: "Local",
        outcome: "failed",
        finishedAt: "2026-10-12T09:10:00Z",
        reason: "The disk is full.",
        lastSuccessAt: "2026-10-10T09:10:00Z",
        overdue: true,
        runId: "7c1e2d3f-4a5b-4c6d-8e7f-9a0b1c2d3e4f",
      },
    ],
  },
  gameAware: {
    settings: {
      enabled: true,
      defaultAction: "pause",
      allowlist: ["emulator"],
      ignoreList: ["powerpnt"],
      vmPolicies: [{ vmId: "0b9a6f53-1c2d-4e8f-a1b2-3c4d5e6f7a8b", action: "keep", alwaysPause: true }],
    },
    state: {
      enabled: true,
      gaming: true,
      process: "eldenring",
      reason: "A full-screen Direct3D application is running.",
      since: "2026-10-03T12:00:00Z",
      changedVms: ["Dev Box"],
      sessionVms: ["Work"],
    },
    vms: [{ vmId: "0b9a6f53-1c2d-4e8f-a1b2-3c4d5e6f7a8b", name: "Dev Box", gpuPartition: false }],
  },
  restoreWizard: {
    phase: "ready",
    step: "Restoring the files",
    percent: 40,
    targetName: "Old backups",
    vms: [{ vmId: "0b9a6f53-1c2d-4e8f-a1b2-3c4d5e6f7a8b", name: "Dev Box", snapshots: [{ id: "k4f8a1b2", startTime: "2026-10-03T12:00:00Z", totalSize: 64000000000 }] }],
    hostSnapshots: [{ id: "k9a8b7c6", startTime: "2026-10-03T12:00:00Z", totalSize: 1000000 }],
    messages: ["Dev Box was restored."],
    restartRequired: true,
  },
  busy: ["folder:iso"],
  pairing: { pairingId: "id", deviceName: "Laptop", pin: "440141", expiresAt: "2026-10-05T12:02:00Z" },
  dataDirectory: "C:\\ProgramData\\HyperHarbor",
};

const samples: HostMessage[] = [
  { type: "state", value: fullState },
  {
    type: "state",
    value: {
      ...fullState,
      connected: false,
      passphraseConfigured: null,
      devices: [],
      vmFolder: null,
      isoFolder: null,
      backupFolder: null,
      consoleReady: false,
      update: null,
      hostLean: null,
      gameReady: null,
      backup: null,
      gameAware: null,
      restoreWizard: null,
      busy: [],
      pairing: null,
    },
  },
  { type: "toast", kind: "success", title: "Saved", text: "Done." },
  { type: "navigate", page: "devices" },
];

/** The shape of a value: object keys (sorted, recursively), array element shapes, and null versus a value. */
function shape(value: unknown): unknown {
  if (value === null) return null;
  if (Array.isArray(value)) return value.length === 0 ? [] : [shape(value[0])];
  if (typeof value === "object") {
    return Object.fromEntries(
      Object.keys(value as object)
        .sort()
        .map((key) => [key, shape((value as Record<string, unknown>)[key])]),
    );
  }
  return typeof value;
}

describe("bridge", () => {
  it("matches what the tray sends", () => {
    expect(samples.map(shape)).toEqual(fixtures.map(shape));
  });

  it("knows every update activity and mode the tray sends", () => {
    const update = (fixtures[0] as { value: TrayViewState }).value.update!;
    expect(["idle", "checking", "preparing", "ready", "installing"]).toContain(update.activity);
    expect(["auto", "notify", "off"]).toContain(update.mode);
  });
});
