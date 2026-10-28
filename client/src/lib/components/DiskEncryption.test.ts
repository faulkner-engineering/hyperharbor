import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import { fireEvent, render, screen, waitFor } from "@testing-library/svelte";

const invoke = vi.hoisted(() => vi.fn());
vi.mock("@tauri-apps/api/core", () => ({ invoke }));
vi.mock("@tauri-apps/api/event", () => ({ listen: vi.fn() }));

import AccountSection from "./AccountSection.svelte";
import BackupPanel from "./BackupPanel.svelte";
import DiskEncryptionDialog from "./DiskEncryptionDialog.svelte";
import { defaultWindows, MachineDraft } from "$lib/machineDraft.svelte";
import type { BackupRun, GuestVolumeEncryption, HostEntry, Vm, VmJob } from "$lib/api/client";

const host = (apiVersion = "1.37.0"): HostEntry => ({
  key: "mdns:host",
  displayName: "TC-PC",
  hostId: null,
  hostName: null,
  addresses: [],
  port: 48443,
  apiVersion,
  source: "discovered",
  isLocal: false,
  paired: true,
  canWake: false,
});

const vm: Vm = {
  id: "0b9a6f53-1c2d-4e8f-a1b2-3c4d5e6f7a8b",
  name: "Dev Box",
  state: "running",
  generation: 2,
  rdpAvailable: false,
  ipAddresses: [],
  provisioned: true,
  remoteDesktop: { address: null, reachableFromHost: false },
  guestOs: { family: "windows", name: "Windows 11 Pro" },
  performanceMode: false,
};

const volume = (state: GuestVolumeEncryption["state"], encryptedPercent: number): GuestVolumeEncryption => ({
  mountPoint: "C:",
  operatingSystem: true,
  state,
  encryptedPercent,
});

const turnOffJob = (volumes: GuestVolumeEncryption[], problems: string[] = []): VmJob => ({
  id: "7a6b5c4d-3e2f-4a1b-9c8d-7e6f5a4b3c2d",
  kind: "turnOffDiskEncryption",
  vmId: vm.id,
  state: "succeeded",
  step: "Done",
  percentComplete: 100,
  createdAt: "2026-10-28T12:00:00Z",
  updatedAt: "2026-10-28T12:00:05Z",
  error: null,
  diskEncryption: { vmId: vm.id, volumes, problems },
});

const calls = (command: string) => invoke.mock.calls.filter(([name]) => name === command);

beforeEach(() => {
  invoke.mockReset();
});

afterEach(() => {
  vi.useRealTimers();
});

describe("DiskEncryptionDialog", () => {
  it("turns BitLocker off and follows the decryption until nothing is encrypted", async () => {
    vi.useFakeTimers({ shouldAdvanceTime: true });
    const states = [[volume("encrypted", 100)], [volume("decrypted", 0)]];
    invoke.mockImplementation((command: string) => {
      if (command === "get_vm_disk_encryption") return Promise.resolve({ vmId: vm.id, volumes: states.shift() ?? [volume("decrypted", 0)], problems: [] });
      if (command === "turn_off_vm_disk_encryption") return Promise.resolve(turnOffJob([volume("decrypting", 64)]));
      return Promise.reject(new Error(`unexpected ${command}`));
    });
    render(DiskEncryptionDialog, { host: host(), vm, onclose: vi.fn() });

    await screen.findByText("C: (Windows) is encrypted with BitLocker");
    await fireEvent.click(screen.getByRole("button", { name: "Turn off encryption" }));

    await screen.findByText("C: (Windows) is decrypting, 64% still encrypted");
    expect(calls("turn_off_vm_disk_encryption")).toEqual([["turn_off_vm_disk_encryption", { key: "mdns:host", vmId: vm.id }]]);
    expect(screen.queryByRole("button", { name: "Turn off encryption" })).toBeNull();

    await vi.advanceTimersByTimeAsync(10_000);
    await screen.findByText("No volume is encrypted.");
    expect(calls("get_vm_disk_encryption")).toHaveLength(2);
  });

  it("shows the volumes that could not start decrypting", async () => {
    invoke.mockImplementation((command: string) => {
      if (command === "get_vm_disk_encryption") return Promise.resolve({ vmId: vm.id, volumes: [volume("encrypted", 100)], problems: [] });
      if (command === "turn_off_vm_disk_encryption")
        return Promise.resolve(turnOffJob([volume("encrypted", 100)], ["C: could not start decrypting (error 0x80310029)."]));
      return Promise.reject(new Error(`unexpected ${command}`));
    });
    render(DiskEncryptionDialog, { host: host(), vm, onclose: vi.fn() });

    await fireEvent.click(await screen.findByRole("button", { name: "Turn off encryption" }));

    await screen.findByText("C: could not start decrypting (error 0x80310029).");
  });

  it("closes at once while the host is still answering, and ignores the late answer", async () => {
    let answer: (value: unknown) => void = () => {};
    invoke.mockImplementation((command: string) =>
      command === "get_vm_disk_encryption" ? new Promise((resolve) => (answer = resolve)) : Promise.reject(new Error(`unexpected ${command}`)),
    );
    const onclose = vi.fn();
    render(DiskEncryptionDialog, { host: host(), vm, onclose });

    await screen.findByText(/Asking Windows in Dev Box/);
    await fireEvent.click(screen.getByRole("button", { name: "Close" }));
    expect(onclose).toHaveBeenCalledWith(false);

    answer({ vmId: vm.id, volumes: [volume("encrypted", 100)], problems: [] });
    await Promise.resolve();
    expect(screen.queryByRole("button", { name: "Turn off encryption" })).toBeNull();
  });

  it("says why it cannot read the guest", async () => {
    invoke.mockRejectedValue({ code: "api", message: "Dev Box has no stored administrator credential.", status: 409, problemCode: "vmNotProvisioned" });
    render(DiskEncryptionDialog, { host: host(), vm, onclose: vi.fn() });

    await screen.findByText(/no stored administrator credential/);
    expect(screen.queryByRole("button", { name: "Turn off encryption" })).toBeNull();
  });
});

