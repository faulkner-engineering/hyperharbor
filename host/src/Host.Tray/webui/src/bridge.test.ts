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
