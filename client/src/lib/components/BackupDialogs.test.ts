import { beforeEach, describe, expect, it, vi } from "vitest";
import { fireEvent, render, screen, waitFor } from "@testing-library/svelte";

const invoke = vi.hoisted(() => vi.fn());
vi.mock("@tauri-apps/api/core", () => ({ invoke }));
vi.mock("@tauri-apps/api/event", () => ({ listen: vi.fn() }));

import BackupBrowseDialog from "./BackupBrowseDialog.svelte";
import RecoveryKeyDialog from "./RecoveryKeyDialog.svelte";
import ReplaceDiskDialog from "./ReplaceDiskDialog.svelte";
import type { BackupTarget, HostEntry, Vm } from "$lib/api/client";

const host: HostEntry = {
  key: "mdns:host",
  displayName: "TC-PC",
  hostId: null,
  hostName: null,
  addresses: [],
  port: 48443,
  apiVersion: "1.18.0",
  source: "discovered",
  isLocal: false,
  paired: true,
  canWake: false,
};

const vm: Vm = {
  id: "0b9a6f53-1c2d-4e8f-a1b2-3c4d5e6f7a8b",
  name: "Dev Box",
  state: "off",
  generation: 2,
  rdpAvailable: false,
  ipAddresses: [],
  provisioned: false,
  remoteDesktop: { address: null, reachableFromHost: false },
  guestOs: { family: "windows", name: null },
  performanceMode: false,
};

const target = (keyState: "pendingKey" | "confirmed"): BackupTarget => ({
  id: "t1",
  name: "Local",
  kind: "local",
  enabled: true,
  keyState,
  schedule: { time: "02:00" },
  retention: { daily: 7, weekly: 4, monthly: 6 },
  conditions: { noActiveSession: true, notInGame: true },
  local: { path: "D:\\B" },
});

const job = (state: "running" | "succeeded") => ({
  id: "7a6b5c4d-3e2f-4a1b-9c8d-7e6f5a4b3c2d",
  kind: "replaceDisk",
  vmId: vm.id,
  state,
  step: "Swapping the disks",
  percentComplete: state === "succeeded" ? 100 : 50,
  createdAt: "2026-10-12T12:00:00Z",
  updatedAt: "2026-10-12T12:00:00Z",
  error: null,
});

beforeEach(() => {
  invoke.mockReset();
});

describe("RecoveryKeyDialog", () => {
  const offer = {
    setupId: "setup1",
    recoveryKey: "AAAA-BBBB-CCCC-DDDD-EEEE-FFFF-GGGG-HHHH",
    expiresAt: "2026-10-12T12:30:00Z",
  };

  it("shows the key once and confirms only after the box is ticked and the groups are typed", async () => {
    const confirmed = target("confirmed");
    invoke.mockImplementation((command: string) => {
      if (command === "create_recovery_key") return Promise.resolve(offer);
      if (command === "confirm_recovery_key") return Promise.resolve(confirmed);
      return Promise.reject(new Error(`unexpected ${command}`));
    });
    const onclose = vi.fn();
    render(RecoveryKeyDialog, { props: { host, target: target("pendingKey"), onclose } });

    expect((await screen.findByTestId("recovery-key")).textContent).toBe(offer.recoveryKey);
    const confirm = screen.getByRole("button", { name: "Confirm" });
    expect((confirm as HTMLButtonElement).disabled).toBe(true);

    await fireEvent.click(screen.getByLabelText(/I saved the recovery key/));
    await fireEvent.input(screen.getByLabelText(/last four groups/), { target: { value: "eeee ffff gggg hhhh" } });
    expect((confirm as HTMLButtonElement).disabled).toBe(false);
    await fireEvent.click(confirm);

    await waitFor(() => expect(onclose).toHaveBeenCalledWith(confirmed));
    expect(invoke).toHaveBeenCalledWith("confirm_recovery_key", {
      key: host.key,
      request: { setupId: "setup1", typedBack: "EEEE-FFFF-GGGG-HHHH" },
    });
  });

  it("asks for a new key when the offer expired", async () => {
    invoke.mockImplementation((command: string) => {
      if (command === "create_recovery_key") return Promise.resolve(offer);
      return Promise.reject({ code: "api", message: "expired", status: 409, problemCode: "recoverySetupExpired" });
    });
    render(RecoveryKeyDialog, { props: { host, target: target("pendingKey"), onclose: vi.fn() } });
    await screen.findByTestId("recovery-key");

    await fireEvent.click(screen.getByLabelText(/I saved the recovery key/));
    await fireEvent.input(screen.getByLabelText(/last four groups/), { target: { value: "EEEEFFFFGGGGHHHH" } });
    await fireEvent.click(screen.getByRole("button", { name: "Confirm" }));

    expect(await screen.findByText("The recovery key expired. Get a new one.")).toBeTruthy();
    expect(screen.queryByTestId("recovery-key")).toBeNull();
  });
});