describe("AccountSection device encryption", () => {
  const draft = () => new MachineDraft({ name: "Desk", os: "windows", account: { adminAccountName: "hhadmin", locale: "en-US", windows: defaultWindows() } });

  it("sends allowDeviceEncryption only while the box is ticked", async () => {
    const machine = draft();
    render(AccountSection, { host: host(), draft: machine });

    const box = screen.getByRole("checkbox", { name: /Let Windows encrypt the disk/ }) as HTMLInputElement;
    expect(box.checked).toBe(false);
    expect(machine.account?.windows).not.toHaveProperty("allowDeviceEncryption");

    await fireEvent.click(box);
    expect(machine.account?.windows?.allowDeviceEncryption).toBe(true);

    await fireEvent.click(box);
    expect(machine.account?.windows).not.toHaveProperty("allowDeviceEncryption");
  });

  it("is hidden for a host older than 1.37.0, which would reject the field", () => {
    render(AccountSection, { host: host("1.36.0"), draft: draft() });

    expect(screen.queryByRole("checkbox", { name: /Let Windows encrypt the disk/ })).toBeNull();
  });
});

describe("BackupPanel upload and encryption", () => {
  const run: BackupRun = {
    runId: "r1",
    item: `vm:${vm.id}`,
    targetId: "t1",
    trigger: "schedule",
    startedAt: "2026-10-28T02:00:00Z",
    finishedAt: new Date(Date.now() - 3 * 3600_000).toISOString(),
    outcome: "succeeded",
    totalBytes: 40 * 1024 ** 3,
    uploadedBytes: 1.5 * 1024 ** 3,
    encryptedDisks: 1,
    warnings: ["The guest encrypts its disks with BitLocker."],
    vmId: vm.id,
    vmName: vm.name,
  };

  it("shows what the last run uploaded, the encrypted disk, and the run's warnings", async () => {
    invoke.mockImplementation((command: string) => {
      switch (command) {
        case "list_backup_targets":
        case "list_backup_policies":
          return Promise.resolve([]);
        case "get_backup_status":
          return Promise.resolve({ running: false, waiting: 0, items: [{ item: run.item, targetId: "t1", targetName: "Local", overdue: false, vmId: vm.id, vmName: vm.name, lastRun: run }] });
        case "list_backup_history":
          return Promise.resolve([run]);
        default:
          return Promise.reject(new Error(`unexpected ${command}`));
      }
    });
    render(BackupPanel, { host: host("1.19.0"), vms: [vm] });

    await screen.findByText(/Backed up 3 hours ago \(uploaded 1.5 GB of 40 GB\)/);
    expect(screen.getByText(/Its disk is encrypted, so its backups are larger/)).toBeTruthy();
    expect(screen.getByText("The guest encrypts its disks with BitLocker.")).toBeTruthy();
    await waitFor(() => expect(screen.getByText(/uploaded 1.5 GB of 40 GB/, { selector: ".history span" })).toBeTruthy());
  });
});
