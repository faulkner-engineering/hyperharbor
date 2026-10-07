import { beforeEach, describe, expect, it, vi } from "vitest";
import { fireEvent, render, screen, waitFor, within } from "@testing-library/svelte";
import { flushSync } from "svelte";
import HostWindow from "./HostWindow.svelte";
import PinWindow from "./PinWindow.svelte";
import type { HostLeanPlanSummary, HostLeanStatus, HostMessage, HostUpdateStatus, PageMessage, TrayViewState } from "./bridge";
import { tray } from "./store.svelte";

const sent: PageMessage[] = [];
let deliver: (message: HostMessage) => void = () => {};

const update = (overrides: Partial<HostUpdateStatus> = {}): HostUpdateStatus => ({
  supported: true,
  mode: "auto",
  channel: "stable",
  channels: ["beta", "stable"],
  maintenanceTime: "03:00",
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

const lean = (overrides: Partial<HostLeanStatus> = {}): HostLeanStatus => ({
  supported: true,
  busy: null,
  profileName: "Host gaming",
  applied: false,
  undoAvailable: false,
  scheduleEnabled: false,
  ...overrides,
});

const plan = (overrides: Partial<HostLeanPlanSummary> = {}): HostLeanPlanSummary => ({
  source: "lean",
  at: "2026-10-05T12:00:00Z",
  canApply: true,
  changes: [{ handler: "services", item: "DiagTrack", text: "Startup type Automatic to Disabled" }],
  kept: ["Spooler: a printer is installed"],
  problems: [],
  alreadyInPlace: 3,
  ...overrides,
});

const state = (overrides: Partial<TrayViewState> = {}): TrayViewState => ({
  connected: true,
  passphraseConfigured: true,
  devices: [{ id: "d1", name: "Laptop", fingerprint: "AB12CD34EF567890", pairedAt: "2026-10-05T12:00:00Z" }],
  vmFolder: { folder: "D:\\VMs", isDefault: false },
  isoFolder: "D:\\ISOs",
  backupFolder: "C:\\Backups",
  consoleReady: false,
  packageSearchReady: true,
  update: update(),
  hostLean: lean(),
  busy: [],
  pairing: null,
  dataDirectory: "C:\\ProgramData\\HyperHarbor",
  ...overrides,
});

/** The tray sends a new state, as it does after every change. */
function push(value: TrayViewState) {
  deliver({ type: "state", value });
  flushSync();
}

beforeEach(() => {
  sent.length = 0;
  tray.reset();
  tray.start(
    (message) => sent.push(message),
    (handler) => (deliver = handler),
  );
});

describe("host window", () => {
  it("shows placeholders until the tray reports, then the service state", () => {
    render(HostWindow);
    expect(document.querySelector("[aria-busy=true]")).toBeTruthy();

    push(state({ connected: false, passphraseConfigured: null }));

    expect(screen.getByText("Service not running")).toBeTruthy();
    expect(screen.getByText(/service is not running\. Start it/)).toBeTruthy();
    expect(screen.getByText("Unknown while the service is not running.")).toBeTruthy();
  });

  it("asks before setting up console access and shows it in progress until the tray finishes", async () => {
    render(HostWindow);
    push(state());

    await fireEvent.click(screen.getByRole("button", { name: "Set up…" }));
    const dialog = screen.getByRole("dialog", { name: "Set up console access" });
    await fireEvent.click(within(dialog).getByRole("button", { name: "Set up" }));

    expect(sent).toContainEqual({ type: "setUpConsole" });
    // In progress at once, before the tray answers.
    expect(screen.getByText(/Setting up… approve the administrator prompt/)).toBeTruthy();

    push(state({ busy: ["console"] }));
    expect(screen.getByText(/Setting up…/)).toBeTruthy();
    push(state({ consoleReady: true }));
    expect(screen.getByRole("button", { name: "Set up again…" })).toBeTruthy();
  });

  it("checks the passphrase, sends it once, and closes when the tray has saved it", async () => {
    render(HostWindow);
    push(state());
    deliver({ type: "navigate", page: "passphrase" });
    flushSync();

    const dialog = screen.getByRole("dialog", { name: "Change the admin passphrase" });
    const [first, second] = within(dialog).getAllByDisplayValue("");
    await fireEvent.input(first, { target: { value: "short" } });
    await fireEvent.input(second, { target: { value: "short" } });
    await fireEvent.click(within(dialog).getByRole("button", { name: "Save" }));
    expect(screen.getByRole("alert").textContent).toMatch(/at least 8/);
    expect(sent.filter((message) => message.type === "setPassphrase")).toHaveLength(0);

    await fireEvent.input(first, { target: { value: "correct horse battery" } });
    await fireEvent.input(second, { target: { value: "correct horse battery" } });
    await fireEvent.click(within(dialog).getByRole("button", { name: "Save" }));
    expect(sent).toContainEqual({ type: "setPassphrase", passphrase: "correct horse battery" });
    expect((first as HTMLInputElement).value).toBe("");
    expect(within(dialog).getByRole("button", { name: /Saving/ })).toBeTruthy();

    push(state({ busy: ["passphrase"] }));
    push(state());
    // It fades out.
    await waitFor(() => expect(screen.queryByRole("dialog")).toBeNull());
  });

  it("removes a device after confirming", async () => {
    render(HostWindow);
    push(state());
    await fireEvent.click(screen.getByRole("button", { name: /Devices/ }));

    await fireEvent.click(screen.getByRole("button", { name: "Remove…" }));
    await fireEvent.click(within(screen.getByRole("dialog", { name: "Remove Laptop?" })).getByRole("button", { name: "Remove" }));

    expect(sent).toContainEqual({ type: "removeDevice", deviceId: "d1" });
    expect(screen.getByRole("button", { name: /Removing/ })).toBeTruthy();
  });

  it("checks for updates, shows the steps while it works, and offers Install when a version is ready", async () => {
    render(HostWindow);
    push(state());
    await fireEvent.click(screen.getByRole("button", { name: /Updates/ }));
    expect(screen.queryByRole("list", { name: "Update progress" })).toBeNull();

    await fireEvent.click(screen.getByRole("button", { name: "Check now" }));
    expect(sent).toContainEqual({ type: "checkUpdate" });
    expect(screen.getByRole("button", { name: /Checking/ })).toBeTruthy();
    expect(screen.getByRole("list", { name: "Update progress" })).toBeTruthy();

    push(state({ update: update({ activity: "ready", availableVersion: "0.1.5" }) }));
    await fireEvent.click(screen.getByRole("button", { name: "Install 0.1.5 now" }));
    expect(sent).toContainEqual({ type: "installUpdate" });
  });

  it("shows how far the download has come while a version is prepared", async () => {
    render(HostWindow);
    push(
      state({
        update: update({
          activity: "preparing",
          availableVersion: "0.1.5",
          progress: { step: "downloading", bytesDone: 50 * 1024 * 1024, bytesTotal: 200 * 1024 * 1024 },
        }),
      }),
    );
    await fireEvent.click(screen.getByRole("button", { name: /Updates/ }));

    expect(screen.getByRole("progressbar").getAttribute("aria-valuenow")).toBe("25");
    expect(screen.getByText("Downloading: 50 of 200 MB (25%)")).toBeTruthy();
    expect(screen.getByRole("button", { name: "Check now" }).hasAttribute("disabled")).toBe(true);
  });

  it("applies only after a dry run, and undoes through its own dry run", async () => {
    render(HostWindow);
    push(state());
    await fireEvent.click(screen.getByRole("button", { name: /Lean host/ }));
    expect(screen.getByRole("button", { name: "Apply" }).hasAttribute("disabled")).toBe(true);
    expect(screen.queryByRole("button", { name: /Undo last apply/ })).toBeNull();

    await fireEvent.click(screen.getByRole("button", { name: "Dry run" }));
    expect(sent).toContainEqual({ type: "hostLeanDryRun", source: "lean" });

    push(state({ hostLean: lean({ dryRun: plan(), undoAvailable: true, applied: true }) }));
    await fireEvent.click(screen.getByText(/Services/));
    expect(screen.getByText("DiagTrack")).toBeTruthy();
    expect(screen.getByText(/Spooler: a printer is installed/)).toBeTruthy();
    await fireEvent.click(screen.getByRole("button", { name: "Apply" }));
    expect(sent).toContainEqual({ type: "hostLeanApply", source: "lean" });

    await fireEvent.click(screen.getByRole("button", { name: /Undo last apply/ }));
    expect(sent).toContainEqual({ type: "hostLeanDryRun", source: "undo" });
    push(state({ hostLean: lean({ dryRun: plan({ source: "undo" }), undoAvailable: true, applied: true }) }));
    await fireEvent.click(screen.getByRole("button", { name: "Apply undo" }));
    expect(sent).toContainEqual({ type: "hostLeanApply", source: "undo" });
  });

  it("will not apply a stale dry run, and says why Lean host is unavailable", async () => {
    render(HostWindow);
    push(state({ hostLean: lean({ dryRun: plan({ canApply: false }) }) }));
    await fireEvent.click(screen.getByRole("button", { name: /Lean host/ }));
    expect(screen.getByRole("button", { name: "Apply" }).hasAttribute("disabled")).toBe(true);

    push(state({ hostLean: lean({ supported: false, unsupportedReason: "Run the installed service." }) }));
    expect(screen.getByText("Run the installed service.")).toBeTruthy();
    expect(screen.queryByRole("button", { name: "Dry run" })).toBeNull();
  });

  it("shows the last run's before and after, and sets the schedule", async () => {
    render(HostWindow);
    push(
      state({
        hostLean: lean({
          applied: true,
          scheduleEnabled: true,
          lastRun: {
            source: "lean",
            at: "2026-10-05T12:00:00Z",
            scheduled: false,
            changed: 7,
            problems: [],
            before: { at: "2026-10-05T12:00:00Z", usedMemoryMb: 6144, processCount: 210, idle: true },
            after: { at: "2026-10-05T12:01:00Z", usedMemoryMb: 5120, processCount: 180, idle: true },
          },
        }),
      }),
    );
    await fireEvent.click(screen.getByRole("button", { name: /Lean host/ }));
    expect(screen.getByText("-1024 MB memory, -30 processes")).toBeTruthy();

    await fireEvent.click(screen.getByRole("checkbox", { name: "Re-apply every month" }));
    expect(sent).toContainEqual({ type: "setHostLeanSchedule", enabled: false });
  });

  it("shows the tray's toasts", () => {
    render(HostWindow);
    push(state());

    deliver({ type: "toast", kind: "error", title: "The folder was not changed", text: "Access is denied." });
    flushSync();

    expect(screen.getByRole("alert").textContent).toMatch(/Access is denied/);
  });
});

describe("PIN window", () => {
  it("shows the PIN with the time left and cancels the request", async () => {
    vi.useFakeTimers({ toFake: ["Date"] });
    vi.setSystemTime(new Date("2026-10-05T12:00:00Z"));
    try {
      render(PinWindow);
      push(state({ pairing: { pairingId: "p1", deviceName: "Laptop", pin: "440141", expiresAt: "2026-10-05T12:02:00Z" } }));

      expect(screen.getByText("440 141")).toBeTruthy();
      expect(screen.getByRole("timer").getAttribute("aria-label")).toBe("Expires in 2:00");

      await fireEvent.click(screen.getByRole("button", { name: "Cancel" }));
      expect(sent).toContainEqual({ type: "cancelPairing", pairingId: "p1" });
    } finally {
      vi.useRealTimers();
    }
  });
});