describe("ReplaceDiskDialog", () => {
  const catalog = {
    vms: [{ vmId: vm.id, name: "Dev Box", snapshots: [{ id: "k1", startTime: "2026-10-12T02:00:00Z", totalSize: 1024 }] }],
    hostSnapshots: [],
  };

  it("refuses a running VM", () => {
    render(ReplaceDiskDialog, { props: { host, vm: { ...vm, state: "running" }, targets: [target("confirmed")], onclose: vi.fn() } });

    expect(screen.getByText(/Turn Dev Box off first/)).toBeTruthy();
    expect(screen.queryByRole("button", { name: "Look for backups" })).toBeNull();
  });

  it("replaces only after the exact name is typed", async () => {
    invoke.mockImplementation((command: string) => {
      if (command === "scan_backups") return Promise.resolve(catalog);
      if (command === "replace_vm_disk") return Promise.resolve(job("succeeded"));
      return Promise.reject(new Error(`unexpected ${command}`));
    });
    render(ReplaceDiskDialog, { props: { host, vm, targets: [target("confirmed")], onclose: vi.fn() } });

    await fireEvent.click(screen.getByRole("button", { name: "Look for backups" }));
    const replace = await screen.findByRole("button", { name: "Replace the disk" });
    expect((replace as HTMLButtonElement).disabled).toBe(true);

    const input = screen.getByLabelText(/Type the VM's name/);
    await fireEvent.input(input, { target: { value: "dev box" } });
    expect((replace as HTMLButtonElement).disabled).toBe(true);
    await fireEvent.input(input, { target: { value: "Dev Box" } });
    expect((replace as HTMLButtonElement).disabled).toBe(false);
    await fireEvent.click(replace);

    await waitFor(() =>
      expect(invoke).toHaveBeenCalledWith("replace_vm_disk", {
        key: host.key,
        vmId: vm.id,
        request: { targetId: "t1", snapshotId: "k1", typedName: "Dev Box" },
      }),
    );
    expect(await screen.findByText("The disk was replaced.")).toBeTruthy();
  });

  it("says when the target holds nothing for this VM", async () => {
    invoke.mockResolvedValue({ vms: [], hostSnapshots: [] });
    render(ReplaceDiskDialog, { props: { host, vm, targets: [target("confirmed")], onclose: vi.fn() } });

    await fireEvent.click(screen.getByRole("button", { name: "Look for backups" }));

    expect(await screen.findByText(/holds no backup of Dev Box/)).toBeTruthy();
  });
});

describe("BackupBrowseDialog", () => {
  const session = (state: "preparing" | "ready") => ({
    id: "abc123",
    state,
    step: state === "ready" ? "Ready" : "Restoring the snapshot's files.",
    volumes: state === "ready" ? [{ drive: "E:", label: "Windows", sizeBytes: 1024 }] : [],
    expiresAt: "2026-10-12T12:30:00Z",
    vmName: "Dev Box",
  });

  it("waits for the disks, lists the root, opens a folder, saves a file, and closes the session", async () => {
    invoke.mockImplementation((command: string, args: { request?: { path?: string } }) => {
      if (command === "open_backup_browse") return Promise.resolve(session("preparing"));
      if (command === "get_backup_browse") return Promise.resolve(session("ready"));
      if (command === "list_backup_browse") {
        return Promise.resolve(
          args.request?.path
            ? [{ name: "notes.txt", isDirectory: false, size: 5 }]
            : [{ name: "Docs", isDirectory: true, size: 0 }],
        );
      }
      if (command === "download_backup_browse_file") return Promise.resolve("C:\\Users\\me\\notes.txt");
      if (command === "close_backup_browse") return Promise.resolve(undefined);
      return Promise.reject(new Error(`unexpected ${command}`));
    });
    const onclose = vi.fn();
    render(BackupBrowseDialog, { props: { host, targetId: "t1", snapshotId: "k1", vmName: "Dev Box", onclose } });

    expect(await screen.findByText(/Restoring the snapshot/)).toBeTruthy();
    const folder = await screen.findByRole("button", { name: /Docs/ }, { timeout: 5000 });
    await fireEvent.click(folder);
    await fireEvent.click(await screen.findByRole("button", { name: "Save…" }));
    expect(await screen.findByText(/Saved notes.txt to/)).toBeTruthy();
    expect(invoke).toHaveBeenCalledWith("download_backup_browse_file", { key: host.key, sessionId: "abc123", volume: "E:", path: "Docs\\notes.txt" });

    await fireEvent.click(screen.getByRole("button", { name: "Close backup" }));
    await waitFor(() => expect(onclose).toHaveBeenCalled());
    expect(invoke).toHaveBeenCalledWith("close_backup_browse", { key: host.key, sessionId: "abc123" });
  });

  it("shows why a backup could not be opened", async () => {
    invoke.mockImplementation((command: string) => {
      if (command === "open_backup_browse") return Promise.reject({ code: "api", message: "A backup is already open for browsing. Close it first.", status: 409 });
      return Promise.reject(new Error(`unexpected ${command}`));
    });
    render(BackupBrowseDialog, { props: { host, targetId: "t1", snapshotId: "k1", vmName: "Dev Box", onclose: vi.fn() } });

    expect(await screen.findByText(/already open for browsing/)).toBeTruthy();
  });
});
